using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Attention;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Ordering;
using Tetragon.Domain.Sourcing;

namespace Tetragon.Application.Services;

/// <summary>
/// 지금 코드가 이미 알고 있는 사실을 주의 원장에 싣는다 (확장 08 §1.1 "현재 코드" 행).
///
/// 09~12가 만들 사실(역마진·위너 상실·정산 미매칭)은 아직 없지만,
/// <b>사람이 봐야 하는 사실은 지금도 네 군데에 흩어져 있다</b> —
/// 금고 잔액, 공급처 조치가 남은 CS 티켓, 죽은 수집 작업, 차단된 상품.
/// 화면을 네 개 돌아다녀야 알 수 있고, 그래서 아무도 안 본다.
///
/// 이 스캔은 <see cref="AttentionKinds"/> 규약을 그대로 따른다 — 특히
/// <b>실측이 없는 항목의 영향 금액은 0으로 두고 그 사실을 근거 문장에 적는다.</b>
/// 상수를 지어내면 그 상수가 목록의 순서를 정하게 되고, 순서가 곧 운영자의 하루가 된다.
/// 진짜 금액은 09(성과)·10(정산)이 실측을 만든 뒤에 채운다.
/// </summary>
public sealed class AttentionScan(
    AttentionLedger ledger,
    IAttentionRepository repo,
    IWalletRepository wallets,
    ICsTicketRepository tickets,
    IOrderRepository orders,
    IScrapeJobRepository jobs,
    IProductRepository products,
    ILogger<AttentionScan> log)
{
    /// <summary>죽은 작업·차단 상품은 수천 건일 수 있다. 스캔이 통째로 도는 것을 막는 상한.</summary>
    private const int ScanLimit = 200;

    /// <summary><see cref="ICsTicketRepository.SearchAsync"/>가 한 번에 돌려주는 최대치.</summary>
    private const int TicketPageSize = 500;

    /// <summary>죽은 작업은 오래된 것까지 다 올리면 목록이 과거로 채워진다. 최근 것만 본다.</summary>
    private static readonly TimeSpan DeadJobWindow = TimeSpan.FromDays(7);

    public async Task<AttentionScanResult> RunAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var groups = new List<FactGroup>
        {
            await WalletAsync(ct),
            await SupplierActionAsync(ct),
            await DeadJobsAsync(notes, ct),
            await BlockedProductsAsync(notes, ct),
        };

        var result = await ledger.OpenManyAsync([.. groups.SelectMany(g => g.Drafts)], ct);

        // 사실이 사라진 항목을 닫는다. 사람이 손대지 않아도 닫히는 항목이 있어야 목록이 신뢰를 얻는다.
        var closed = 0;
        foreach (var group in groups)
        {
            // 상한에 걸려 일부만 훑었다면 닫지 않는다 —
            // 못 본 것을 "사라졌다"고 닫으면 살아 있는 사실을 조용히 지우게 된다.
            if (!group.Complete)
            {
                notes.Add($"{group.Kind}: 일부만 확인해 자동 닫기를 건너뜁니다.");
                continue;
            }

            var live = group.Drafts.Select(d => d.DedupKey).ToHashSet();
            foreach (var item in await repo.ActiveByKindAsync(group.Kind, ct))
            {
                if (live.Contains(item.DedupKey)) continue;
                item.ResolveAuto(group.CloseNote);
                closed++;
            }
        }
        if (closed > 0) await repo.SaveAsync(ct);

        log.LogInformation(
            "주의 스캔 — 신규 {Opened}건, 갱신 {Touched}건, 접힘 {Folded}건, 자동 닫힘 {Closed}건",
            result.Opened, result.Touched, result.Folded, closed);

        return new AttentionScanResult(result.Opened, result.Touched, result.Folded, closed, notes);
    }

    /// <summary>
    /// 금고가 마르면 발주가 멈추고, 발주가 멈추면 미출고 페널티가 쌓인다.
    /// 영향 금액은 "얼마를 채워야 경고가 사라지는가" — 유일하게 실측으로 답할 수 있는 숫자다.
    /// </summary>
    private async Task<FactGroup> WalletAsync(CancellationToken ct)
    {
        var wallet = await wallets.GetOrCreateAsync(ct);
        var group = new FactGroup(AttentionKinds.WalletLow, "잔액이 경고선 위로 올라와 자동으로 닫혔습니다.");
        if (!wallet.IsLow) return group; // 초안이 비어 있으면 열려 있던 항목이 자동으로 닫힌다

        var shortfall = wallet.LowBalanceThreshold - wallet.Available;
        group.Drafts.Add(new AttentionDraft
        {
            Kind = AttentionKinds.WalletLow,
            SubjectType = "system",
            SubjectId = wallet.Id.ToString(),
            DedupKey = $"wallet:{wallet.Id}",
            Title = $"금고 잔액 부족 — 가용 {wallet.Available:N0}원",
            ImpactKrw = shortfall,
            ImpactBasis =
                $"경고선 {wallet.LowBalanceThreshold:N0}원 − 가용 {wallet.Available:N0}원 " +
                $"(잔액 {wallet.Balance:N0} − 예약 {wallet.Reserved:N0}) = 부족 {shortfall:N0}원. " +
                "잔액이 마르면 자동 발주가 멈추고 미출고 페널티가 쌓입니다",
            Severity = wallet.Available <= 0 ? AttentionSeverity.Critical : AttentionSeverity.Warn,
            SuggestedAction = AttentionActions.None,
        });
        return group;
    }

    /// <summary>
    /// 발주가 나간 뒤의 취소·반품인데 공급처 조치를 안 했다 —
    /// 방치하면 <b>우리 돈으로 산 물건이 구매자에게 그대로 간다.</b>
    /// 여기 영향 금액은 추정이 아니라 이미 결제한 금액이다.
    /// </summary>
    private async Task<FactGroup> SupplierActionAsync(CancellationToken ct)
    {
        var open = await tickets.SearchAsync(nameof(CsStatus.Open), null, ct);
        var group = new FactGroup(AttentionKinds.CsSupplierAction, "티켓이 정리되어 자동으로 닫혔습니다.")
        {
            Complete = open.Count < TicketPageSize,
        };

        foreach (var ticket in open.Where(t => t.SupplierActionRequired && !t.SupplierActionDone))
        {
            var order = ticket.OrderId is null ? null : await orders.FindAsync(ticket.OrderId.Value, ct);
            var paid = order?.SupplierPaidAmount?.Amount
                       ?? (order?.SupplierUnitCost?.Amount ?? 0m) * Math.Max(ticket.Quantity, 1);

            group.Drafts.Add(new AttentionDraft
            {
                Kind = AttentionKinds.CsSupplierAction,
                SubjectType = "order",
                SubjectId = (ticket.OrderId ?? ticket.Id).ToString(),
                DedupKey = $"cs:{ticket.Id}",
                Title = $"공급처 취소 필요 — {ticket.ProductName ?? ticket.MarketOrderId}",
                ImpactKrw = paid,
                ImpactBasis = paid > 0
                    ? $"공급처 발주 {order?.SupplierOrderNo ?? "번호 미상"} 결제액 {paid:N0}원. " +
                      "공급처를 취소하지 않으면 이 금액만큼 물건이 그대로 출고됩니다"
                    : "발주 결제액이 기록돼 있지 않아 0으로 둡니다 — 공급처 주문 내역에서 직접 확인하세요",
                Severity = AttentionSeverity.Critical,
                SuggestedAction = AttentionActions.Verify,
                Detail = new Dictionary<string, string>
                {
                    ["marketOrderId"] = ticket.MarketOrderId,
                    ["kind"] = ticket.Kind.ToString(),
                    ["supplierOrderNo"] = order?.SupplierOrderNo ?? "",
                    ["supplierUrl"] = order?.SupplierUrl ?? "",
                },
            });
        }
        return group;
    }

    /// <summary>재시도를 다 쓰고 죽은 수집 작업. 정보성이라 영향 금액은 0이다.</summary>
    private async Task<FactGroup> DeadJobsAsync(List<string> notes, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - DeadJobWindow;
        var recent = await jobs.RecentAsync(ScanLimit * 2, ct);
        var dead = recent.Where(j => j.State == JobState.DeadLettered && j.UpdatedAt >= since).ToList();

        // 창(7일) 밖으로 밀려나면 목록에서 내린다. "사라졌다"가 아니라 "오래됐다"이므로 문구를 구분한다.
        var group = new FactGroup(AttentionKinds.WorkDead, $"{DeadJobWindow.Days}일이 지나 목록에서 내렸습니다.");

        if (dead.Count > ScanLimit)
        {
            notes.Add($"죽은 작업 {dead.Count}건 중 최근 {ScanLimit}건만 실었습니다.");
            group.Complete = false;
            dead = [.. dead.Take(ScanLimit)];
        }

        group.Drafts.AddRange(dead.Select(job => new AttentionDraft
        {
            Kind = AttentionKinds.WorkDead,
            SubjectType = "work_item",
            SubjectId = job.Id.ToString(),
            DedupKey = $"job:{job.Id}",
            Title = $"수집 실패 — {Trim(job.Url, 60)}",
            ImpactKrw = 0m,
            ImpactBasis = "정보성 항목. 수집이 죽었을 때의 손실은 아직 측정할 수 없습니다 " +
                          "(등록 슬롯당 기대이익 실측은 09·10에서 생깁니다)",
            Severity = AttentionSeverity.Info,
            SuggestedAction = AttentionActions.None,
            Detail = new Dictionary<string, string>
            {
                ["url"] = job.Url,
                ["stage"] = job.Stage.ToString(),
                ["attempts"] = job.Attempts.ToString(),
                ["lastError"] = job.LastError ?? "",
            },
        }));
        return group;
    }

    /// <summary>
    /// 금지어·규제로 등록이 막힌 상품.
    /// 영향 금액을 0으로 두는 것이 이 스캔에서 가장 중요한 판단이다 —
    /// "카테고리 평균 월이익 × 막힌 상품 수"(08 §1.4)를 지금 계산하려면 카테고리 평균 월이익을
    /// 지어내야 하는데, 그 상수가 곧 목록의 순서가 되고 순서가 곧 운영자의 하루가 된다.
    /// </summary>
    private async Task<FactGroup> BlockedProductsAsync(List<string> notes, CancellationToken ct)
    {
        var (blocked, total) = await products.SearchAsync(
            nameof(ProductStatus.Blocked), null, 1, ScanLimit, ct);

        var group = new FactGroup(AttentionKinds.RegulationBlocked, "차단이 풀려 자동으로 닫혔습니다.");
        if (total > blocked.Count)
        {
            notes.Add($"차단된 상품 {total}건 중 최근 {blocked.Count}건만 실었습니다.");
            group.Complete = false;
        }

        group.Drafts.AddRange(blocked.Select(product => new AttentionDraft
        {
            Kind = AttentionKinds.RegulationBlocked,
            SubjectType = "product",
            SubjectId = product.Id.ToString(),
            DedupKey = $"product:{product.Id}",
            Title = $"등록 차단 — {Trim(product.Name.GetOrFirst("ko-KR"), 60)}",
            ImpactKrw = 0m,
            ImpactBasis = "카테고리 평균 월이익 실측이 아직 없어 0으로 둡니다. " +
                          "10(정산 대사)이 확정이익을 만든 뒤 재산정합니다",
            Severity = AttentionSeverity.Warn,
            SuggestedAction = AttentionActions.Verify,
            Detail = new Dictionary<string, string>
            {
                ["supplier"] = product.Source.SupplierCode,
                ["sourceUrl"] = product.Source.Url,
            },
        }));
        return group;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// 한 kind의 스캔 결과.
    /// <paramref name="Complete"/>가 핵심이다 — 상한에 걸려 일부만 봤다면 자동 닫기를 하면 안 된다.
    /// 못 본 것을 "사라졌다"고 닫으면 살아 있는 사실이 조용히 지워진다.
    /// </summary>
    private sealed record FactGroup(string Kind, string CloseNote)
    {
        public List<AttentionDraft> Drafts { get; } = [];
        public bool Complete { get; set; } = true;
    }
}

/// <param name="Notes">상한에 걸려 싣지 못한 것 — 조용히 자르지 않고 화면에 그대로 보여준다.</param>
public sealed record AttentionScanResult(
    int Opened, int Touched, int Folded, int AutoClosed, IReadOnlyList<string> Notes);

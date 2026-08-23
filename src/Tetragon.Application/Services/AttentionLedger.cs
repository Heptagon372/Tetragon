using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Attention;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// 주의 원장 (v2 확장 08 §1) — 사람이 봐야 하는 사실을 한 곳에 모으고 줄을 세운다.
///
/// 이 서비스가 지키는 규약은 셋이다.
///   ① <see cref="AttentionDraft.ImpactKrw"/>·<see cref="AttentionDraft.ImpactBasis"/>가 required —
///      근거를 못 적는 항목은 애초에 사람 시간을 쓸 가치가 없다. 컴파일러가 이걸 강제한다.
///   ② 하루에 새로 열리는 항목에 상한이 있다 (<see cref="AttentionPolicy.DailyOpenCap"/>).
///      상한을 넘으면 조용히 버리지 않고 집계 항목 한 건으로 접는다.
///   ③ 같은 사실은 항목 하나다 — 매일 재검출돼도 seen_count만 오른다.
///
/// 신규 기능(09 성과·10 정산·11 소싱·12 콘텐츠)은 전부 여기에 쓴다.
/// 각자 알림을 보내면 1인 운영자는 알림 피로로 전부 무시하게 되기 때문이다.
/// </summary>
public sealed class AttentionLedger(
    IAttentionRepository repo,
    AttentionPolicy policy,
    ILogger<AttentionLedger> log)
{
    /// <summary>
    /// 사실을 기록한다. 이미 열려 있으면 항목을 새로 만들지 않고 last_seen_at·seen_count만 올린다.
    /// 오늘 상한을 이미 채웠으면 집계 항목으로 접어서 반환한다.
    /// </summary>
    public async Task<AttentionItem> OpenAsync(AttentionDraft draft, CancellationToken ct)
    {
        var existing = await repo.FindActiveAsync(draft.Kind, draft.DedupKey, ct);
        if (existing is not null)
        {
            existing.Touch(draft.ImpactKrw, draft.ImpactBasis, draft.Title, draft.Severity);
            await repo.SaveAsync(ct);
            return existing;
        }

        var openedToday = await repo.CountOpenedSinceAsync(draft.Kind, policy.DayStart(), ct);
        if (openedToday >= policy.DailyOpenCap)
        {
            var folded = await FoldAsync(draft.Kind, [draft], ct);
            await repo.SaveAsync(ct);
            log.LogInformation(
                "주의 항목 상한 초과 — kind={Kind} 오늘 {Opened}건, 집계 항목으로 접음", draft.Kind, openedToday);
            return folded!;
        }

        var item = Materialize(draft);
        await repo.AddAsync(item, ct);
        await repo.SaveAsync(ct);
        return item;
    }

    /// <summary>
    /// 여러 사실을 한 번에 기록한다 — 성과·정산 배치가 쓰는 경로.
    ///
    /// 상한을 넘길 때 <b>영향 금액 상위부터</b> 여는 것이 핵심이다.
    /// 한 건씩 <see cref="OpenAsync"/>로 넣으면 먼저 온 순서대로 상한을 먹어서
    /// 정작 큰 손실이 집계 항목에 묻힌다.
    /// </summary>
    public async Task<AttentionOpenResult> OpenManyAsync(
        IReadOnlyCollection<AttentionDraft> drafts, CancellationToken ct)
    {
        int touched = 0, opened = 0, folded = 0;

        foreach (var group in drafts.GroupBy(d => d.Kind))
        {
            var fresh = new List<AttentionDraft>();
            foreach (var draft in group)
            {
                var existing = await repo.FindActiveAsync(draft.Kind, draft.DedupKey, ct);
                if (existing is null) { fresh.Add(draft); continue; }
                existing.Touch(draft.ImpactKrw, draft.ImpactBasis, draft.Title, draft.Severity);
                touched++;
            }

            var used = await repo.CountOpenedSinceAsync(group.Key, policy.DayStart(), ct);
            var room = Math.Max(0, policy.DailyOpenCap - used);
            var ranked = fresh.OrderByDescending(d => d.ImpactKrw).ToList();

            foreach (var draft in ranked.Take(room))
            {
                await repo.AddAsync(Materialize(draft), ct);
                opened++;
            }

            var overflow = ranked.Skip(room).ToList();
            if (overflow.Count > 0)
            {
                await FoldAsync(group.Key, overflow, ct);
                folded += overflow.Count;
            }
        }

        await repo.SaveAsync(ct);
        if (folded > 0)
            log.LogInformation("주의 항목 {Opened}건 열고 {Folded}건은 집계 항목으로 접음", opened, folded);

        return new AttentionOpenResult(opened, touched, folded);
    }

    /// <summary>
    /// 사실이 사라졌다 — 사람이 손대지 않아도 닫는다.
    /// 저절로 닫히는 항목이 있어야 목록이 신뢰를 얻는다.
    /// </summary>
    public async Task<bool> ResolveAutoAsync(string kind, string dedupKey, string note, CancellationToken ct)
    {
        var item = await repo.FindActiveAsync(kind, dedupKey, ct);
        if (item is null) return false;
        item.ResolveAuto(note);
        await repo.SaveAsync(ct);
        return true;
    }

    /// <summary>
    /// 이미 열려 있는 항목이 상한을 넘겼으면 하위 초과분을 집계 항목으로 접는다.
    ///
    /// <see cref="OpenManyAsync"/>가 상한을 사전에 지키므로 평시엔 0을 반환한다.
    /// 이 메서드는 상한을 낮췄거나 여러 배치가 각자 상한을 먹은 뒤의 <b>사후 정리</b>용이다.
    /// </summary>
    public async Task<int> FoldOverflowAsync(string kind, CancellationToken ct)
    {
        if (kind == AttentionKinds.Folded) return 0; // 집계 항목을 다시 접지 않는다

        var active = await repo.ActiveByKindAsync(kind, ct);
        if (active.Count <= policy.DailyOpenCap) return 0;

        var overflow = active
            .OrderByDescending(i => i.ImpactKrw)
            .Skip(policy.DailyOpenCap)
            .ToList();

        var drafts = overflow.Select(i => new AttentionDraft
        {
            Kind = i.Kind,
            SubjectType = i.SubjectType,
            SubjectId = i.SubjectId,
            DedupKey = i.DedupKey,
            Title = i.Title,
            ImpactKrw = i.ImpactKrw,
            ImpactBasis = i.ImpactBasis,
            Severity = i.Severity,
        }).ToList();

        var fold = await FoldAsync(kind, drafts, ct);
        foreach (var item in overflow) item.FoldInto(fold!.Title);

        await repo.SaveAsync(ct);
        log.LogInformation("kind={Kind} 초과 {Count}건을 집계 항목으로 접음", kind, overflow.Count);
        return overflow.Count;
    }

    // ── 사람이 하는 조치 ────────────────────────────────────────────────

    public Task<bool> SnoozeAsync(Guid id, int days, CancellationToken ct) =>
        ApplyAsync(id, item => item.Snooze(days), ct);

    public Task<bool> DoneAsync(Guid id, string? note, CancellationToken ct) =>
        ApplyAsync(id, item => item.Done(note), ct);

    public Task<bool> DismissAsync(Guid id, string? note, CancellationToken ct) =>
        ApplyAsync(id, item => item.Dismiss(note), ct);

    /// <summary>일괄 처리 — 집계 항목이 가리키는 218건을 한 번에 닫는 경로.</summary>
    public async Task<int> BulkAsync(
        IReadOnlyCollection<Guid> ids, AttentionBulkAction action, int snoozeDays, string? note,
        CancellationToken ct)
    {
        var items = await repo.FindManyAsync(ids, ct);
        var applied = 0;
        foreach (var item in items)
        {
            if (!item.IsActive) continue;
            switch (action)
            {
                case AttentionBulkAction.Snooze: item.Snooze(snoozeDays); break;
                case AttentionBulkAction.Done: item.Done(note); break;
                case AttentionBulkAction.Dismiss: item.Dismiss(note); break;
            }
            applied++;
        }
        await repo.SaveAsync(ct);
        return applied;
    }

    private async Task<bool> ApplyAsync(Guid id, Action<AttentionItem> change, CancellationToken ct)
    {
        var item = await repo.FindAsync(id, ct);
        if (item is null) return false;
        change(item);
        await repo.SaveAsync(ct);
        return true;
    }

    // ── 내부 ───────────────────────────────────────────────────────────

    private static AttentionItem Materialize(AttentionDraft draft) => AttentionItem.Open(
        Tenant.Default, draft.Kind, draft.SubjectType, draft.SubjectId, draft.DedupKey,
        draft.Title, draft.ImpactKrw, draft.ImpactBasis,
        draft.Severity, draft.SuggestedAction, draft.Detail);

    /// <summary>
    /// 초과분을 하루 한 건의 집계 항목에 누적한다.
    /// 개별 항목을 조용히 버리지 않고 "역마진 상품 218건 (합계 월 -84만원)"처럼 한 줄로 보여준다.
    /// </summary>
    private async Task<AttentionItem?> FoldAsync(
        string kind, IReadOnlyCollection<AttentionDraft> overflow, CancellationToken ct)
    {
        if (overflow.Count == 0) return null;

        var dedupKey = $"{kind}:{policy.DayStamp()}";
        var existing = await repo.FindActiveAsync(AttentionKinds.Folded, dedupKey, ct);

        var count = overflow.Count + (existing is null ? 0 : ReadInt(existing.Detail, "count"));
        var impact = overflow.Sum(d => d.ImpactKrw) + (existing?.ImpactKrw ?? 0m);
        var title = $"{KindLabel(kind)} {count:N0}건 (합계 월 {impact:N0}원) — 일괄 처리 필요";
        var basis = $"일일 상한 {policy.DailyOpenCap}건을 넘어 접힌 {count:N0}건의 영향 금액 합계";

        if (existing is not null)
        {
            existing.Touch(impact, basis, title, AttentionSeverity.Warn);
            existing.Detail["count"] = count.ToString();
            return existing;
        }

        var fold = AttentionItem.Open(
            Tenant.Default, AttentionKinds.Folded, "system", kind, dedupKey,
            title, impact, basis, AttentionSeverity.Warn, AttentionActions.None,
            new Dictionary<string, string> { ["foldedKind"] = kind, ["count"] = count.ToString() });
        await repo.AddAsync(fold, ct);
        return fold;
    }

    private static int ReadInt(Dictionary<string, string> detail, string key) =>
        detail.TryGetValue(key, out var raw) && int.TryParse(raw, out var value) ? value : 0;

    /// <summary>집계 항목 제목에 쓰는 사람말. 모르는 kind는 코드 그대로 보여준다.</summary>
    private static string KindLabel(string kind) => kind switch
    {
        AttentionKinds.MarginLoss => "역마진 상품",
        AttentionKinds.MarginThin => "마진 미달 상품",
        AttentionKinds.WinnerLost => "위너 상실",
        AttentionKinds.PerfDead => "성과 없는 리스팅",
        AttentionKinds.SettleUnmatched => "미매칭 정산 라인",
        AttentionKinds.SettleGap => "이익 차이",
        AttentionKinds.SagaDisputed => "발주 분쟁",
        AttentionKinds.WorkDead => "죽은 작업",
        AttentionKinds.SourceCandidate => "소싱 후보",
        AttentionKinds.ContentRejected => "콘텐츠 반려",
        AttentionKinds.RegulationBlocked => "규제로 막힌 상품",
        _ => kind,
    };
}

/// <summary>
/// 주의 원장 정책. 상한을 낮추면 목록이 짧아지고 집계 항목이 늘어난다 — 항목이 사라지지는 않는다.
/// </summary>
public sealed class AttentionPolicy
{
    /// <summary>하루에 새로 열 수 있는 항목 수 (kind별).</summary>
    public int DailyOpenCap { get; set; } = 30;

    /// <summary>
    /// 하루의 경계는 KST다. 서버가 UTC로 돌아도 운영자는 한국 시간으로 하루를 센다 —
    /// UTC 자정으로 자르면 오전 9시에 상한이 초기화된다.
    /// </summary>
    public static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);

    public DateTimeOffset DayStart()
    {
        var kstNow = DateTimeOffset.UtcNow.ToOffset(KstOffset);
        return new DateTimeOffset(kstNow.Date, KstOffset);
    }

    public string DayStamp() => DateTimeOffset.UtcNow.ToOffset(KstOffset).ToString("yyyy-MM-dd");
}

/// <summary>
/// 사실 한 건의 초안. <see cref="ImpactKrw"/>·<see cref="ImpactBasis"/>가 required인 것이 설계의 핵심이다 —
/// 근거 없는 알림을 만들 수 없게 컴파일러가 막는다 (08 §1.3 ①).
/// </summary>
public sealed record AttentionDraft
{
    public required string Kind { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required string DedupKey { get; init; }
    public required string Title { get; init; }
    /// <summary>★ 필수. 0이어도 명시해야 한다 — "정보성"이라는 판단 자체를 기록으로 남긴다.</summary>
    public required decimal ImpactKrw { get; init; }
    /// <summary>★ 필수. 그 숫자가 어떻게 나왔는지 사람이 읽을 문장.</summary>
    public required string ImpactBasis { get; init; }
    public AttentionSeverity Severity { get; init; } = AttentionSeverity.Warn;
    public string? SuggestedAction { get; init; }
    public Dictionary<string, string> Detail { get; init; } = [];
}

public sealed record AttentionOpenResult(int Opened, int Touched, int Folded);

public enum AttentionBulkAction { Snooze, Done, Dismiss }

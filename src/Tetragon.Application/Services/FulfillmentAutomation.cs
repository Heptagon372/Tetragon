using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Ordering;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// 주문 이행 자동화 — 구매자 주문부터 마켓 송장 반영까지 한 바퀴.
///
/// <code>
///  구매자 주문 → [1.수집] → [2.공급처 연결] → [3.자동 발주] → 공급사 출고
///                                                    ↓
///                             [5.CS 수집] ← 취소·반품   [4.송장 마켓 반영]
/// </code>
///
/// 단계마다 실패해도 다음 단계로 넘어간다. 한 주문의 발주가 막혔다고
/// 다른 주문의 송장 반영까지 멈추면 안 되기 때문이다.
///
/// <b>돈에 관한 원칙</b>
///   - 정책이 꺼져 있으면 아무것도 하지 않는다
///   - 시험 모드에서는 "무엇을 했을 것인지"만 기록하고 결제하지 않는다
///   - 1건 한도·하루 한도를 넘으면 자동으로 사지 않고 사람에게 남긴다
///   - 연속 실패가 쌓이면 스스로 멈춘다
/// </summary>
public sealed class FulfillmentAutomation(
    IAutomationPolicyRepository policies,
    IOrderRepository orders,
    ICsTicketRepository csTickets,
    IListingRepository listings,
    IProductRepository products,
    IMarketplaceAdapterRegistry markets,
    ICredentialStore credentials,
    SupplierPurchaseService purchases,
    ILogger<FulfillmentAutomation> logger)
{
    /// <summary>주문 수집 조회 기간. 놓친 주문이 없도록 넉넉히 잡는다.</summary>
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(3);

    public async Task<AutomationRun> RunAsync(bool manualTrigger, CancellationToken ct)
    {
        var policy = await policies.GetOrCreateAsync(ct);

        // 수동 실행은 스위치가 꺼져 있어도 한 번 돌려볼 수 있게 한다 (시험 목적).
        // 단, 정책의 시험 모드/한도는 그대로 지킨다.
        if (!manualTrigger && !policy.CanRun)
            return AutomationRun.Skipped(policy.HaltedReason ?? "자동화가 꺼져 있습니다.");

        var run = new AutomationRun { DryRun = policy.DryRun, StartedAt = DateTimeOffset.UtcNow };

        try
        {
            if (policy.AutoCollectOrders) await CollectOrdersAsync(run, ct);
            await LinkOrdersToSuppliersAsync(run, ct);
            if (policy.AutoPurchase) await PurchaseAsync(policy, run, ct);
            if (policy.AutoUploadTracking) await UploadTrackingAsync(run, ct);
            if (policy.AutoCollectCs) await CollectCsAsync(run, ct);

            run.FinishedAt = DateTimeOffset.UtcNow;

            // 단계에서 잡아 담은 오류가 있으면 실패로 센다 — 조용히 넘어가면 안 된다
            if (run.Errors.Count > 0) policy.RecordFailure(run.Summary);
            else policy.RecordSuccess(run.Summary);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "자동 이행 실행 중 예외");
            run.Errors.Add($"실행 중단: {ex.Message}");
            run.FinishedAt = DateTimeOffset.UtcNow;
            policy.RecordFailure(ex.Message);
        }

        await policies.SaveAsync(ct);
        run.HaltedReason = policy.HaltedReason;
        return run;
    }

    // ── 1. 마켓 주문 수집 ────────────────────────────────────────────────

    private async Task CollectOrdersAsync(AutomationRun run, CancellationToken ct)
    {
        var range = new DateRange(DateTimeOffset.UtcNow - LookBack, DateTimeOffset.UtcNow);

        foreach (var adapter in markets.All.Where(a => a.IsAvailable))
        {
            try
            {
                var credential = await credentials.GetAsync($"market:{adapter.Code}", ct);
                var marketOrders = await adapter.FetchOrdersAsync(range, credential, ct);

                foreach (var mo in marketOrders)
                {
                    var existing = await orders.FindByMarketOrderAsync(mo.MarketCode, mo.MarketOrderId, ct);
                    if (existing is not null) continue;

                    await orders.AddAsync(Order.Import(
                        Tenant.Default, mo.MarketCode, mo.MarketOrderId, mo.MarketItemId,
                        mo.ProductName, mo.OptionName, mo.Quantity, mo.PaidAmount,
                        mo.OrdererName, mo.OrderedAt,
                        mo.ShipTo?.ReceiverName, mo.ShipTo?.Phone,
                        mo.ShipTo?.Zipcode, mo.ShipTo?.FullAddress, mo.ShipTo?.Message,
                        mo.ShipmentBoxId, mo.VendorItemId), ct);
                    run.OrdersCollected++;
                }
                await orders.SaveAsync(ct);
            }
            catch (NotSupportedException ex)
            {
                // 이 마켓은 애초에 주문 API가 없다(옥션·G마켓은 엑셀 경로).
                // 이것을 실패로 세면 정상 상태인데도 연속 실패가 쌓여 자동화가 스스로 멈춘다.
                run.Unsupported.Add($"{adapter.Code} 주문 수집: {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Market} 주문 수집 실패", adapter.Code);
                run.Errors.Add($"{adapter.Code} 주문 수집 실패: {ex.Message}");
            }
        }
    }

    // ── 2. 주문 → 공급처 연결 ────────────────────────────────────────────

    /// <summary>
    /// 마켓 상품번호로 우리 Listing을 역추적해 "어디서 사야 하는지"를 붙인다.
    /// 이게 안 되면 발주 자체가 불가능하다.
    /// </summary>
    private async Task LinkOrdersToSuppliersAsync(AutomationRun run, CancellationToken ct)
    {
        var pending = (await orders.SearchAsync(nameof(OrderStatus.Imported), ct))
            .Where(o => o.ProductId is null && o.MarketItemId is { Length: > 0 })
            .ToList();

        foreach (var order in pending)
        {
            try
            {
                var listing = await listings.FindByMarketItemAsync(order.MarketCode, order.MarketItemId!, ct);
                if (listing is null) continue;

                var product = await products.FindAsync(listing.ProductId, ct);
                if (product is null) continue;

                order.LinkSource(
                    product.Id,
                    product.Source.SupplierCode,
                    product.Source.SourceProductId,
                    product.Source.Url,
                    product.BasePrice);
                run.OrdersLinked++;
            }
            catch (Exception ex)
            {
                run.Errors.Add($"주문 {order.MarketOrderId} 공급처 연결 실패: {ex.Message}");
            }
        }

        if (run.OrdersLinked > 0) await orders.SaveAsync(ct);
    }

    // ── 3. 자동 발주 ─────────────────────────────────────────────────────

    private async Task PurchaseAsync(AutomationPolicy policy, AutomationRun run, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var pending = (await orders.SearchAsync(nameof(OrderStatus.Imported), ct))
            .Where(o => o.ProductId is not null)
            .OrderBy(o => o.OrderedAt)
            .ToList();

        foreach (var order in pending)
        {
            if (ct.IsCancellationRequested) break;

            // 무엇이 막고 있는지 먼저 본다 (돈은 아직 안 나간다)
            var preflight = await purchases.PreflightAsync(order.Id, ct);
            var amount = preflight.EstimatedAmount;

            if (policy.RejectPurchase(amount, today) is { } rejection)
            {
                order.NoteAutomation($"자동 발주 보류 — {rejection}");
                run.PurchaseSkipped.Add($"{order.MarketOrderId}: {rejection}");
                continue;
            }

            if (!preflight.CanAutoOrder)
            {
                var reason = preflight.Blockers.Count > 0
                    ? string.Join(" / ", preflight.Blockers)
                    : $"{preflight.CapabilityReason} {preflight.CapabilityHowToEnable}".Trim();
                order.NoteAutomation($"자동 발주 불가 — {reason}");
                run.PurchaseSkipped.Add($"{order.MarketOrderId}: {reason}");
                continue;
            }

            // 시험 모드: 여기까지 통과했다는 것만 기록하고 결제하지 않는다
            if (policy.DryRun)
            {
                order.NoteAutomation($"[시험] 실제 운영이면 {amount:N0}원으로 발주했을 주문입니다.");
                run.WouldPurchase.Add($"{order.MarketOrderId}: {order.ProductName} x{order.Quantity} → {amount:N0}원");
                continue;
            }

            var outcome = await purchases.PurchaseAsync(order.Id, ct);
            if (outcome.Success)
            {
                policy.RecordSpend(outcome.PaidAmount ?? amount, today);
                order.NoteAutomation($"자동 발주 완료 · 공급처 주문번호 {outcome.SupplierOrderNo}");
                run.Purchased++;
                run.PurchasedAmount += outcome.PaidAmount ?? amount;
            }
            else
            {
                order.NoteAutomation($"자동 발주 실패 — {outcome.Message}");
                run.Errors.Add($"{order.MarketOrderId} 발주 실패: {outcome.Message}");
            }
        }

        await orders.SaveAsync(ct);
    }

    // ── 4. 송장을 마켓에 반영 ────────────────────────────────────────────

    /// <summary>
    /// 공급처가 준 송장을 마켓에 올린다.
    /// 이걸 빠뜨리면 물건은 갔는데 마켓은 '미출고'로 보고 페널티를 준다.
    /// </summary>
    private async Task UploadTrackingAsync(AutomationRun run, CancellationToken ct)
    {
        var shippable = (await orders.SearchAsync(null, ct))
            .Where(o => o.NeedsTrackingUpload)
            .ToList();

        foreach (var order in shippable)
        {
            var adapter = markets.Resolve(order.MarketCode);
            if (adapter is not IOrderFulfillmentProvider provider)
            {
                run.TrackingSkipped.Add($"{order.MarketOrderId}: {order.MarketCode}는 송장 자동 등록을 지원하지 않습니다.");
                continue;
            }

            try
            {
                var credential = await credentials.GetAsync($"market:{order.MarketCode}", ct);
                var result = await provider.UploadTrackingAsync(new TrackingUpload
                {
                    MarketOrderId = order.MarketOrderId,
                    ShipmentBoxId = order.ShipmentBoxId,
                    VendorItemId = order.VendorItemId,
                    TrackingNo = order.TrackingNo!,
                    DeliveryCompanyCode = order.DeliveryCompanyCode
                        ?? credential.Get("delivery_company_code") ?? "CJGLS",
                }, credential, ct);

                if (result.Success)
                {
                    order.MarkTrackingUploaded();
                    order.NoteAutomation($"송장 {order.TrackingNo} 마켓 반영 완료");
                    run.TrackingUploaded++;
                }
                else if (IsPermanentPerOrder(result.ErrorCode))
                {
                    // 이 주문에 마켓 식별자가 없다 — 다음 주기에 다시 시도해도 결과가 같다.
                    // 실패로 세면 정상 운영 중에도 연속 실패가 쌓여 자동화가 멈춘다.
                    order.MarkTrackingUploadFailed(result.ErrorMessage ?? "");
                    order.NoteAutomation($"송장 반영 불가 — {result.ErrorMessage}");
                    run.TrackingSkipped.Add($"{order.MarketOrderId}: {result.ErrorMessage}");
                }
                else
                {
                    order.MarkTrackingUploadFailed(result.ErrorMessage ?? "알 수 없는 오류");
                    run.Errors.Add($"{order.MarketOrderId} 송장 반영 실패: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                order.MarkTrackingUploadFailed(ex.Message);
                run.Errors.Add($"{order.MarketOrderId} 송장 반영 오류: {ex.Message}");
            }
        }

        await orders.SaveAsync(ct);
    }

    /// <summary>
    /// 재시도해도 결과가 같은 주문 단위 문제인지.
    /// 이런 건 실패로 세지 않는다 — 고칠 수 없는 주문 몇 개 때문에 자동화 전체가 멈추면 안 된다.
    /// </summary>
    private static bool IsPermanentPerOrder(string? errorCode) =>
        errorCode is "NO_SHIPMENT_BOX" or "NO_INVOICE";

    // ── 5. CS 요청 수집 ──────────────────────────────────────────────────

    private async Task CollectCsAsync(AutomationRun run, CancellationToken ct)
    {
        var range = new DateRange(DateTimeOffset.UtcNow - LookBack, DateTimeOffset.UtcNow);

        foreach (var adapter in markets.All.Where(a => a.IsAvailable))
        {
            if (adapter is not IOrderFulfillmentProvider provider) continue;

            try
            {
                var credential = await credentials.GetAsync($"market:{adapter.Code}", ct);
                var tickets = await provider.FetchCsTicketsAsync(range, credential, ct);

                foreach (var t in tickets)
                {
                    if (await csTickets.ExistsAsync(t.MarketCode, t.MarketTicketId, ct)) continue;

                    var order = await orders.FindByMarketOrderAsync(t.MarketCode, t.MarketOrderId, ct);

                    // 이미 공급처에 발주가 나간 뒤라면 공급처에도 조치해야 한다.
                    // 안 그러면 우리 돈으로 산 물건이 그대로 구매자에게 간다.
                    var supplierActionRequired = order is not null
                        && order.Status is not (OrderStatus.Imported or OrderStatus.Cancelled);

                    var kind = ParseKind(t.Kind);
                    await csTickets.AddAsync(CsTicket.Import(
                        Tenant.Default, t.MarketCode, t.MarketTicketId, t.MarketOrderId,
                        kind, t.ProductName ?? order?.ProductName, t.Reason, t.Quantity,
                        t.RequestedAt, order?.Id, supplierActionRequired), ct);

                    // 아직 발주 전인 취소는 우리가 사지 않도록 즉시 막는다 — 손해를 원천 차단한다
                    if (order is not null && kind == CsKind.Cancel && order.Status == OrderStatus.Imported)
                    {
                        order.Cancel($"구매자 취소 요청 — 자동 발주 대상에서 제외 ({t.Reason})");
                        run.CsAutoCancelled++;
                    }

                    run.CsCollected++;
                }

                await csTickets.SaveAsync(ct);
                await orders.SaveAsync(ct);
            }
            catch (NotSupportedException ex)
            {
                run.Unsupported.Add($"{adapter.Code} CS 수집: {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Market} CS 수집 실패", adapter.Code);
                run.Errors.Add($"{adapter.Code} CS 수집 실패: {ex.Message}");
            }
        }
    }

    private static CsKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "return" => CsKind.Return,
        "exchange" => CsKind.Exchange,
        _ => CsKind.Cancel,
    };
}

/// <summary>자동화 한 바퀴의 결과. 화면에 그대로 보여줄 수 있게 사람 말로 담는다.</summary>
public sealed class AutomationRun
{
    public bool DryRun { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public int OrdersCollected { get; set; }
    public int OrdersLinked { get; set; }
    public int Purchased { get; set; }
    public decimal PurchasedAmount { get; set; }
    public int TrackingUploaded { get; set; }
    public int CsCollected { get; set; }
    public int CsAutoCancelled { get; set; }

    /// <summary>시험 모드에서 "실제였다면 샀을" 목록.</summary>
    public List<string> WouldPurchase { get; } = [];
    public List<string> PurchaseSkipped { get; } = [];
    public List<string> TrackingSkipped { get; } = [];
    /// <summary>그 마켓이 원래 지원하지 않는 기능. 오류가 아니므로 실패로 세지 않는다.</summary>
    public List<string> Unsupported { get; } = [];
    public List<string> Errors { get; } = [];

    public string? HaltedReason { get; set; }
    public string? SkipReason { get; init; }

    public string Summary
    {
        get
        {
            if (SkipReason is { } skip) return skip;
            var parts = new List<string>();
            if (OrdersCollected > 0) parts.Add($"주문 {OrdersCollected}건 수집");
            if (OrdersLinked > 0) parts.Add($"공급처 연결 {OrdersLinked}건");
            if (DryRun && WouldPurchase.Count > 0) parts.Add($"[시험] 발주 대상 {WouldPurchase.Count}건");
            if (Purchased > 0) parts.Add($"발주 {Purchased}건 ({PurchasedAmount:N0}원)");
            if (PurchaseSkipped.Count > 0) parts.Add($"발주 보류 {PurchaseSkipped.Count}건");
            if (TrackingUploaded > 0) parts.Add($"송장 반영 {TrackingUploaded}건");
            if (CsCollected > 0) parts.Add($"CS {CsCollected}건 수집");
            if (CsAutoCancelled > 0) parts.Add($"취소 반영 {CsAutoCancelled}건");
            if (Errors.Count > 0) parts.Add($"오류 {Errors.Count}건");
            return parts.Count == 0 ? "처리할 것이 없었습니다." : string.Join(" · ", parts);
        }
    }

    public static AutomationRun Skipped(string reason) =>
        new() { SkipReason = reason, FinishedAt = DateTimeOffset.UtcNow };
}

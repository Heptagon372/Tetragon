using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Ordering;
using Tetragon.Domain.Treasury;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Services;

/// <summary>
/// 주문 → 공급처 발주 오케스트레이션.
///
/// 흐름:
///   구매자 → 쿠팡 주문 → (수집) → 여기 → 도매꾹 발주 → 공급사가 구매자에게 직배송
///
/// 돈이 실제로 나가는 경로이므로 순서를 엄격히 지킨다:
///   1. 발주 가능한지 확인 (상품·배송지·공급처 권한)
///   2. 금고에서 금액을 **예약**한다 (동시 발주로 잔액을 두 번 쓰는 것 방지)
///   3. 공급처에 발주한다
///   4. 성공 → 예약을 지출로 확정 / 실패 → 예약을 반드시 되돌린다
///
/// 4번을 빠뜨리면 실패한 발주가 잔액을 계속 묶어 둔다.
/// </summary>
public sealed class SupplierPurchaseService(
    IOrderRepository orders,
    IProductRepository products,
    IWalletRepository wallets,
    ISupplierOrderPluginRegistry orderPlugins,
    PurchaseOrderSheetBuilder sheetBuilder,
    ILogger<SupplierPurchaseService> logger)
{
    /// <summary>
    /// 발주 전 점검. 실제로 돈을 쓰기 전에 무엇이 막고 있는지 알려준다.
    /// UI가 '발주 가능/불가'를 표시하는 근거가 된다.
    /// </summary>
    public async Task<PurchasePreflight> PreflightAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderId, ct);
        if (order is null)
            return PurchasePreflight.Blocked("주문을 찾을 수 없습니다.");

        var sheet = await sheetBuilder.BuildAsync(orderId, ct);
        if (sheet is null)
            return PurchasePreflight.Blocked("발주서를 만들 수 없습니다.");

        var blockers = new List<string>();
        var warnings = new List<string>(sheet.Warnings);

        if (order.Status != OrderStatus.Imported)
            blockers.Add($"이미 처리된 주문입니다 (현재 상태: {order.Status}).");
        if (string.IsNullOrWhiteSpace(sheet.SupplierCode))
            blockers.Add("공급처를 알 수 없습니다 — 이 주문이 어느 상품에서 왔는지 연결되지 않았습니다.");
        if (string.IsNullOrWhiteSpace(sheet.ReceiverAddress))
            blockers.Add("구매자 배송지가 없습니다 — 공급처에 보낼 주소가 없으면 발주할 수 없습니다.");

        // 발주 예상액: 실제 결제될 금액을 모르면 예상 원가로 잡는다
        var estimated = sheet.EstimatedCost ?? 0m;
        if (estimated <= 0)
            warnings.Add("공급가를 알 수 없어 예상 금액이 0원입니다 — 금고 예약이 부정확할 수 있습니다.");

        // 금고 잔액
        var wallet = await wallets.GetOrCreateAsync(ct);
        var shortfall = Math.Max(estimated - wallet.Available, 0);
        if (shortfall > 0)
            blockers.Add(
                $"금고 잔액이 부족합니다. 필요 {estimated:N0}원 / 사용 가능 {wallet.Available:N0}원 " +
                $"(부족분 {shortfall:N0}원) — 금고에 충전한 뒤 다시 시도하세요.");
        else if (wallet.IsLow)
            warnings.Add(
                $"금고 잔액이 설정한 경고선({wallet.LowBalanceThreshold:N0}원) 아래입니다 " +
                $"— 현재 {wallet.Available:N0}원.");

        // 공급처 자동 발주 권한
        SupplierOrderCapability? capability = null;
        if (sheet.SupplierCode is { Length: > 0 } supplierCode)
        {
            var plugin = orderPlugins.Resolve(supplierCode);
            capability = plugin is null
                ? SupplierOrderCapability.Unavailable(
                    $"'{supplierCode}' 공급처는 자동 발주를 지원하지 않습니다.",
                    "아래 '공급사 사이트에서 주문하기'로 직접 주문한 뒤 주문번호를 입력하세요.")
                : await plugin.CheckCapabilityAsync(ct);
        }

        return new PurchasePreflight
        {
            OrderId = orderId,
            CanAutoOrder = blockers.Count == 0 && capability?.CanAutoOrder == true,
            Blockers = blockers,
            Warnings = warnings,
            EstimatedAmount = estimated,
            WalletAvailable = wallet.Available,
            WalletShortfall = shortfall,
            CapabilityReason = capability?.Reason,
            CapabilityHowToEnable = capability?.HowToEnable,
            SupplierUrl = sheet.SupplierUrl,
            Sheet = sheet,
        };
    }

    /// <summary>
    /// 실제 발주. <b>돈이 나간다.</b>
    /// 호출자는 반드시 사용자의 명시적 확인을 받은 뒤에 호출해야 한다.
    /// </summary>
    public async Task<PurchaseOutcome> PurchaseAsync(Guid orderId, CancellationToken ct)
    {
        var preflight = await PreflightAsync(orderId, ct);
        if (!preflight.CanAutoOrder)
        {
            var reason = preflight.Blockers.Count > 0
                ? string.Join(" / ", preflight.Blockers)
                : $"{preflight.CapabilityReason} {preflight.CapabilityHowToEnable}".Trim();
            return PurchaseOutcome.Rejected(reason, preflight);
        }

        var order = (await orders.FindAsync(orderId, ct))!;
        var sheet = preflight.Sheet!;
        var plugin = orderPlugins.Resolve(sheet.SupplierCode!)!;
        var wallet = await wallets.GetOrCreateAsync(ct);

        // ── 1. 금고 예약 ──────────────────────────────────────────────
        WalletTransaction reservation;
        try
        {
            reservation = wallet.Reserve(preflight.EstimatedAmount, orderId,
                $"발주 예약 · {sheet.ProductName} x{sheet.Quantity}");
            await wallets.SaveAsync(wallet, reservation, ct);
        }
        catch (InsufficientFundsException ex)
        {
            return PurchaseOutcome.Rejected(ex.Message, preflight);
        }

        // ── 2. 공급처 발주 ────────────────────────────────────────────
        SupplierOrderResult result;
        try
        {
            var product = order.ProductId is { } pid ? await products.FindAsync(pid, ct) : null;
            result = await plugin.PlaceOrderAsync(new Plugin.Abstractions.SupplierOrderRequest
            {
                SourceProductId = order.SupplierProductId ?? product?.Source.SourceProductId ?? "",
                ProductUrl = sheet.SupplierUrl ?? "",
                Quantity = order.Quantity,
                OptionName = order.OptionName,
                ReceiverName = order.ReceiverName ?? "",
                ReceiverPhone = order.ReceiverPhone ?? "",
                ReceiverZipcode = order.ReceiverZipcode ?? "",
                ReceiverAddress = order.ReceiverAddress ?? "",
                DeliveryMessage = order.DeliveryMessage,
                ExpectedAmount = preflight.EstimatedAmount,
                OrderId = orderId,
            }, ct);
        }
        catch (Exception ex)
        {
            // 예외가 나도 예약은 반드시 푼다
            await ReleaseAsync(wallet, preflight.EstimatedAmount, orderId, $"발주 오류: {ex.Message}", ct);
            logger.LogError(ex, "발주 중 예외 (Order {OrderId})", orderId);
            return PurchaseOutcome.Failed($"발주 중 오류가 발생했습니다: {ex.Message}", preflight);
        }

        // ── 3. 결과 반영 ──────────────────────────────────────────────
        if (!result.Success)
        {
            await ReleaseAsync(wallet, preflight.EstimatedAmount, orderId,
                $"발주 실패: {result.ErrorMessage}", ct);
            return PurchaseOutcome.Failed(result.ErrorMessage ?? "발주에 실패했습니다.", preflight);
        }

        var actualPaid = result.PaidAmount ?? preflight.EstimatedAmount;

        // 예상보다 크게 비싸면 사람이 봐야 한다 (공급가 인상·옵션가 등)
        if (result.PaidAmount is { } paid && preflight.EstimatedAmount > 0
            && paid > preflight.EstimatedAmount * 1.2m)
        {
            logger.LogWarning(
                "발주 금액이 예상보다 큽니다 (Order {OrderId}): 예상 {Expected:N0} → 실제 {Actual:N0}",
                orderId, preflight.EstimatedAmount, paid);
        }

        var capture = wallet.Capture(preflight.EstimatedAmount, actualPaid, orderId,
            $"발주 · {sheet.ProductName} x{sheet.Quantity} (공급처 주문번호 {result.SupplierOrderNo})");
        await wallets.SaveAsync(wallet, capture, ct);

        order.MarkSupplierOrdered(result.SupplierOrderNo!, SharedKernel.Money.Krw(actualPaid));
        await orders.SaveAsync(ct);

        logger.LogInformation("발주 완료 (Order {OrderId}) → 공급처 주문번호 {SupplierOrderNo}, {Paid:N0}원",
            orderId, result.SupplierOrderNo, actualPaid);

        return PurchaseOutcome.Succeeded(result.SupplierOrderNo!, actualPaid, wallet.Available);
    }

    private async Task ReleaseAsync(
        Wallet wallet, decimal amount, Guid orderId, string memo, CancellationToken ct)
    {
        var release = wallet.Release(amount, orderId, memo);
        await wallets.SaveAsync(wallet, release, ct);
    }
}

/// <summary>발주 전 점검 결과.</summary>
public sealed record PurchasePreflight
{
    public Guid OrderId { get; init; }
    public bool CanAutoOrder { get; init; }
    /// <summary>발주를 막는 사유들. 하나라도 있으면 발주할 수 없다.</summary>
    public List<string> Blockers { get; init; } = [];
    /// <summary>진행은 되지만 사람이 확인해야 하는 것들.</summary>
    public List<string> Warnings { get; init; } = [];

    public decimal EstimatedAmount { get; init; }
    public decimal WalletAvailable { get; init; }
    public decimal WalletShortfall { get; init; }

    public string? CapabilityReason { get; init; }
    public string? CapabilityHowToEnable { get; init; }
    /// <summary>자동 발주가 안 될 때 사람이 직접 주문할 링크.</summary>
    public string? SupplierUrl { get; init; }

    public PurchaseOrderSheet? Sheet { get; init; }

    public static PurchasePreflight Blocked(string reason) =>
        new() { CanAutoOrder = false, Blockers = [reason] };
}

public sealed record PurchaseOutcome
{
    public required bool Success { get; init; }
    public string? SupplierOrderNo { get; init; }
    public decimal? PaidAmount { get; init; }
    public decimal? WalletAvailableAfter { get; init; }
    public string? Message { get; init; }
    /// <summary>실패 시 UI가 원인과 대안을 보여줄 수 있도록 점검 결과를 함께 준다.</summary>
    public PurchasePreflight? Preflight { get; init; }

    public static PurchaseOutcome Succeeded(string orderNo, decimal paid, decimal availableAfter) =>
        new() { Success = true, SupplierOrderNo = orderNo, PaidAmount = paid, WalletAvailableAfter = availableAfter };

    public static PurchaseOutcome Rejected(string message, PurchasePreflight preflight) =>
        new() { Success = false, Message = message, Preflight = preflight };

    public static PurchaseOutcome Failed(string message, PurchasePreflight preflight) =>
        new() { Success = false, Message = message, Preflight = preflight };
}

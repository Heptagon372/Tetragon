namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 공급처에 실제로 발주를 넣는 능력.
///
/// 위탁판매의 자금 흐름:
///   구매자 → 마켓 결제 → (우리) → 공급처 결제 → 공급처가 구매자에게 직배송
///
/// 공급처마다 발주 수단이 다르다. 모든 공급처가 API를 열어 주지는 않으므로
/// ISupplierPlugin과 분리해, 지원하는 공급처만 이 인터페이스를 구현한다.
/// 미지원 공급처는 '수동 발주'(주문서 준비 + 사이트 링크)로 처리한다.
/// </summary>
public interface ISupplierOrderPlugin
{
    string SupplierCode { get; }

    /// <summary>
    /// 지금 이 자격증명으로 자동 발주가 가능한지.
    /// 도매꾹처럼 별도 권한(Private API)이 필요한 경우 false가 될 수 있다.
    /// </summary>
    Task<SupplierOrderCapability> CheckCapabilityAsync(CancellationToken ct);

    /// <summary>
    /// 발주를 넣는다. <b>실제로 돈이 나가는 호출이다.</b>
    /// 호출 전에 금고 잔액을 예약해야 하며, 실패 시 예약을 반드시 풀어야 한다.
    /// </summary>
    Task<SupplierOrderResult> PlaceOrderAsync(SupplierOrderRequest request, CancellationToken ct);
}

/// <summary>자동 발주 가능 여부와, 불가능하다면 그 이유.</summary>
public sealed record SupplierOrderCapability(bool CanAutoOrder, string? Reason, string? HowToEnable)
{
    public static SupplierOrderCapability Available() => new(true, null, null);
    public static SupplierOrderCapability Unavailable(string reason, string? howToEnable = null) =>
        new(false, reason, howToEnable);
}

public sealed record SupplierOrderRequest
{
    public required string SourceProductId { get; init; }
    public required string ProductUrl { get; init; }
    public required int Quantity { get; init; }
    /// <summary>선택한 옵션 (공급처 옵션 ID 또는 이름).</summary>
    public string? OptionId { get; init; }
    public string? OptionName { get; init; }

    /// <summary>수령인 — 판매자가 아니라 <b>최종 구매자</b>다. 위탁판매의 핵심.</summary>
    public required string ReceiverName { get; init; }
    public required string ReceiverPhone { get; init; }
    public required string ReceiverZipcode { get; init; }
    public required string ReceiverAddress { get; init; }
    public string? DeliveryMessage { get; init; }

    /// <summary>예상 결제 금액. 공급처 응답이 이보다 크게 다르면 중단해야 한다.</summary>
    public decimal ExpectedAmount { get; init; }
    /// <summary>우리 쪽 주문 식별자 (멱등성 확보용).</summary>
    public required Guid OrderId { get; init; }
}

public sealed record SupplierOrderResult
{
    public required bool Success { get; init; }
    /// <summary>공급처가 발급한 주문번호.</summary>
    public string? SupplierOrderNo { get; init; }
    /// <summary>실제로 결제된 금액. 예상과 다를 수 있다.</summary>
    public decimal? PaidAmount { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawResponse { get; init; }

    public static SupplierOrderResult Ok(string orderNo, decimal? paid, string? raw = null) =>
        new() { Success = true, SupplierOrderNo = orderNo, PaidAmount = paid, RawResponse = raw };

    public static SupplierOrderResult Fail(string code, string message, string? raw = null) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message, RawResponse = raw };
}

/// <summary>공급처 코드 → 발주 플러그인 해석.</summary>
public interface ISupplierOrderPluginRegistry
{
    ISupplierOrderPlugin? Resolve(string supplierCode);
    IReadOnlyList<ISupplierOrderPlugin> All { get; }
}

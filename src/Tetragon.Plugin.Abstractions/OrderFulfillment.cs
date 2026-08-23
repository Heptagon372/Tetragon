using Tetragon.SharedKernel;

namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 주문 이행(송장 반영·CS 요청 조회) 능력.
///
/// 모든 마켓이 지원하지는 않으므로 <see cref="IMarketplaceAdapter"/>와 분리한다
/// (엑셀 업로드로만 운영하는 옥션·G마켓 등은 구현하지 않는다).
///
/// 위탁판매에서 이 두 가지는 선택이 아니다:
///   - 송장 반영 안 하면 → 마켓이 '미출고'로 보고 페널티를 준다
///   - CS 요청 못 받으면 → 취소된 주문을 공급처에서 계속 사게 된다
/// </summary>
public interface IOrderFulfillmentProvider
{
    /// <summary>공급처가 준 송장번호를 마켓에 등록해 '배송중'으로 만든다.</summary>
    Task<FulfillmentResult> UploadTrackingAsync(
        TrackingUpload upload, MarketCredential credential, CancellationToken ct);

    /// <summary>구매자가 건 취소·반품·교환 요청을 가져온다.</summary>
    Task<IReadOnlyList<MarketCsTicket>> FetchCsTicketsAsync(
        DateRange range, MarketCredential credential, CancellationToken ct);
}

public sealed record TrackingUpload
{
    public required string MarketOrderId { get; init; }
    /// <summary>쿠팡 shipmentBoxId 등 마켓의 배송 묶음 식별자.</summary>
    public string? ShipmentBoxId { get; init; }
    public string? VendorItemId { get; init; }
    public required string TrackingNo { get; init; }
    /// <summary>택배사 코드. 마켓마다 코드 체계가 달라 어댑터가 변환한다.</summary>
    public required string DeliveryCompanyCode { get; init; }
}

public sealed record FulfillmentResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RawResponse { get; init; }

    public static FulfillmentResult Ok(string? raw = null) =>
        new() { Success = true, RawResponse = raw };

    public static FulfillmentResult Fail(string code, string message, string? raw = null) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message, RawResponse = raw };
}

/// <summary>마켓에서 가져온 CS 요청 (마켓 중립).</summary>
public sealed record MarketCsTicket
{
    public required string MarketCode { get; init; }
    /// <summary>중복 수집을 막는 마켓 측 고유 키.</summary>
    public required string MarketTicketId { get; init; }
    public required string MarketOrderId { get; init; }
    /// <summary>Cancel / Return / Exchange</summary>
    public required string Kind { get; init; }
    public string? ProductName { get; init; }
    public string? Reason { get; init; }
    public int Quantity { get; init; } = 1;
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}

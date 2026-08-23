using Tetragon.SharedKernel;

namespace Tetragon.Domain.Listings;

/// <summary>
/// 공급처 주소 → 마켓의 출고지/반품지 코드 매핑.
///
/// 위탁판매에서는 상품마다 출고지·반품지가 다르다(공급처가 다르므로).
/// 그런데 쿠팡 같은 마켓은 주소 문자열을 상품 등록에 직접 넣을 수 없고,
/// 먼저 마켓에 출고지/반품지를 등록해 받은 **코드**를 써야 한다.
///
/// 매번 등록하면 중복이 쌓이므로, (마켓 × 공급처 주소) 조합으로 한 번만 만들고
/// 코드를 여기 캐시해 재사용한다.
/// </summary>
public sealed class ShippingPlaceMapping : Entity<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string MarketCode { get; private set; } = "";
    /// <summary>공급처 코드 (도매꾹 등).</summary>
    public string SupplierCode { get; private set; } = "";
    /// <summary>주소 기반 식별 키 — 같은 공급처라도 주소가 바뀌면 새로 등록해야 한다.</summary>
    public string AddressKey { get; private set; } = "";

    public string? OutboundPlaceCode { get; private set; }
    public string? ReturnCenterCode { get; private set; }

    /// <summary>등록에 사용한 원본 주소 (감사·디버깅용).</summary>
    public string? OutboundAddress { get; private set; }
    public string? ReturnAddress { get; private set; }

    /// <summary>마켓 등록에 실패했다면 그 사유. 다음 시도에서 재시도한다.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private ShippingPlaceMapping() { }

    public static ShippingPlaceMapping Create(
        string tenantId, string marketCode, string supplierCode, string addressKey,
        string? outboundAddress, string? returnAddress) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MarketCode = marketCode,
            SupplierCode = supplierCode,
            AddressKey = addressKey,
            OutboundAddress = outboundAddress,
            ReturnAddress = returnAddress,
        };

    public void SetCodes(string? outboundPlaceCode, string? returnCenterCode)
    {
        OutboundPlaceCode = outboundPlaceCode;
        ReturnCenterCode = returnCenterCode;
        LastError = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 부분 성공을 저장한다. 한쪽만 등록됐으면 그 코드는 보존하고
    /// 오류는 남겨 다음 시도에서 나머지만 재시도하게 한다.
    /// </summary>
    public void SetCodesPreservingError(string? outboundPlaceCode, string? returnCenterCode, bool hadError)
    {
        OutboundPlaceCode = outboundPlaceCode;
        ReturnCenterCode = returnCenterCode;
        if (!hadError) LastError = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string error)
    {
        LastError = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>두 코드를 모두 확보했는지.</summary>
    public bool IsResolved =>
        !string.IsNullOrWhiteSpace(OutboundPlaceCode) && !string.IsNullOrWhiteSpace(ReturnCenterCode);

    /// <summary>주소에서 안정적인 식별 키를 만든다 (공백·대소문자 차이 무시).</summary>
    public static string BuildAddressKey(string? outbound, string? returnAddress)
    {
        static string Normalize(string? s) =>
            string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return $"{Normalize(outbound)}|{Normalize(returnAddress)}";
    }
}

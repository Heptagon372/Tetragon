using Tetragon.SharedKernel;

namespace Tetragon.Plugin.Abstractions;

/// <summary>오픈마켓 어댑터 계약 (설계서 5.6 - Adapter Pattern).</summary>
public interface IMarketplaceAdapter : IPlugin
{
    Task<ListingResult> RegisterAsync(ListingPayload payload, MarketCredential credential, CancellationToken ct);
    Task<ListingResult> UpdateAsync(string marketItemId, ListingPayload payload, MarketCredential credential, CancellationToken ct);
    Task<ListingResult> DeleteAsync(string marketItemId, MarketCredential credential, CancellationToken ct);
    Task<ListingResult> UpdatePriceStockAsync(string marketItemId, Money price, int stock, MarketCredential credential, CancellationToken ct);
    Task<IReadOnlyList<MarketOrder>> FetchOrdersAsync(DateRange range, MarketCredential credential, CancellationToken ct);
}

/// <summary>
/// 마켓 등록 페이로드. Product + 계산가 + 카테고리 매핑의 조합 (마켓 중립).
/// 마켓별 특수 필드 변환은 각 어댑터 내부에서만 처리한다 (설계서 §4 원칙).
/// </summary>
public sealed record ListingPayload
{
    public required string ProductId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<string> ImageUrls { get; init; } = [];
    public required Money SalePrice { get; init; }
    public int Stock { get; init; }
    public List<PayloadOptionGroup> OptionGroups { get; init; } = [];
    public List<PayloadVariant> Variants { get; init; } = [];
    public Dictionary<string, string> Attributes { get; init; } = [];
    /// <summary>마켓별 카테고리 코드 (CategoryMapping 결과). 키 = 마켓 코드.</summary>
    public Dictionary<string, string> MarketCategoryCodes { get; init; } = [];
    /// <summary>위탁판매 물류 정보 (공급처 출고지·반품지). 상품마다 다르다.</summary>
    public ConsignmentLogistics Logistics { get; init; } = new();

    /// <summary>
    /// 마켓에 미리 등록해 얻은 출고지 코드. IShippingPlaceProvider가 해석해 채운다.
    /// null이면 어댑터는 판매자 기본값으로 폴백한다.
    /// </summary>
    public string? ResolvedOutboundPlaceCode { get; init; }
    /// <summary>마켓에 미리 등록해 얻은 반품지 코드.</summary>
    public string? ResolvedReturnCenterCode { get; init; }
}

/// <summary>
/// 위탁판매 물류 정보.
///
/// 위탁판매는 판매자가 재고를 갖지 않는다. 상품은 공급처 창고에서 바로 구매자에게 가고
/// 반품도 공급처로 돌아간다. 따라서 출고지·반품지는 판매자 주소가 아니라
/// **상품마다 다른 공급처 주소**다. 마켓 등록 시 이 값을 써야 반품이 정상 처리된다.
///
/// 공급처가 값을 주지 않으면 각 필드는 null이고, 어댑터/엑셀이 판매자 기본값으로 폴백한다.
/// </summary>
public sealed record ConsignmentLogistics
{
    public string? SupplierName { get; init; }
    public string? SupplierPhone { get; init; }

    /// <summary>출고지 — 공급처 창고/사업장 주소.</summary>
    public string? OutboundAddress { get; init; }
    /// <summary>출고지 우편번호. 공급처가 주지 않는 경우가 많다 — <see cref="OutboundPlace"/>가 대신 판단한다.</summary>
    public string? OutboundZipcode { get; init; }
    public string? OutboundPhone { get; init; }

    /// <summary>반품지 — 공급처가 지정한 반품 수령 주소.</summary>
    public string? ReturnAddress { get; init; }
    public string? ReturnZipcode { get; init; }
    public string? ReturnPhone { get; init; }

    /// <summary>공급처가 청구하는 반품 배송비 (원). 마켓의 반품비 설정에 반영한다.</summary>
    public decimal? ReturnFee { get; init; }
    /// <summary>공급처 → 구매자 기본 배송비 (원). 가격에 녹였다면 마켓에는 무료로 등록한다.</summary>
    public decimal? DeliveryFee { get; init; }
    public decimal? JejuExtraFee { get; init; }
    public decimal? IslandExtraFee { get; init; }

    /// <summary>최소 구매 수량 — 도매는 MOQ가 있어 1개 주문이 불가능할 수 있다.</summary>
    public int? MinOrderQty { get; init; }

    /// <summary>
    /// 해외구매대행 상품인지.
    ///
    /// 국내 도매(도매꾹·11번가)와 해외 소싱(알리·타오바오·아마존)은
    /// 마켓 등록 시 배송방법·통관 설정이 완전히 다르다.
    /// 국내 상품을 구매대행으로 등록하면 쿠팡이 "배송방법을 확인해 주시기 바랍니다"로 거부한다.
    /// (실측 확인 — 도매꾹 상품을 AGENT_BUY로 보냈다가 거부당함)
    /// </summary>
    public bool IsOverseasPurchase { get; init; }

    /// <summary>공급처 평균 출고 소요일. 0.4면 사실상 당일출고다.</summary>
    public decimal? AverageOutboundDays { get; init; }

    /// <summary>
    /// 마켓에 표기할 출고 소요일(정수).
    ///
    /// 공급처 평균에 여유를 더한다 — 위탁판매는 우리가 발주한 뒤 공급처가 출고하므로
    /// 공급처 평균만큼 걸린다고 약속하면 지연이 난다. 여유 없이 등록했다가
    /// 출고 지연이 쌓이면 마켓 페널티를 받는다.
    /// </summary>
    public int OutboundShippingDays => AverageOutboundDays is { } avg
        ? Math.Clamp((int)Math.Ceiling(avg + OutboundBufferDays), 1, 30)
        : DefaultOutboundDays;

    /// <summary>발주→공급처 출고 사이에 두는 여유일.</summary>
    private const decimal OutboundBufferDays = 1m;
    /// <summary>출고일을 알 수 없을 때의 보수적 기본값.</summary>
    private const int DefaultOutboundDays = 3;

    /// <summary>
    /// 상세설명 이미지를 재사용해도 되는지 (공급처가 명시적으로 허용한 경우만 true).
    /// false면 상세 이미지를 마켓에 올리지 않는다 — 저작권 문제를 피하기 위함이다.
    /// </summary>
    public bool DetailImagesAllowed { get; init; }

    /// <summary>공급처 상세설명 HTML (라이선스 허용 시에만 값이 있다).</summary>
    public string? DetailHtml { get; init; }

    /// <summary>
    /// 마켓에 보낼 수 있게 정제한 반품지 연락처.
    ///
    /// 공급처는 연락처를 자유 입력으로 받아 "010-2376-0142 / 032-713-7919"처럼
    /// 여러 개를 한 칸에 넣어 두는 경우가 많다. 쿠팡은 16자 상한이라 그대로 보내면 거부된다.
    /// 첫 번째로 유효한 번호 하나만 골라 쓴다.
    /// </summary>
    public string? ContactNumber => PickPhone(ReturnPhone) ?? PickPhone(SupplierPhone);

    /// <summary>
    /// 여러 번호가 섞인 문자열에서 쓸 수 있는 번호 하나를 고른다.
    /// 자릿수가 국내 전화번호 범위(9~11자리)인 것만 인정한다.
    /// </summary>
    private static string? PickPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var candidate in raw.Split(['/', ',', '|', '\n'], StringSplitOptions.TrimEntries))
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length is 0 or > MaxPhoneLength) continue;
            var digits = trimmed.Count(char.IsDigit);
            if (digits is >= 9 and <= 11) return trimmed;
        }
        return null;
    }

    /// <summary>쿠팡 연락처 필드 상한.</summary>
    private const int MaxPhoneLength = 16;

    /// <summary>
    /// 반품지 우편번호가 신 우편번호(5자리)인지.
    /// 구 우편번호(예: 626-812)는 마켓이 받지 않으므로 배송지 등록에 쓸 수 없다.
    /// </summary>
    public bool HasValidZipcode => KoreanAddress.IsValidZipcode(ReturnZipcode);

    /// <summary>
    /// 마켓 주소록에 등록할 <b>출고지</b>의 주소와 우편번호.
    ///
    /// 주소와 우편번호는 반드시 같은 곳에서 나와야 한다.
    /// 예전에는 출고지 주소(공급사 사업장)에 반품지 우편번호를 붙여 보냈는데,
    /// 두 주소가 다른 공급처에서는 쿠팡 주소록에 <b>엉뚱한 우편번호를 가진 출고지</b>가 만들어졌다.
    ///
    /// 판단 순서:
    ///   1. 공급처가 출고지 우편번호를 직접 준 경우 — 그대로 쓴다
    ///   2. 출고지와 반품지가 같은 건물이면 — 반품지 우편번호가 곧 출고지 우편번호다
    ///   3. 그 외 — 반품지를 출고지로 쓴다. 위탁판매에서 물건이 실제로 나가는 곳은
    ///      사업자등록 주소가 아니라 공급처 창고(=반품 받는 곳)이고, 그쪽만 우편번호가 확인된다.
    ///   4. 반품지도 없으면 — 우편번호 없이 주소만 (마켓 등록은 실패하고 사유가 남는다)
    /// </summary>
    public (string? Address, string? Zipcode) OutboundPlace
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OutboundAddress) && KoreanAddress.IsValidZipcode(OutboundZipcode))
                return (OutboundAddress, OutboundZipcode);

            if (!string.IsNullOrWhiteSpace(OutboundAddress)
                && KoreanAddress.SamePlace(OutboundAddress, ReturnAddress)
                && KoreanAddress.IsValidZipcode(ReturnZipcode))
                return (OutboundAddress, ReturnZipcode);

            if (!string.IsNullOrWhiteSpace(ReturnAddress) && KoreanAddress.IsValidZipcode(ReturnZipcode))
                return (ReturnAddress, ReturnZipcode);

            return (OutboundAddress ?? ReturnAddress, null);
        }
    }

    /// <summary>출고지 주소와 우편번호가 짝이 맞게 확보됐는지.</summary>
    public bool HasValidOutboundPlace =>
        OutboundPlace is { Address: { Length: > 0 }, Zipcode: { Length: > 0 } };

    /// <summary>
    /// 출고지로 사업장 주소 대신 반품지를 쓰게 된 경우 — 로그·경고에 이유를 남기기 위한 표시.
    /// </summary>
    public bool OutboundFellBackToReturnAddress =>
        !string.IsNullOrWhiteSpace(OutboundAddress)
        && !string.IsNullOrWhiteSpace(ReturnAddress)
        && !KoreanAddress.SamePlace(OutboundAddress, ReturnAddress)
        && !KoreanAddress.IsValidZipcode(OutboundZipcode)
        && KoreanAddress.IsValidZipcode(ReturnZipcode);

    /// <summary>
    /// 공급처에서 출고지·반품지를 모두 얻었는지. false면 판매자 기본값 폴백이 필요하다.
    /// 출고지는 <see cref="OutboundPlace"/>가 반품지로 대체할 수 있으므로 그 결과로 판단한다.
    /// </summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(OutboundPlace.Address) && !string.IsNullOrWhiteSpace(ReturnAddress);

    /// <summary>
    /// 수집한 Attributes에서 물류 정보를 뽑아낸다.
    /// sourceCurrency는 국내/해외 판정에 쓴다 (KRW = 국내 소싱).
    /// </summary>
    public static ConsignmentLogistics FromAttributes(
        IReadOnlyDictionary<string, string> attributes, string? sourceCurrency = null)
    {
        string? Value(string key) =>
            attributes.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        decimal? Amount(string key) =>
            decimal.TryParse(Value(key)?.Replace(",", ""), out var d) ? d : null;

        return new ConsignmentLogistics
        {
            SupplierName = Value(LogisticsKeys.SupplierName),
            SupplierPhone = Value(LogisticsKeys.SupplierPhone),
            OutboundAddress = Value(LogisticsKeys.OutboundAddress),
            OutboundZipcode = Value(LogisticsKeys.OutboundZipcode),
            OutboundPhone = Value(LogisticsKeys.OutboundPhone) ?? Value(LogisticsKeys.SupplierPhone),
            ReturnAddress = Value(LogisticsKeys.ReturnAddress),
            ReturnZipcode = Value(LogisticsKeys.ReturnZipcode),
            ReturnPhone = Value(LogisticsKeys.ReturnPhone),
            ReturnFee = Amount(LogisticsKeys.ReturnFee),
            DeliveryFee = Amount(LogisticsKeys.DeliveryFee),
            JejuExtraFee = Amount(LogisticsKeys.JejuExtraFee),
            IslandExtraFee = Amount(LogisticsKeys.IslandExtraFee),
            MinOrderQty = int.TryParse(Value(LogisticsKeys.MinOrderQty), out var moq) ? moq : null,
            // 원가 통화가 원화면 국내 소싱 = 구매대행 아님
            IsOverseasPurchase = sourceCurrency is { Length: > 0 }
                && !sourceCurrency.Equals("KRW", StringComparison.OrdinalIgnoreCase),
            AverageOutboundDays = decimal.TryParse(Value(LogisticsKeys.AvgOutboundDays), out var days)
                ? days : null,
            DetailImagesAllowed = Value(LogisticsKeys.DetailImageLicense) is "Y",
            DetailHtml = Value(LogisticsKeys.DetailHtml),
        };
    }
}

public sealed record PayloadOptionGroup(string Name, List<string> Values);

public sealed record PayloadVariant
{
    public required string VariantId { get; init; }
    public Dictionary<string, string> Options { get; init; } = [];
    public required Money Price { get; init; }
    public int Stock { get; init; }
}

/// <summary>
/// 공급처 주소를 마켓의 출고지/반품지로 등록하고 코드를 돌려주는 능력.
///
/// 쿠팡처럼 주소를 상품 등록에 직접 넣지 못하고 사전 등록된 코드를 요구하는 마켓만 구현한다.
/// 엑셀 업로드나 주소를 그대로 받는 마켓(11번가·ESM)은 구현할 필요가 없다.
/// </summary>
public interface IShippingPlaceProvider
{
    /// <summary>공급처 출고지를 마켓에 등록하고 코드를 반환한다. 이미 있으면 그 코드를 반환한다.</summary>
    Task<ShippingPlaceResult> EnsureOutboundPlaceAsync(
        ConsignmentLogistics logistics, MarketCredential cred, CancellationToken ct);

    /// <summary>공급처 반품지를 마켓에 등록하고 코드를 반환한다.</summary>
    Task<ShippingPlaceResult> EnsureReturnCenterAsync(
        ConsignmentLogistics logistics, MarketCredential cred, CancellationToken ct);
}

public sealed record ShippingPlaceResult(bool Success, string? Code, string? Error)
{
    public static ShippingPlaceResult Ok(string code) => new(true, code, null);
    public static ShippingPlaceResult Fail(string error) => new(false, null, error);
}

/// <summary>테넌트별 마켓 자격증명. 어댑터 호출 시점에만 전달된다 (설계서 5.6).</summary>
public sealed record MarketCredential
{
    public Dictionary<string, string> Secrets { get; init; } = [];
    public string? Get(string key) => Secrets.GetValueOrDefault(key);
    public string Require(string key) =>
        Secrets.GetValueOrDefault(key)
        ?? throw new PluginCredentialException($"마켓 자격증명 누락: '{key}' — 설정 화면에서 입력하세요.");
}

public sealed class PluginCredentialException(string message) : Exception(message);

public sealed record ListingResult
{
    public bool Success { get; init; }
    /// <summary>마켓이 발급한 상품 번호.</summary>
    public string? MarketItemId { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>마켓 API 원본 응답 (감사/디버깅).</summary>
    public string? RawResponse { get; init; }

    public static ListingResult Ok(string marketItemId, string? raw = null) =>
        new() { Success = true, MarketItemId = marketItemId, RawResponse = raw };
    public static ListingResult Fail(string code, string message, string? raw = null) =>
        new() { Success = false, ErrorCode = code, ErrorMessage = message, RawResponse = raw };
}

public sealed record MarketOrder
{
    public required string MarketOrderId { get; init; }
    public required string MarketCode { get; init; }
    public string? MarketItemId { get; init; }
    public string? ProductName { get; init; }
    public string? OptionName { get; init; }
    public int Quantity { get; init; }
    public Money PaidAmount { get; init; }
    public string? OrdererName { get; init; }
    public DateTimeOffset OrderedAt { get; init; }
    public string Status { get; init; } = "Paid";

    /// <summary>
    /// 구매자 배송지. 위탁판매에서는 이 주소를 공급처 발주의 수령지로 그대로 넘긴다
    /// (판매자를 거치지 않고 공급처 → 구매자로 바로 배송).
    /// </summary>
    public ShippingAddress? ShipTo { get; init; }

    /// <summary>
    /// 송장을 올릴 때 마켓이 요구하는 배송 묶음 식별자 (쿠팡 shipmentBoxId).
    /// 주문을 수집할 때 같이 받아두지 않으면 나중에 송장을 등록할 방법이 없다.
    /// </summary>
    public string? ShipmentBoxId { get; init; }

    /// <summary>마켓 내부 옵션 식별자 (쿠팡 vendorItemId). 송장 등록에 함께 필요하다.</summary>
    public string? VendorItemId { get; init; }
}

/// <summary>배송지 정보.</summary>
public sealed record ShippingAddress
{
    public string? ReceiverName { get; init; }
    public string? Phone { get; init; }
    public string? Zipcode { get; init; }
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    /// <summary>배송 요청사항.</summary>
    public string? Message { get; init; }

    public string FullAddress =>
        string.Join(" ", new[] { Address1, Address2 }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
}

using Tetragon.SharedKernel;

namespace Tetragon.Plugin.Abstractions;

/// <summary>공급처(스크래퍼) 플러그인 계약 (설계서 5.1).</summary>
public interface ISupplierPlugin : IPlugin
{
    /// <summary>이 플러그인이 처리할 수 있는 URL인지 판단 (Registry의 Factory 라우팅에 사용).</summary>
    bool CanHandle(Uri productUrl);

    /// <summary>상품 URL 하나를 수집해 RawProduct로 반환한다.</summary>
    Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext context, CancellationToken ct);

    /// <summary>재고/원가 재확인 (Inventory Sync에서 호출).</summary>
    Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct);

    /// <summary>
    /// URL만 보고 원본 상품번호를 알아낸다 — 수집하기 전에 이미 가진 상품인지 확인하는 용도.
    /// 알 수 없으면 null. 기본값이 null이므로 기존 플러그인은 손댈 필요가 없고,
    /// 이 값을 줄 수 있는 플러그인만 중복 검사의 이득을 본다.
    /// </summary>
    string? TryGetSourceProductId(Uri productUrl) => null;
}

/// <summary>수집 컨텍스트: 테넌트별 쿠키/프록시 등 실행 옵션.</summary>
public sealed record ScrapeContext
{
    public string TenantId { get; init; } = Tenant.Default;
    /// <summary>로그인 세션이 필요한 공급처용 쿠키 문자열 (설정에서 주입).</summary>
    public string? CookieHeader { get; init; }
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// 위탁판매 물류 표준 속성 키.
///
/// 위탁판매는 판매자가 재고를 갖지 않으므로 출고지·반품지가 판매자 주소가 아니라
/// **공급처(도매꾹 셀러 등) 주소**여야 한다. 공급처 플러그인이 이 키들로
/// Attributes에 담으면 Product → ListingPayload → 엑셀/어댑터까지 그대로 흘러가고,
/// 값이 없을 때만 설정 화면의 판매자 기본값으로 폴백한다.
/// </summary>
public static class LogisticsKeys
{
    public const string OutboundAddress = "출고지주소";
    /// <summary>
    /// 출고지 우편번호. 공급처가 출고지 주소만 주고 우편번호는 안 주는 경우가 많아
    /// (도매꾹 seller.company.addr이 그렇다) 값이 없을 수 있다 — 그때는 반품지 쪽으로 폴백한다.
    /// </summary>
    public const string OutboundZipcode = "출고지우편번호";
    public const string OutboundPhone = "출고지연락처";
    public const string ReturnAddress = "반품지주소";
    public const string ReturnZipcode = "반품지우편번호";
    public const string ReturnPhone = "반품지연락처";
    public const string ReturnFee = "반품배송비";
    public const string DeliveryFee = "공급처배송비";
    public const string JejuExtraFee = "제주추가배송비";
    public const string IslandExtraFee = "도서산간추가배송비";
    public const string SupplierName = "공급사";
    public const string SupplierPhone = "공급사연락처";
    public const string MinOrderQty = "최소구매수량";

    /// <summary>공급처 평균 출고 소요일 (예: "0.4" = 당일출고 수준).</summary>
    public const string AvgOutboundDays = "평균출고일";
    /// <summary>배송 준비기간 안내 문구 (예: "즉시배송가능").</summary>
    public const string OutboundNote = "출고안내";

    /// <summary>
    /// 상세설명 이미지를 다른 곳에 사용해도 되는지 (공급처가 허용한 경우만 "Y").
    /// 허용되지 않은 이미지를 마켓에 올리면 저작권 문제가 된다.
    /// </summary>
    public const string DetailImageLicense = "상세이미지사용허용";
    /// <summary>상세설명 HTML 원본 (라이선스 허용 시에만 채운다).</summary>
    public const string DetailHtml = "상세설명HTML";

    /// <summary>
    /// 공급처가 제공한 검색 키워드 (쉼표 구분).
    /// 마켓 검색 노출에 직접 영향을 주므로, 상품명에서 추측하는 것보다 공급처 값이 훨씬 정확하다.
    /// </summary>
    public const string SearchKeywords = "검색어";
}

/// <summary>
/// 스크래퍼가 반환하는 원본 상품 (Universal Product Model로 정규화되기 전).
/// RawJson은 재처리·디버깅·감사용으로 그대로 보존된다 (설계서 §4 - MongoDB raw_products 대응).
/// </summary>
public sealed record RawProduct
{
    public required string SupplierCode { get; init; }
    public required string SourceProductId { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string TitleLocale { get; init; } = "zh-CN";
    public string? Description { get; init; }
    public List<string> ImageUrls { get; init; } = [];
    public List<string> CategoryPath { get; init; } = [];
    public List<RawOptionGroup> OptionGroups { get; init; } = [];
    public List<RawVariant> Variants { get; init; } = [];
    public required string Currency { get; init; }
    public decimal BasePrice { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = [];
    /// <summary>스크래핑 원본 페이로드(JSON). 그대로 보존.</summary>
    public string RawJson { get; init; } = "{}";
}

public sealed record RawOptionGroup(string Name, List<RawOptionValue> Values);
public sealed record RawOptionValue(string Id, string Name, string? ImageUrl);

public sealed record RawVariant
{
    public required string SourceSkuId { get; init; }
    /// <summary>옵션 조합: (그룹명 → 값 Id)</summary>
    public Dictionary<string, string> OptionValueIds { get; init; } = [];
    public decimal Price { get; init; }
    public int Stock { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed record RawInventory
{
    public required string SourceProductId { get; init; }
    public bool IsAvailable { get; init; }
    public List<RawVariant> Variants { get; init; } = [];
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
}

using Tetragon.Application.Ports;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Pricing;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// 링크 하나를 실제로 긁어서 "쿠팡에 뭐가 올라가는지"를 저장 없이 보여준다.
///
/// 등록은 되돌리기가 번거롭다 — 잘못된 반품지나 적자 가격으로 올리면
/// 쿠팡에서 내리고 다시 올려야 한다. 그래서 보내기 전에 한 번 보여준다.
/// DB에 아무것도 쓰지 않으므로 몇 번을 눌러도 상품이 늘지 않는다.
/// </summary>
public sealed class LinkPreviewService(
    ISupplierPluginRegistry suppliers,
    ICredentialStore credentials,
    IRawProductRepository rawProducts,
    IProductRepository products,
    IPricingPolicyRepository policies,
    ProductNormalizer normalizer,
    PricingEngine pricing)
{
    public async Task<LinkPreview> InspectAsync(string rawUrl, Guid? pricingPolicyId, CancellationToken ct)
    {
        if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var url))
            throw new ArgumentException("주소 형식이 아닙니다.");

        var plugin = suppliers.Resolve(url)
            ?? throw new InvalidOperationException(
                $"'{url.Host}'을(를) 처리할 공급처가 없습니다. 도매꾹/도매매 상품 링크를 넣어주세요.");

        var credential = await credentials.GetAsync($"supplier:{plugin.Code}", ct);
        var raw = await plugin.CollectAsync(url, new ScrapeContext
        {
            TenantId = Tenant.Default,
            CookieHeader = credential.Get("cookie"),
        }, ct);

        // 저장하지 않는다 — 이 Product는 미리보기 계산용으로만 산다
        var product = normalizer.Normalize(raw, Tenant.Default, Guid.NewGuid());

        var policy = (pricingPolicyId is Guid id ? await policies.FindAsync(id, ct) : null)
            ?? await policies.FindDefaultAsync(ct)
            ?? throw new InvalidOperationException("가격 정책이 없습니다. 기본 정책을 먼저 만드세요.");

        var calculations = await pricing.CalculateAsync(product, policy, ct);
        var marketFeePct = policy.Rules
            .FirstOrDefault(r => r.RuleCode == "market-fee")?
            .Parameters.GetValueOrDefault("feePct", 13m) ?? 13m;

        var logistics = ConsignmentLogistics.FromAttributes(product.Attributes, product.BasePrice.Currency);
        var supplierShipping = logistics.DeliveryFee ?? 0m;

        // 가장 나쁜 SKU 기준으로 본다 — 하나라도 적자면 알아야 한다
        var worst = calculations
            .Select(c => MarginHealth.Assess(
                c.FinalPrice, c.SourceCost, c.ExchangeRate, marketFeePct, supplierShipping))
            .OrderBy(a => a.Profit)
            .FirstOrDefault();

        var existing = await FindExistingAsync(plugin.Code, raw.SourceProductId, ct);

        return new LinkPreview
        {
            Url = raw.Url,
            SupplierCode = plugin.Code,
            SupplierDisplayName = plugin.DisplayName,
            SourceProductId = raw.SourceProductId,
            Name = product.Name.GetOrFirst("ko-KR"),
            ImageUrls = product.Images.OrderBy(i => i.Order).Select(i => i.Url).ToList(),
            OptionGroups = product.OptionGroups
                .Select(g => new PreviewOptionGroup(g.Name, g.Values.Select(v => v.Name).ToList()))
                .ToList(),
            VariantCount = product.Variants.Count,
            TotalStock = product.Variants.Sum(v => v.SourceStock),
            Variants = BuildVariantRows(product, calculations),
            SourceCost = product.BasePrice,
            // Money는 비교 연산이 없으므로 금액으로 정렬해서 고른다.
            // 마켓에 걸리는 대표가는 가장 싼 옵션 가격이다.
            SalePrice = calculations.Count > 0
                ? calculations.MinBy(c => c.FinalPrice.Amount)!.FinalPrice
                : Money.Krw(0),
            Margin = worst,
            MarketFeePct = marketFeePct,
            PolicyName = policy.Name,
            Logistics = ConsignmentLogisticsView.Of(logistics),
            SearchKeywords = product.Attributes.GetValueOrDefault(LogisticsKeys.SearchKeywords),
            ExistingProductId = existing?.Id,
            ExistingStatus = existing?.Status.ToString(),
            Warnings = BuildWarnings(product, logistics, worst, existing),
        };
    }

    /// <summary>
    /// 옵션 조합을 그대로 보여준다 — "빨강 / 8(250)"처럼 실제로 팔리는 단위 하나하나.
    ///
    /// 옵션마다 공급가와 재고가 다르기 때문에, 조합 목록을 봐야
    /// 어떤 옵션이 적자인지·품절인지 등록 전에 알 수 있다.
    /// </summary>
    private static List<PreviewVariant> BuildVariantRows(
        Product product, IReadOnlyList<Domain.Pricing.PriceCalculation> calculations)
    {
        var priceByVariant = calculations.ToDictionary(c => c.VariantId, c => c.FinalPrice);

        return product.Variants
            .Select(v => new PreviewVariant
            {
                VariantId = v.VariantId,
                // 옵션값 Id를 사람이 읽는 이름으로 바꾼다 (그룹 순서 유지)
                Options = product.OptionGroups
                    .Select(g => (
                        Group: g.Name,
                        Value: v.OptionValueIds.TryGetValue(g.Name, out var valueId)
                            ? g.Values.FirstOrDefault(x => x.ValueId == valueId)?.Name
                            : null))
                    .Where(x => x.Value is not null)
                    .ToDictionary(x => x.Group, x => x.Value!),
                SourceCost = v.SourcePrice,
                SalePrice = priceByVariant.GetValueOrDefault(v.VariantId),
                Stock = v.SourceStock,
            })
            .OrderBy(v => v.SalePrice?.Amount ?? 0)
            .ToList();
    }

    private async Task<Product?> FindExistingAsync(string supplierCode, string sourceProductId, CancellationToken ct)
    {
        var record = await rawProducts.FindBySourceAsync(supplierCode, sourceProductId, ct);
        if (record?.ProductId is not Guid productId) return null;
        return await products.FindAsync(productId, ct);
    }

    /// <summary>
    /// 마켓이 받아주는 최소 판매가. 쿠팡 기준 1,000원이며 어댑터가 이 값으로 올려 보낸다.
    /// (Application은 마켓 플러그인을 참조하지 않으므로 값을 여기 둔다 —
    ///  CoupangAdapter.MinCoupangSalePrice와 같은 값이다.)
    /// </summary>
    private const decimal MinMarketSalePrice = 1000m;

    /// <summary>
    /// 등록 후에야 드러나는 문제들을 미리 모은다.
    /// 쿠팡이 거절하는 것(반품지 우편번호)과 거절하진 않지만 손해인 것(적자)을 모두 담는다.
    /// </summary>
    private static List<string> BuildWarnings(
        Product product, ConsignmentLogistics logistics, MarginAssessment? margin, Product? existing)
    {
        var warnings = new List<string>();

        if (existing is not null)
            warnings.Add($"이미 수집한 상품입니다 (상태: {existing.Status}). 다시 보내면 중복 등록이 됩니다.");

        if (margin is { Level: MarginLevel.Loss })
            warnings.Add($"적자입니다 — {margin.Message}");
        else if (margin is { Level: MarginLevel.Thin })
            warnings.Add($"마진이 얇습니다 — {margin.Message}");

        if (string.IsNullOrWhiteSpace(logistics.ReturnAddress))
            warnings.Add("공급처 반품지 주소가 없습니다. 설정의 판매자 기본 반품지로 등록됩니다.");
        else if (!logistics.HasValidZipcode)
            warnings.Add($"반품지 우편번호가 5자리 새 우편번호가 아닙니다 ({logistics.ReturnZipcode ?? "없음"}). 쿠팡이 거절할 수 있습니다.");

        if (!logistics.DetailImagesAllowed)
            warnings.Add("공급처가 상세 이미지 사용을 허용하지 않았습니다. 상세 이미지는 빼고 등록됩니다.");

        if (logistics.MinOrderQty > 1)
            warnings.Add($"공급처 최소구매수량이 {logistics.MinOrderQty}개입니다. 1개 주문이 오면 발주할 수 없습니다.");

        if (product.Variants.Sum(v => v.SourceStock) == 0)
            warnings.Add("공급처 재고가 0입니다. 지금 등록하면 주문을 받아도 발주할 수 없습니다.");

        if (product.Images.Count == 0)
            warnings.Add("대표 이미지가 없습니다. 쿠팡은 이미지 없는 상품을 거절합니다.");

        // 쿠팡은 1,000원 미만 옵션을 거부하므로 등록할 때 하한으로 올려 보낸다.
        // 판매가가 의도와 달라지는 것이므로 미리 알린다.
        var belowFloor = product.Variants.Count(v =>
            v.CalculatedPrice is { } p && p.Amount > 0 && p.Amount < MinMarketSalePrice);
        if (belowFloor > 0)
            warnings.Add(
                $"판매가가 1,000원 미만인 옵션이 {belowFloor}개 있습니다. " +
                "쿠팡 최소 판매가라 등록 시 1,000원으로 올려 보냅니다.");

        return warnings;
    }
}

public sealed record LinkPreview
{
    public required string Url { get; init; }
    public required string SupplierCode { get; init; }
    public required string SupplierDisplayName { get; init; }
    public required string SourceProductId { get; init; }

    public required string Name { get; init; }
    public List<string> ImageUrls { get; init; } = [];
    public List<PreviewOptionGroup> OptionGroups { get; init; } = [];
    /// <summary>실제로 팔리는 옵션 조합 하나하나 — 조합마다 공급가·재고가 다르다.</summary>
    public List<PreviewVariant> Variants { get; init; } = [];
    public int VariantCount { get; init; }
    public int TotalStock { get; init; }

    public Money SourceCost { get; init; }
    public Money SalePrice { get; init; }
    public MarginAssessment? Margin { get; init; }
    public decimal MarketFeePct { get; init; }
    public string? PolicyName { get; init; }

    public required ConsignmentLogisticsView Logistics { get; init; }
    public string? SearchKeywords { get; init; }

    /// <summary>이미 수집한 상품이면 그 id — 화면에서 "다시 긁지 않고 등록" 선택지를 준다.</summary>
    public Guid? ExistingProductId { get; init; }
    public string? ExistingStatus { get; init; }

    public List<string> Warnings { get; init; } = [];
}

public sealed record PreviewOptionGroup(string Name, List<string> Values);

public sealed record PreviewVariant
{
    public required string VariantId { get; init; }
    /// <summary>그룹명 → 값. 예: {"color":"카키블랙", "size":"8(250)"}</summary>
    public Dictionary<string, string> Options { get; init; } = [];
    public Money SourceCost { get; init; }
    public Money? SalePrice { get; init; }
    public int Stock { get; init; }
}

/// <summary>
/// 화면에 보여줄 물류 정보.
///
/// <see cref="ConsignmentLogistics"/>를 그대로 내보내면 <c>outboundAddress</c>가
/// '공급사 사업장 주소'(우편번호가 없을 수 있음)를 뜻하는데, 화면에서 필요한 건
/// '마켓에 실제로 등록될 출고지'다. 둘을 분리해서 이름을 붙인다.
/// 상품 상세와 링크 미리보기가 같은 값을 보게 하려고 한 곳에 둔다.
/// </summary>
public sealed record ConsignmentLogisticsView
{
    public string? SupplierName { get; init; }
    public string? SupplierPhone { get; init; }

    /// <summary>마켓 주소록에 등록되는 출고지 — 아래 우편번호와 짝이 맞는 주소다.</summary>
    public string? OutboundAddress { get; init; }
    public string? OutboundZipcode { get; init; }
    /// <summary>공급사 사업장 주소. 출고지와 다를 수 있다(비상주 사무실 등).</summary>
    public string? SupplierBusinessAddress { get; init; }
    /// <summary>출고지 우편번호가 없어 반품지 주소를 출고지로 쓴 경우.</summary>
    public bool OutboundUsesReturnAddress { get; init; }

    public string? ReturnAddress { get; init; }
    public string? ReturnZipcode { get; init; }
    public string? ReturnPhone { get; init; }
    public decimal? ReturnFee { get; init; }
    public decimal? DeliveryFee { get; init; }
    public decimal? JejuExtraFee { get; init; }
    public decimal? IslandExtraFee { get; init; }
    public int? MinOrderQty { get; init; }
    public bool IsOverseasPurchase { get; init; }
    public decimal? AverageOutboundDays { get; init; }
    public int OutboundShippingDays { get; init; }
    public bool DetailImagesAllowed { get; init; }
    public bool HasDetailHtml { get; init; }

    public static ConsignmentLogisticsView Of(ConsignmentLogistics l)
    {
        var (address, zipcode) = l.OutboundPlace;
        return new ConsignmentLogisticsView
        {
            SupplierName = l.SupplierName,
            SupplierPhone = l.SupplierPhone,
            OutboundAddress = address,
            OutboundZipcode = zipcode,
            SupplierBusinessAddress = l.OutboundAddress,
            OutboundUsesReturnAddress = l.OutboundFellBackToReturnAddress,
            ReturnAddress = l.ReturnAddress,
            ReturnZipcode = l.ReturnZipcode,
            ReturnPhone = l.ReturnPhone,
            ReturnFee = l.ReturnFee,
            DeliveryFee = l.DeliveryFee,
            JejuExtraFee = l.JejuExtraFee,
            IslandExtraFee = l.IslandExtraFee,
            MinOrderQty = l.MinOrderQty,
            IsOverseasPurchase = l.IsOverseasPurchase,
            AverageOutboundDays = l.AverageOutboundDays,
            OutboundShippingDays = l.OutboundShippingDays,
            DetailImagesAllowed = l.DetailImagesAllowed,
            HasDetailHtml = !string.IsNullOrWhiteSpace(l.DetailHtml),
        };
    }
}

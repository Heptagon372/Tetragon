using Tetragon.Application.Services;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Compliance;
using Tetragon.Domain.Listings;
using Tetragon.Domain.Pricing;
using Tetragon.Domain.Sourcing;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Api.Contracts;

// ── 요청 ─────────────────────────────────────────────────────────────────

public sealed record CollectRequest(string? Url, List<string>? Urls, Guid? PricingPolicyId);
/// <summary>링크 미리보기 — 저장 없이 수집 결과만 본다.</summary>
public sealed record LinkPreviewRequest(string Url, Guid? PricingPolicyId);
/// <summary>
/// 링크 → 마켓 자동 등록.
/// ReuseExisting을 false로 주면 이미 수집한 상품이어도 다시 긁는다 (중복 상품이 생긴다).
/// </summary>
public sealed record QuickListRequest(
    string? Url, List<string>? Urls, List<string>? MarketCodes, Guid? PricingPolicyId, bool? ReuseExisting);
public sealed record ProductEditRequest(string? Name, string? Description);
public sealed record ListingRequest(List<Guid> ProductIds, List<string> MarketCodes);
public sealed record PricingPolicyRequest(string Name, List<PriceRuleConfigDto> Rules, bool IsDefault);
public sealed record PriceRuleConfigDto(string RuleCode, int Order, Dictionary<string, decimal> Parameters);
/// <summary>MarginPct를 주면 정책을 저장하지 않고 그 마진율로 미리 계산한다.</summary>
public sealed record SimulateRequest(Guid ProductId, decimal? MarginPct);
public sealed record ComplianceRuleRequest(string Keyword, string Severity, string? Reason, string? MarketCode);
public sealed record CredentialRequest(string Scope, Dictionary<string, string> Secrets);
/// <summary>송장 등록. 택배사 코드를 함께 주면 마켓에 반영할 때 그대로 쓴다.</summary>
public sealed record TrackingRequest(string TrackingNo, string? DeliveryCompanyCode);
/// <summary>공급사 사이트에서 직접 주문한 뒤 그 결과를 기록할 때 쓴다.</summary>
public sealed record ManualPurchaseRequest(string? SupplierOrderNo, decimal? SupplierPaidAmount);
public sealed record WalletAmountRequest(decimal Amount, string? Memo);
public sealed record WalletThresholdRequest(decimal Threshold);

/// <summary>주의 항목 연기. 기본 7일.</summary>
public sealed record AttentionSnoozeRequest(int? UntilDays);
public sealed record AttentionNoteRequest(string? Note);
/// <summary>일괄 처리. Action은 'snooze' | 'done' | 'dismiss'.</summary>
public sealed record AttentionBulkRequest(List<Guid> Ids, string Action, int? UntilDays, string? Note);
/// <summary>CSV 컬럼 매핑 확정 (확장 08 §3.2). ColumnMap은 정규화된 컬럼명 → 표준 필드명.</summary>
public sealed record ImportProfileRequest(
    string Channel, string Purpose, string Fingerprint,
    Dictionary<string, string> ColumnMap, List<string>? SampleHeader);

public sealed record CategoryPreviewRequest(
    string? CategoryCode, string? Keyword, int? PageSize, decimal? MinPrice, decimal? MaxPrice);

public sealed record CategoryCollectRequest(
    string? CategoryCode, string? CategoryName, string? Keyword,
    int? MaxProducts, decimal? MinPrice, decimal? MaxPrice, Guid? PricingPolicyId);

public sealed record ExcelExportRequest(
    List<Guid>? ProductIds, string? Status, Dictionary<string, string>? Settings);

// ── 응답 ─────────────────────────────────────────────────────────────────

public static class ProductDto
{
    public static object Summary(Product p) => new
    {
        id = p.Id,
        name = p.Name.Get("ko-KR") ?? p.Name.GetOrFirst("ko-KR"),
        originalName = p.Name.Values.FirstOrDefault(kv => kv.Key != "ko-KR").Value,
        status = p.Status.ToString(),
        supplier = p.Source.SupplierCode,
        sourceUrl = p.Source.Url,
        mainImage = p.Images.OrderBy(i => i.Order).FirstOrDefault()?.Url,
        imageCount = p.Images.Count,
        basePrice = new { amount = p.BasePrice.Amount, currency = p.BasePrice.Currency },
        salePrice = p.Variants.Where(v => v.CalculatedPrice is not null)
            .Select(v => (decimal?)v.CalculatedPrice!.Value.Amount).DefaultIfEmpty(null).Min(),
        variantCount = p.Variants.Count,
        totalStock = p.Variants.Sum(v => v.SourceStock),
        createdAt = p.CreatedAt,
        updatedAt = p.UpdatedAt,
    };

    public static object Detail(
        Product p,
        IReadOnlyList<PriceCalculation> calculations,
        ComplianceResult? compliance,
        IReadOnlyList<Listing> listings) => new
        {
            id = p.Id,
            name = p.Name.Get("ko-KR") ?? p.Name.GetOrFirst("ko-KR"),
            names = p.Name.Values,
            description = p.Description.Get("ko-KR") ?? p.Description.GetOrFirst("ko-KR"),
            status = p.Status.ToString(),
            source = new { p.Source.SupplierCode, p.Source.SourceProductId, p.Source.Url },
            sourceCategory = p.SourceCategory,
            images = p.Images.OrderBy(i => i.Order).Select(i => i.Url),
            attributes = p.Attributes,
            // 위탁판매 물류 (출고일·상세이미지 라이선스 포함) — 화면이 계산 없이 쓸 수 있게 파생값까지 준다
            logistics = LogisticsDto(p),
            basePrice = new { amount = p.BasePrice.Amount, currency = p.BasePrice.Currency },
            // 목록 DTO와 동일한 파생값 — 화면이 두 응답을 같은 모양으로 다룰 수 있어야 한다
            salePrice = p.Variants
                .Where(v => v.CalculatedPrice is not null)
                .Select(v => (decimal?)v.CalculatedPrice!.Value.Amount)
                .DefaultIfEmpty(null)
                .Min(),
            totalStock = p.Variants.Sum(v => v.SourceStock),
            optionGroups = p.OptionGroups.Select(g => new
            {
                name = g.TranslatedName ?? g.Name,
                originalName = g.Name,
                values = g.Values.Select(v => new
                {
                    id = v.ValueId,
                    name = v.TranslatedName ?? v.Name,
                    originalName = v.Name,
                    imageUrl = v.ImageUrl,
                }),
            }),
            variants = p.Variants.Select(v => new
            {
                id = v.VariantId,
                sku = v.SourceSkuId,
                options = ResolveOptions(p, v),
                sourcePrice = new { amount = v.SourcePrice.Amount, currency = v.SourcePrice.Currency },
                calculatedPrice = v.CalculatedPrice is null ? null : new { amount = v.CalculatedPrice.Value.Amount },
                stock = v.SourceStock,
            }),
            priceCalculations = calculations.Take(30).Select(c => new
            {
                variantId = c.VariantId,
                sourceCost = new { amount = c.SourceCost.Amount, currency = c.SourceCost.Currency },
                finalPrice = c.FinalPrice.Amount,
                exchangeRate = c.ExchangeRate,
                steps = c.Steps.Select(s => new
                {
                    s.RuleCode,
                    s.Description,
                    before = s.Before.Amount,
                    beforeCurrency = s.Before.Currency,
                    after = s.After.Amount,
                }),
                c.CalculatedAt,
            }),
            compliance = compliance is null ? null : new
            {
                verdict = compliance.Verdict.ToString(),
                hits = compliance.Hits.Select(h => new
                {
                    h.Keyword, h.Field,
                    severity = h.Severity.ToString(),
                    h.Reason,
                }),
                compliance.CheckedAt,
            },
            listings = listings.Select(ListingDto.Of),
            createdAt = p.CreatedAt,
            updatedAt = p.UpdatedAt,
        };

    /// <summary>
    /// 위탁판매 물류 — 원시 속성 대신 해석된 값을 준다.
    /// 링크 미리보기와 같은 투영을 쓴다 (두 화면이 다른 출고지를 보여주면 안 된다).
    /// </summary>
    private static object LogisticsDto(Product p) =>
        ConsignmentLogisticsView.Of(
            ConsignmentLogistics.FromAttributes(p.Attributes, p.BasePrice.Currency));

    private static Dictionary<string, string> ResolveOptions(Product p, Variant v)
    {
        var result = new Dictionary<string, string>();
        foreach (var (groupName, valueId) in v.OptionValueIds)
        {
            var group = p.OptionGroups.FirstOrDefault(g => g.Name == groupName);
            var value = group?.Values.FirstOrDefault(x => x.ValueId == valueId);
            if (group is not null && value is not null)
                result[group.TranslatedName ?? group.Name] = value.TranslatedName ?? value.Name;
        }
        return result;
    }
}

public static class ListingDto
{
    public static object Of(Listing l) => new
    {
        id = l.Id,
        productId = l.ProductId,
        marketCode = l.MarketCode,
        status = l.Status.ToString(),
        marketItemId = l.MarketItemId,
        listedPrice = l.ListedPrice?.Amount,
        lastError = l.LastError,
        syncLogs = l.SyncLogs.TakeLast(10).Select(s => new { s.Action, s.Success, s.Message, s.At }),
        createdAt = l.CreatedAt,
        updatedAt = l.UpdatedAt,
    };
}

public static class JobDto
{
    /// <summary>파이프라인 단계 순서 (프론트 스텝퍼 UI용).</summary>
    public static readonly string[] Stages =
        [nameof(JobStage.Queued), nameof(JobStage.Collecting), nameof(JobStage.Normalizing),
         nameof(JobStage.Enriching), nameof(JobStage.Pricing), nameof(JobStage.Compliance),
         nameof(JobStage.Completed)];

    public static object Of(ScrapeJob j) => new
    {
        id = j.Id,
        url = j.Url,
        supplierCode = j.SupplierCode,
        productId = j.ProductId,
        stage = j.Stage.ToString(),
        stageIndex = Array.IndexOf(Stages, j.Stage.ToString()),
        state = j.State.ToString(),
        attempts = j.Attempts,
        lastError = j.LastError,
        createdAt = j.CreatedAt,
        updatedAt = j.UpdatedAt,
    };
}

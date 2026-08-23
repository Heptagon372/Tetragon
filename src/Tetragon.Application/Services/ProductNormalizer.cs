using Tetragon.Domain.Catalog;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// RawProduct → Universal Product Model 변환 (설계서 5.2).
/// 옵션 정규화 → 이미지 URL 정리 → 속성 추출 순의 파이프라인.
/// </summary>
public sealed class ProductNormalizer
{
    public Product Normalize(RawProduct raw, string tenantId, Guid correlationId)
    {
        var source = new SourceRef(raw.SupplierCode, raw.SourceProductId, raw.Url);

        var images = raw.ImageUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(CleanImageUrl)
            .Distinct()
            .Select((url, i) => new ProductImage { Url = url, Order = i })
            .ToList();

        var optionGroups = raw.OptionGroups.Select(g => new OptionGroup
        {
            Name = g.Name.Trim(),
            Values = g.Values.Select(v => new OptionValue
            {
                ValueId = v.Id,
                Name = CleanOptionName(v.Name),
                ImageUrl = v.ImageUrl,
            }).ToList(),
        }).ToList();

        var variants = raw.Variants.Select(v => new Variant
        {
            SourceSkuId = v.SourceSkuId,
            OptionValueIds = v.OptionValueIds,
            SourcePrice = new Money(v.Price, raw.Currency),
            SourceStock = v.Stock,
            ImageUrl = v.ImageUrl,
        }).ToList();

        // 변형이 없으면 단일 SKU로 취급
        if (variants.Count == 0)
            variants.Add(new Variant
            {
                SourceSkuId = raw.SourceProductId,
                SourcePrice = new Money(raw.BasePrice, raw.Currency),
                SourceStock = 999,
            });

        var basePrice = variants.Min(v => v.SourcePrice.Amount);

        return Product.CreateDraft(
            tenantId,
            source,
            LocalizedText.Of(raw.TitleLocale, raw.Title.Trim()),
            LocalizedText.Of(raw.TitleLocale, raw.Description?.Trim() ?? string.Empty),
            raw.CategoryPath,
            images,
            optionGroups,
            variants,
            new Money(basePrice, raw.Currency),
            raw.Attributes,
            correlationId);
    }

    /// <summary>프로토콜 없는 //img... URL 보정, 쿼리 제거.</summary>
    private static string CleanImageUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("//")) url = "https:" + url;
        return url;
    }

    /// <summary>옵션명 정규화: 공백/특수문자 정리 (설계서 §12 옵션 정규화의 최소 구현).</summary>
    private static string CleanOptionName(string name) =>
        name.Trim().Replace("【", "[").Replace("】", "]");
}

using Tetragon.Domain.Catalog;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// Product + 계산가 → 마켓 중립 ListingPayload 생성 (설계서 5.6 IListingPayloadBuilder).
/// 마켓별 포맷 변환은 각 어댑터가 담당한다.
/// </summary>
public sealed class ListingPayloadBuilder
{
    public ListingPayload Build(Product product)
    {
        var name = product.Name.Get("ko-KR") ?? product.Name.GetOrFirst("ko-KR");
        var description = product.Description.Get("ko-KR") ?? product.Description.GetOrFirst("ko-KR");

        var variants = product.Variants
            .Where(v => v.CalculatedPrice is not null)
            .Select(v => new PayloadVariant
            {
                VariantId = v.VariantId,
                Options = ResolveOptionNames(product, v),
                Price = v.CalculatedPrice!.Value,
                Stock = v.SourceStock,
            })
            .ToList();

        if (variants.Count == 0)
            throw new InvalidOperationException("계산된 가격이 있는 Variant가 없습니다 — 가격 계산 단계를 먼저 수행하세요.");

        var salePrice = variants.Min(v => v.Price.Amount);

        return new ListingPayload
        {
            ProductId = product.Id.ToString(),
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? name : description,
            ImageUrls = product.Images.OrderBy(i => i.Order).Select(i => i.Url).ToList(),
            SalePrice = Money.Krw(salePrice),
            Stock = variants.Sum(v => v.Stock),
            OptionGroups = product.OptionGroups.Select(g => new PayloadOptionGroup(
                g.TranslatedName ?? g.Name,
                g.Values.Select(v => v.TranslatedName ?? v.Name).ToList())).ToList(),
            Variants = variants,
            Attributes = product.Attributes,
            // 위탁판매: 출고지·반품지는 상품마다 다른 공급처 주소를 쓴다
            Logistics = ConsignmentLogistics.FromAttributes(product.Attributes, product.BasePrice.Currency),
        };
    }

    /// <summary>Variant의 (그룹명→값Id) 조합을 번역된 표시 이름으로 해석.</summary>
    private static Dictionary<string, string> ResolveOptionNames(Product product, Variant variant)
    {
        var result = new Dictionary<string, string>();
        foreach (var (groupName, valueId) in variant.OptionValueIds)
        {
            var group = product.OptionGroups.FirstOrDefault(g => g.Name == groupName);
            var value = group?.Values.FirstOrDefault(v => v.ValueId == valueId);
            if (group is null || value is null) continue;
            result[group.TranslatedName ?? group.Name] = value.TranslatedName ?? value.Name;
        }
        return result;
    }
}

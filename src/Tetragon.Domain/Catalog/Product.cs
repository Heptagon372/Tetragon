using Tetragon.SharedKernel;

namespace Tetragon.Domain.Catalog;

/// <summary>
/// Universal Product Model — 프로젝트 전체에서 유일한 상품 모델 (설계서 §4).
/// 모든 스크래퍼는 이 모델로 변환해 반환하고, 이후 모든 서비스는 이 모델만 다룬다.
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public SourceRef Source { get; private set; } = null!;
    public LocalizedText Name { get; private set; } = new();
    public LocalizedText Description { get; private set; } = new();
    public List<string> SourceCategory { get; private set; } = [];
    public List<ProductImage> Images { get; private set; } = [];
    public List<OptionGroup> OptionGroups { get; private set; } = [];
    public List<Variant> Variants { get; private set; } = [];
    public Money BasePrice { get; private set; }
    public ProductStatus Status { get; private set; } = ProductStatus.Draft;
    public Dictionary<string, string> Attributes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    /// <summary>파이프라인 추적 ID (수집 Job과 연결).</summary>
    public Guid CorrelationId { get; private set; }

    private Product() { } // EF Core

    public static Product CreateDraft(
        string tenantId, SourceRef source, LocalizedText name, LocalizedText description,
        List<string> sourceCategory, List<ProductImage> images,
        List<OptionGroup> optionGroups, List<Variant> variants,
        Money basePrice, Dictionary<string, string> attributes, Guid correlationId)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Source = source,
            Name = name,
            Description = description,
            SourceCategory = sourceCategory,
            Images = images,
            OptionGroups = optionGroups,
            Variants = variants,
            BasePrice = basePrice,
            Attributes = attributes,
            CorrelationId = correlationId,
        };
        return product;
    }

    /// <summary>
    /// 상태 전이는 이 상태 머신으로만 허용한다 (설계서 §4 원칙).
    /// Draft → Normalized → Enriched → Priced → Ready → Listed / Blocked
    /// </summary>
    private static readonly Dictionary<ProductStatus, ProductStatus[]> Transitions = new()
    {
        [ProductStatus.Draft] = [ProductStatus.Normalized, ProductStatus.Failed],
        [ProductStatus.Normalized] = [ProductStatus.Enriched, ProductStatus.Failed],
        [ProductStatus.Enriched] = [ProductStatus.Priced, ProductStatus.Failed],
        [ProductStatus.Priced] = [ProductStatus.Ready, ProductStatus.Blocked, ProductStatus.Failed],
        [ProductStatus.Ready] = [ProductStatus.Listed, ProductStatus.Priced],
        [ProductStatus.Blocked] = [ProductStatus.Priced, ProductStatus.Ready], // 사용자 확인 후 재개
        [ProductStatus.Listed] = [ProductStatus.Ready],
        [ProductStatus.Failed] = [ProductStatus.Draft],
    };

    public void TransitionTo(ProductStatus next)
    {
        if (Status == next) return;
        if (!Transitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"허용되지 않는 상태 전이: {Status} → {next}");
        Status = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetTranslatedName(string locale, string value)
    {
        Name = Name.With(locale, value);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetTranslatedDescription(string locale, string value)
    {
        Description = Description.With(locale, value);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RenameOptions(Dictionary<string, string> groupNameMap, Dictionary<string, string> valueNameMap)
    {
        foreach (var group in OptionGroups)
        {
            if (groupNameMap.TryGetValue(group.Name, out var newGroupName))
                group.TranslatedName = newGroupName;
            foreach (var value in group.Values)
                if (valueNameMap.TryGetValue(value.Name, out var newValueName))
                    value.TranslatedName = newValueName;
        }
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ApplyCalculatedPrices(IReadOnlyDictionary<string, Money> pricesByVariantId)
    {
        foreach (var variant in Variants)
            if (pricesByVariantId.TryGetValue(variant.VariantId, out var price))
                variant.CalculatedPrice = price;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>사용자 수동 편집 (PATCH /products/{id}).</summary>
    public void EditManually(string? koreanName, string? koreanDescription)
    {
        if (!string.IsNullOrWhiteSpace(koreanName)) Name = Name.With("ko-KR", koreanName);
        if (!string.IsNullOrWhiteSpace(koreanDescription)) Description = Description.With("ko-KR", koreanDescription);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public enum ProductStatus
{
    Draft,      // 수집 직후 (raw 저장됨)
    Normalized, // 표준 모델 변환 완료
    Enriched,   // 번역/정제 완료
    Priced,     // 가격 계산 완료
    Ready,      // 컴플라이언스 통과, 등록 가능
    Blocked,    // 컴플라이언스 차단 — 사용자 확인 필요
    Listed,     // 1개 이상 마켓 등록됨
    Failed,     // 파이프라인 실패
}

public sealed class ProductImage
{
    public string Url { get; set; } = "";
    public int Order { get; set; }
    public bool IsMain => Order == 0;
}

public sealed class OptionGroup
{
    public string Name { get; set; } = "";          // 원문 (예: "颜色")
    public string? TranslatedName { get; set; }      // 번역 (예: "색상")
    public List<OptionValue> Values { get; set; } = [];
}

public sealed class OptionValue
{
    public string ValueId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? TranslatedName { get; set; }
    public string? ImageUrl { get; set; }
}

/// <summary>옵션 조합 = SKU (설계서 §4 Variant).</summary>
public sealed class Variant
{
    public string VariantId { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceSkuId { get; set; } = "";
    /// <summary>(옵션그룹명 → 옵션값 Id) 조합.</summary>
    public Dictionary<string, string> OptionValueIds { get; set; } = [];
    public Money SourcePrice { get; set; }
    public int SourceStock { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>Pricing Engine 계산 결과.</summary>
    public Money? CalculatedPrice { get; set; }
}

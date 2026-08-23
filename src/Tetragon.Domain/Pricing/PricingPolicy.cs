using Tetragon.SharedKernel;

namespace Tetragon.Domain.Pricing;

/// <summary>가격 정책 = 순서 있는 Rule 구성의 집합 (설계서 5.4).</summary>
public sealed class PricingPolicy : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string Name { get; private set; } = "";
    public bool IsDefault { get; private set; }
    public List<PriceRuleConfig> Rules { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private PricingPolicy() { }

    public static PricingPolicy Create(string tenantId, string name, List<PriceRuleConfig> rules, bool isDefault = false)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Rules = [.. rules.OrderBy(r => r.Order)],
            IsDefault = isDefault,
        };

    public void Update(string name, List<PriceRuleConfig> rules)
    {
        Name = name;
        Rules = [.. rules.OrderBy(r => r.Order)];
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;

    /// <summary>기본 정책 프리셋: 환율 → 해외배송비 → 관세/부가세 → 마진 → 마켓수수료 역산 → 심리가격 절사.</summary>
    public static PricingPolicy CreateDefaultPreset(string tenantId) =>
        Create(tenantId, "기본 정책", [
            new PriceRuleConfig("exchange-rate", 1, []),
            new PriceRuleConfig("intl-shipping", 2, new() { ["amountKrw"] = 6000m }),
            new PriceRuleConfig("tariff-vat", 3, new() { ["tariffPct"] = 8m, ["vatPct"] = 10m, ["thresholdUsd"] = 150m }),
            new PriceRuleConfig("margin", 4, new() { ["marginPct"] = 30m, ["marginFixedKrw"] = 0m }),
            new PriceRuleConfig("market-fee", 5, new() { ["feePct"] = 13m }),
            new PriceRuleConfig("psych-rounding", 6, new() { ["unit"] = 100m, ["endsWith"] = 900m }),
        ], isDefault: true);
}

/// <summary>정책에 포함된 Rule 1건의 구성 (RuleCode + 순서 + 파라미터).</summary>
public sealed record PriceRuleConfig(string RuleCode, int Order, Dictionary<string, decimal> Parameters);

public static class PricingPolicyExtensions
{
    /// <summary>
    /// 마진율만 바꾼 사본을 만든다 (저장하지 않는 미리보기용).
    ///
    /// 가격 정책 화면에서 마진 슬라이더를 움직일 때마다 정책을 저장하면
    /// 이미 등록된 상품 가격까지 흔들린다. 그래서 계산에만 쓰는 사본을 만든다.
    /// </summary>
    public static PricingPolicy WithMarginOverride(this PricingPolicy policy, decimal marginPct) =>
        PricingPolicy.Create(policy.TenantId, policy.Name, policy.Rules
            .Select(r => r.RuleCode == "margin"
                ? r with { Parameters = new Dictionary<string, decimal>(r.Parameters) { ["marginPct"] = marginPct } }
                : r)
            .ToList());
}

/// <summary>계산 이력 스냅샷 (설계서 5.4 PriceCalculation 테이블).</summary>
public sealed class PriceCalculation
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = Tenant.Default;
    public Guid ProductId { get; set; }
    public string VariantId { get; set; } = "";
    public Guid PolicyId { get; set; }
    public Money SourceCost { get; set; }
    public Money FinalPrice { get; set; }
    public decimal ExchangeRate { get; set; }
    /// <summary>Rule별 적용 내역 (JSON 스냅샷).</summary>
    public List<PriceStep> Steps { get; set; } = [];
    public DateTimeOffset CalculatedAt { get; set; } = DateTimeOffset.UtcNow;
}

using Tetragon.Application.Ports;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Pricing;
using Tetragon.SharedKernel;

namespace Tetragon.Application.Services;

/// <summary>
/// Rule Engine 실행기 (설계서 5.4).
/// DI에 등록된 IPriceRule 구현체들을 정책의 Rule 구성 순서대로 적용한다.
/// </summary>
public sealed class PricingEngine
{
    private readonly Dictionary<string, IPriceRule> _rules;
    private readonly IExchangeRateProvider _exchangeRates;

    public PricingEngine(IEnumerable<IPriceRule> rules, IExchangeRateProvider exchangeRates)
    {
        _rules = rules.ToDictionary(r => r.RuleCode);
        _exchangeRates = exchangeRates;
    }

    public IReadOnlyList<IPriceRule> AvailableRules => _rules.Values.ToList();

    /// <summary>상품의 모든 Variant 가격을 계산하고 스냅샷을 반환한다.</summary>
    public async Task<IReadOnlyList<PriceCalculation>> CalculateAsync(
        Product product, PricingPolicy policy, CancellationToken ct)
    {
        var results = new List<PriceCalculation>();

        foreach (var variant in product.Variants)
        {
            var rate = variant.SourcePrice.Currency == "KRW"
                ? 1m
                : await _exchangeRates.GetRateAsync(variant.SourcePrice.Currency, "KRW", ct);

            var ctx = new PriceContext
            {
                Current = variant.SourcePrice,
                SourceCost = variant.SourcePrice,
                ExchangeRate = rate,
                // 원가가 원화면 국내 소싱 — 해외배송비·관세 Rule이 스스로 빠진다
                IsDomestic = variant.SourcePrice.Currency == "KRW",
            };

            foreach (var config in policy.Rules.OrderBy(r => r.Order))
            {
                if (!_rules.TryGetValue(config.RuleCode, out var rule))
                    throw new InvalidOperationException($"등록되지 않은 가격 Rule: {config.RuleCode}");
                ctx = rule.Apply(ctx, config.Parameters);
            }

            results.Add(new PriceCalculation
            {
                Id = Guid.NewGuid(),
                TenantId = product.TenantId,
                ProductId = product.Id,
                VariantId = variant.VariantId,
                PolicyId = policy.Id,
                SourceCost = variant.SourcePrice,
                FinalPrice = ctx.Current,
                ExchangeRate = rate,
                Steps = ctx.Steps,
            });
        }

        return results;
    }
}

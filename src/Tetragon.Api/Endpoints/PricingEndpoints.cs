using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Domain.Pricing;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Api.Endpoints;

public static class PricingEndpoints
{
    public static void MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pricing-policies").WithTags("Pricing");

        // 사용 가능한 Rule 목록 (프론트 정책 빌더용)
        group.MapGet("/rules", (PricingEngine engine) =>
            Results.Ok(engine.AvailableRules.Select(r => new
            {
                r.RuleCode,
                r.DisplayName,
                parameters = r.ParameterSpecs.Select(p => new { p.Key, p.Label, p.DefaultValue }),
            })))
        .WithSummary("등록된 가격 Rule 목록");

        group.MapGet("/", async (IPricingPolicyRepository policies, CancellationToken ct) =>
        {
            var all = await policies.AllAsync(ct);
            return Results.Ok(all.Select(PolicyDto));
        })
        .WithSummary("가격 정책 목록");

        group.MapPost("/", async (
            PricingPolicyRequest request, IPricingPolicyRepository policies, CancellationToken ct) =>
        {
            var rules = request.Rules
                .Select(r => new PriceRuleConfig(r.RuleCode, r.Order, r.Parameters))
                .ToList();
            var policy = PricingPolicy.Create(Tenant.Default, request.Name, rules, request.IsDefault);

            if (request.IsDefault)
            {
                // 기존 기본 정책 해제
                var existing = await policies.FindDefaultAsync(ct);
                existing?.SetDefault(false);
            }

            await policies.AddAsync(policy, ct);
            await policies.SaveAsync(ct);
            return Results.Created($"/api/v1/pricing-policies/{policy.Id}", PolicyDto(policy));
        })
        .WithSummary("가격 정책 생성");

        group.MapPut("/{id:guid}", async (
            Guid id, PricingPolicyRequest request, IPricingPolicyRepository policies, CancellationToken ct) =>
        {
            var policy = await policies.FindAsync(id, ct);
            if (policy is null) return Results.NotFound();

            var rules = request.Rules.Select(r => new PriceRuleConfig(r.RuleCode, r.Order, r.Parameters)).ToList();
            policy.Update(request.Name, rules);

            if (request.IsDefault && !policy.IsDefault)
            {
                var existing = await policies.FindDefaultAsync(ct);
                existing?.SetDefault(false);
                policy.SetDefault(true);
            }

            await policies.SaveAsync(ct);
            return Results.Ok(PolicyDto(policy));
        })
        .WithSummary("가격 정책 수정");

        // 시뮬레이션 (설계서 §8)
        group.MapPost("/{id:guid}/simulate", async (
            Guid id, SimulateRequest request,
            IPricingPolicyRepository policies, IProductRepository products,
            PricingEngine engine, CancellationToken ct) =>
        {
            var policy = await policies.FindAsync(id, ct);
            if (policy is null) return Results.NotFound(new { error = "정책을 찾을 수 없습니다." });
            var product = await products.FindAsync(request.ProductId, ct);
            if (product is null) return Results.NotFound(new { error = "상품을 찾을 수 없습니다." });

            // 마진율을 즉석에서 바꿔 볼 수 있게 한다 (정책을 저장하지 않고 미리보기)
            var effectivePolicy = request.MarginPct is { } overrideMargin
                ? policy.WithMarginOverride(overrideMargin)
                : policy;

            var results = await engine.CalculateAsync(product, effectivePolicy, ct);

            // 마켓 수수료율은 정책의 market-fee Rule에서 가져온다
            var marketFeePct = effectivePolicy.Rules
                .FirstOrDefault(r => r.RuleCode == "market-fee")?
                .Parameters.GetValueOrDefault("feePct", 13m) ?? 13m;

            // 공급처 배송비를 우리가 부담하면 마진에서 빠진다
            var logistics = ConsignmentLogistics.FromAttributes(product.Attributes, product.BasePrice.Currency);
            var supplierShipping = logistics.DeliveryFee ?? 0m;

            var assessments = results.Select(c => new
            {
                calc = c,
                health = MarginHealth.Assess(
                    c.FinalPrice, c.SourceCost, c.ExchangeRate, marketFeePct, supplierShipping),
            }).ToList();

            var worst = assessments
                .OrderBy(a => a.health.Profit)
                .FirstOrDefault()?.health;

            return Results.Ok(new
            {
                policyId = policy.Id,
                policyName = policy.Name,
                productId = product.Id,
                isDomestic = product.BasePrice.Currency == "KRW",
                marketFeePct,
                supplierShippingFee = supplierShipping,
                appliedMarginPct = request.MarginPct,
                // 가장 나쁜 SKU 기준으로 경고한다 — 하나라도 적자면 알아야 한다
                margin = worst is null ? null : new
                {
                    level = worst.Level.ToString(),
                    worst.Message,
                    worst.Profit,
                    worst.MarginPct,
                    worst.MarketFee,
                    worst.NetRevenue,
                    worst.CostKrw,
                    worst.BreakEvenPrice,
                    worst.IsLoss,
                    worst.NeedsAttention,
                },
                calculations = assessments.Select(a => new
                {
                    a.calc.VariantId,
                    sourceCost = new { amount = a.calc.SourceCost.Amount, currency = a.calc.SourceCost.Currency },
                    finalPrice = a.calc.FinalPrice.Amount,
                    a.calc.ExchangeRate,
                    margin = new
                    {
                        level = a.health.Level.ToString(),
                        a.health.Profit,
                        a.health.MarginPct,
                        a.health.BreakEvenPrice,
                    },
                    steps = a.calc.Steps.Select(s => new
                    {
                        s.RuleCode, s.Description,
                        before = s.Before.Amount, beforeCurrency = s.Before.Currency,
                        after = s.After.Amount,
                        // 적용되지 않은 Rule은 금액이 그대로다 — UI가 흐리게 표시할 수 있게
                        applied = s.Before.Amount != s.After.Amount,
                    }),
                }),
            });
        })
        .WithSummary("가격 시뮬레이션 (저장하지 않음)");
    }

    private static object PolicyDto(PricingPolicy p) => new
    {
        id = p.Id,
        name = p.Name,
        isDefault = p.IsDefault,
        rules = p.Rules.Select(r => new { r.RuleCode, r.Order, r.Parameters }),
        createdAt = p.CreatedAt,
    };
}

using Tetragon.SharedKernel;

namespace Tetragon.Domain.Pricing.Rules;

/// <summary>원가(공급처 통화) → KRW 환산.</summary>
public sealed class ExchangeRateRule : IPriceRule
{
    public string RuleCode => "exchange-rate";
    public string DisplayName => "환율 적용";
    public IReadOnlyList<RuleParameterSpec> ParameterSpecs => [];

    public PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> p)
    {
        if (ctx.Current.Currency == "KRW") return ctx;
        var converted = ctx.Current.ConvertTo("KRW", ctx.ExchangeRate);
        return ctx.Next(RuleCode, $"{ctx.Current.Currency}→KRW 환율 {ctx.ExchangeRate:N2}", converted);
    }
}

/// <summary>
/// 해외 배송비 (정액 KRW).
/// 국내 도매(도매꾹·도매매·11번가)는 해외에서 들여오는 것이 아니므로 붙지 않는다.
/// </summary>
public sealed class IntlShippingRule : IPriceRule
{
    public string RuleCode => "intl-shipping";
    public string DisplayName => "해외 배송비";
    public IReadOnlyList<RuleParameterSpec> ParameterSpecs =>
        [new("amountKrw", "배송비(원)", 6000m)];

    public PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> p)
    {
        if (ctx.IsDomestic)
            return ctx.Skip(RuleCode, "국내 도매 — 해외배송비 없음");

        var fee = p.GetValueOrDefault("amountKrw", 6000m);
        return ctx.Next(RuleCode, $"해외배송비 +{fee:N0}원", ctx.Current.Add(Money.Krw(fee)));
    }
}

/// <summary>관세/부가세 — 과세 기준(USD 150) 초과 시 적용.</summary>
public sealed class TariffVatRule : IPriceRule
{
    public string RuleCode => "tariff-vat";
    public string DisplayName => "관세/부가세";
    public IReadOnlyList<RuleParameterSpec> ParameterSpecs =>
    [
        new("tariffPct", "관세율(%)", 8m),
        new("vatPct", "부가세율(%)", 10m),
        new("thresholdUsd", "과세 기준(USD)", 150m),
    ];

    public PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> p)
    {
        if (ctx.IsDomestic)
            return ctx.Skip(RuleCode, "국내 도매 — 통관 없음");

        var thresholdUsd = p.GetValueOrDefault("thresholdUsd", 150m);
        // 근사: KRW 금액을 1350원/USD로 환산해 기준 비교 (정밀 환율은 exchange-rate Rule이 담당)
        var approxUsd = ctx.Current.Amount / 1350m;
        if (approxUsd <= thresholdUsd)
            return ctx.Next(RuleCode, $"목록통관 기준({thresholdUsd:N0} USD) 이하 — 면세", ctx.Current);

        var tariff = p.GetValueOrDefault("tariffPct", 8m) / 100m;
        var vat = p.GetValueOrDefault("vatPct", 10m) / 100m;
        var withTariff = ctx.Current.Multiply(1 + tariff);
        var withVat = withTariff.Multiply(1 + vat);
        return ctx.Next(RuleCode, $"관세 {tariff:P0} + 부가세 {vat:P0}", withVat);
    }
}

/// <summary>마진 (% + 정액).</summary>
public sealed class MarginRule : IPriceRule
{
    public string RuleCode => "margin";
    public string DisplayName => "마진";
    public IReadOnlyList<RuleParameterSpec> ParameterSpecs =>
    [
        new("marginPct", "마진율(%)", 30m),
        new("marginFixedKrw", "정액 마진(원)", 0m),
    ];

    public PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> p)
    {
        var pct = p.GetValueOrDefault("marginPct", 30m) / 100m;
        var fixedAmt = p.GetValueOrDefault("marginFixedKrw", 0m);
        var next = ctx.Current.Multiply(1 + pct).Add(Money.Krw(fixedAmt));
        return ctx.Next(RuleCode, $"마진 {pct:P0}" + (fixedAmt > 0 ? $" + {fixedAmt:N0}원" : ""), next);
    }
}

/// <summary>마켓 수수료 역산 — 수수료 차감 후에도 목표 금액이 남도록 판매가를 올린다.</summary>
public sealed class MarketFeeRule : IPriceRule
{
    public string RuleCode => "market-fee";
    public string DisplayName => "마켓 수수료 역산";
    public IReadOnlyList<RuleParameterSpec> ParameterSpecs =>
        [new("feePct", "수수료율(%)", 13m)];

    public PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> p)
    {
        var fee = p.GetValueOrDefault("feePct", 13m) / 100m;
        if (fee >= 1m) throw new InvalidOperationException("수수료율은 100% 미만이어야 합니다.");
        var next = ctx.Current with { Amount = ctx.Current.Amount / (1 - fee) };
        return ctx.Next(RuleCode, $"수수료 {fee:P0} 역산 (판매가 ÷ {1 - fee:0.##})", next);
    }
}

/// <summary>심리가격 절사 (예: 100원 단위 내림 후 끝자리 900원).</summary>
public sealed class PsychRoundingRule : IPriceRule
{
    public string RuleCode => "psych-rounding";
    public string DisplayName => "심리가격 절사";
    public IReadOnlyList<RuleParameterSpec> ParameterSpecs =>
    [
        new("unit", "절사 단위(원)", 100m),
        new("endsWith", "끝자리(원, 0=사용안함)", 900m),
    ];

    public PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> p)
    {
        var unit = (int)p.GetValueOrDefault("unit", 100m);
        var endsWith = p.GetValueOrDefault("endsWith", 900m);
        var floored = ctx.Current.FloorTo(Math.Max(unit, 1));
        if (endsWith > 0)
        {
            var thousand = Math.Floor(floored.Amount / 1000m) * 1000m;
            var candidate = thousand + endsWith;
            // 절사 결과보다 커지지 않게: 9,900 스타일은 한 단계 아래 천 단위 + 끝자리
            if (candidate > floored.Amount) candidate = thousand - 1000m + endsWith;
            if (candidate > 0) floored = floored with { Amount = candidate };
        }
        return ctx.Next(RuleCode, $"{unit}원 단위 절사, 끝자리 {endsWith:N0}", floored);
    }
}

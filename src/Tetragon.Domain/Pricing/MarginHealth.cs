using Tetragon.SharedKernel;

namespace Tetragon.Domain.Pricing;

/// <summary>
/// 판매가가 실제로 남는 가격인지 판정한다.
///
/// 위탁판매에서 적자가 나는 경로는 눈에 잘 안 띈다:
///   - 마켓 수수료(약 11~13%)를 빼면 손에 쥐는 돈이 확 준다
///   - 공급처 배송비를 우리가 부담하면 그만큼 더 빠진다
///   - 국내 도매에 해외배송비 Rule이 잘못 붙으면 판매가가 부풀어 안 팔린다
///
/// 그래서 "판매가 − 수수료 − 원가 − 배송비"로 실수령 마진을 계산하고,
/// 적자거나 너무 얇으면 등록 전에 알린다.
/// </summary>
public static class MarginHealth
{
    /// <summary>이 아래면 사실상 남는 게 없다고 본다 (기본 10%).</summary>
    public const decimal DefaultThinMarginPct = 10m;

    public static MarginAssessment Assess(
        Money salePrice,
        Money sourceCost,
        decimal exchangeRate,
        decimal marketFeePct,
        decimal supplierShippingFee = 0m,
        decimal thinMarginPct = DefaultThinMarginPct)
    {
        var sale = salePrice.Amount;

        // 원가를 원화로 맞춘다 (해외 소싱이면 환율 적용)
        var costKrw = sourceCost.Currency == "KRW"
            ? sourceCost.Amount
            : sourceCost.Amount * exchangeRate;

        var fee = sale * (marketFeePct / 100m);
        var netRevenue = sale - fee;                       // 마켓이 정산해 주는 금액
        var totalCost = costKrw + supplierShippingFee;     // 우리가 실제로 쓰는 돈
        var profit = netRevenue - totalCost;
        var marginPct = sale > 0 ? profit / sale * 100m : 0m;

        var level = profit < 0 ? MarginLevel.Loss
            : marginPct < thinMarginPct ? MarginLevel.Thin
            : MarginLevel.Healthy;

        return new MarginAssessment
        {
            SalePrice = sale,
            CostKrw = costKrw,
            SupplierShippingFee = supplierShippingFee,
            MarketFee = fee,
            NetRevenue = netRevenue,
            Profit = profit,
            MarginPct = marginPct,
            Level = level,
            /// 손익분기 판매가 = (원가 + 배송비) ÷ (1 − 수수료율)
            BreakEvenPrice = marketFeePct < 100m
                ? totalCost / (1m - marketFeePct / 100m)
                : totalCost,
            Message = BuildMessage(level, profit, marginPct, thinMarginPct),
        };
    }

    private static string BuildMessage(
        MarginLevel level, decimal profit, decimal marginPct, decimal thinMarginPct) => level switch
        {
            MarginLevel.Loss =>
                $"적자입니다. 한 개 팔 때마다 {Math.Abs(profit):N0}원 손해입니다 — " +
                "마진율을 올리거나 판매가를 조정하세요.",
            MarginLevel.Thin =>
                $"마진이 {marginPct:N1}%로 얇습니다 (기준 {thinMarginPct:N0}%). " +
                "반품이나 배송 사고가 한 번만 나도 손해로 돌아섭니다.",
            _ => $"마진 {marginPct:N1}% — 정상입니다.",
        };
}

public enum MarginLevel
{
    Healthy,
    Thin,
    Loss,
}

public sealed record MarginAssessment
{
    public decimal SalePrice { get; init; }
    public decimal CostKrw { get; init; }
    public decimal SupplierShippingFee { get; init; }
    /// <summary>마켓이 떼는 판매 수수료.</summary>
    public decimal MarketFee { get; init; }
    /// <summary>수수료를 뺀 실수령액.</summary>
    public decimal NetRevenue { get; init; }
    /// <summary>실수령액 − 원가 − 배송비.</summary>
    public decimal Profit { get; init; }
    /// <summary>판매가 대비 순이익률.</summary>
    public decimal MarginPct { get; init; }
    /// <summary>이 가격 아래로는 팔면 손해인 지점.</summary>
    public decimal BreakEvenPrice { get; init; }
    public MarginLevel Level { get; init; }
    public string Message { get; init; } = "";

    public bool IsLoss => Level == MarginLevel.Loss;
    public bool NeedsAttention => Level != MarginLevel.Healthy;
}

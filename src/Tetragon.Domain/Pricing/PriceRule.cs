using Tetragon.SharedKernel;

namespace Tetragon.Domain.Pricing;

/// <summary>
/// 가격 Rule (설계서 5.4). Rule은 순서를 가진 파이프라인이며 정책은 Rule의 조합이다.
/// 새 정책 = 새 IPriceRule 구현체 등록만으로 적용, 기존 Rule 수정 금지 (OCP).
/// </summary>
public interface IPriceRule
{
    string RuleCode { get; }
    string DisplayName { get; }
    /// <summary>불변 컨텍스트 변환.</summary>
    PriceContext Apply(PriceContext ctx, Dictionary<string, decimal> parameters);
    /// <summary>이 Rule이 받는 파라미터 정의 (UI 정책 빌더용).</summary>
    IReadOnlyList<RuleParameterSpec> ParameterSpecs { get; }
}

public sealed record RuleParameterSpec(string Key, string Label, decimal DefaultValue);

/// <summary>Rule 파이프라인을 흐르는 불변 계산 컨텍스트.</summary>
public sealed record PriceContext
{
    /// <summary>현재 계산 중인 금액.</summary>
    public required Money Current { get; init; }
    /// <summary>원가 (공급처 통화).</summary>
    public required Money SourceCost { get; init; }
    /// <summary>공급처통화 → KRW 환율.</summary>
    public decimal ExchangeRate { get; init; } = 1m;

    /// <summary>
    /// 국내 소싱 여부 (도매꾹·도매매·11번가 등).
    ///
    /// 국내 도매 상품은 <b>해외배송비도 관세도 발생하지 않는다.</b>
    /// 그런데도 그 Rule들을 적용하면 원가에 없는 비용이 얹혀 판매가가 부풀고,
    /// 결과적으로 안 팔리거나 마진 계산이 틀어진다.
    /// (실제로 4,664원짜리 도매꾹 에코백에 해외배송비 6,000원이 붙어 15,900원이 됐다)
    ///
    /// 그래서 Rule이 스스로 "나는 국내 상품엔 적용되지 않는다"를 판단하게 한다.
    /// 정책에서 Rule을 빼는 방식은 국내/해외 정책을 따로 만들어야 해서 관리가 어렵다.
    /// </summary>
    public bool IsDomestic { get; init; }

    /// <summary>Rule별 적용 내역 스냅샷 — "이 가격이 왜 나왔는지" 추적 (설계서 5.4).</summary>
    public List<PriceStep> Steps { get; init; } = [];

    public PriceContext Next(string ruleCode, string description, Money next) =>
        this with
        {
            Current = next,
            Steps = [.. Steps, new PriceStep(ruleCode, description, Current, next)],
        };

    /// <summary>적용하지 않았음을 이력에 남긴다 — 왜 안 붙었는지도 추적 가능해야 한다.</summary>
    public PriceContext Skip(string ruleCode, string reason) =>
        this with { Steps = [.. Steps, new PriceStep(ruleCode, reason, Current, Current)] };
}

public sealed record PriceStep(string RuleCode, string Description, Money Before, Money After);

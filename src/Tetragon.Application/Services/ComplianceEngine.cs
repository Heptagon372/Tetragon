using Tetragon.Application.Ports;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Compliance;

namespace Tetragon.Application.Services;

/// <summary>
/// 금지어/제한 검사 엔진 (설계서 5.5).
/// Aho-Corasick으로 상품명/상세/옵션을 일괄 검사한다.
/// </summary>
public sealed class ComplianceEngine(IComplianceRepository repository)
{
    public async Task<ComplianceResult> CheckAsync(Product product, CancellationToken ct)
    {
        var rules = await repository.RulesAsync(ct);
        var byKeyword = new Dictionary<string, ComplianceRule>(StringComparer.OrdinalIgnoreCase);
        var matcher = new AhoCorasickMatcher();
        foreach (var rule in rules)
        {
            matcher.AddKeyword(rule.Keyword);
            byKeyword[rule.Keyword] = rule;
        }
        matcher.Build();

        var hits = new List<ComplianceHit>();
        void Check(string field, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var keyword in matcher.Match(text))
                if (byKeyword.TryGetValue(keyword, out var rule))
                    hits.Add(new ComplianceHit(keyword, field, rule.Severity, rule.Reason));
        }

        Check("상품명", product.Name.Get("ko-KR") ?? product.Name.GetOrFirst("ko-KR"));
        Check("상세설명", product.Description.Get("ko-KR"));
        foreach (var group in product.OptionGroups)
        {
            Check("옵션", group.TranslatedName ?? group.Name);
            foreach (var value in group.Values)
                Check("옵션", value.TranslatedName ?? value.Name);
        }

        var verdict = hits.Any(h => h.Severity == ComplianceSeverity.Block) ? ComplianceVerdict.Block
            : hits.Count > 0 ? ComplianceVerdict.Warn
            : ComplianceVerdict.Pass;

        var result = new ComplianceResult
        {
            Id = Guid.NewGuid(),
            TenantId = product.TenantId,
            ProductId = product.Id,
            Verdict = verdict,
            Hits = hits.DistinctBy(h => (h.Keyword, h.Field)).ToList(),
        };
        await repository.AddResultAsync(result, ct);
        await repository.SaveAsync(ct);
        return result;
    }

    /// <summary>기본 금지어 시드 (첫 실행 시).</summary>
    public static IReadOnlyList<ComplianceRule> DefaultSeedRules() =>
    [
        ComplianceRule.Create("정품", ComplianceSeverity.Block, "정품 표기는 증빙 필요 — 브랜드권 침해 위험"),
        ComplianceRule.Create("명품", ComplianceSeverity.Block, "브랜드 위조품 오인 위험"),
        ComplianceRule.Create("최저가", ComplianceSeverity.Warn, "표시광고법 — 근거 필요"),
        ComplianceRule.Create("최고", ComplianceSeverity.Warn, "표시광고법 — 근거 필요"),
        ComplianceRule.Create("1위", ComplianceSeverity.Warn, "표시광고법 — 근거 필요"),
        ComplianceRule.Create("의약품", ComplianceSeverity.Block, "약사법 — 판매 불가 카테고리"),
        ComplianceRule.Create("치료", ComplianceSeverity.Block, "의료 효능 표방 금지"),
        ComplianceRule.Create("다이어트 효과", ComplianceSeverity.Block, "효능 표방 금지"),
        ComplianceRule.Create("KC인증", ComplianceSeverity.Warn, "인증번호 확인 필요"),
        ComplianceRule.Create("전자담배", ComplianceSeverity.Block, "판매 제한 품목"),
        ComplianceRule.Create("nike", ComplianceSeverity.Block, "상표권 — 위조품 위험 브랜드"),
        ComplianceRule.Create("나이키", ComplianceSeverity.Block, "상표권 — 위조품 위험 브랜드"),
        ComplianceRule.Create("아디다스", ComplianceSeverity.Block, "상표권 — 위조품 위험 브랜드"),
        ComplianceRule.Create("샤넬", ComplianceSeverity.Block, "상표권 — 위조품 위험 브랜드"),
        ComplianceRule.Create("루이비통", ComplianceSeverity.Block, "상표권 — 위조품 위험 브랜드"),
        ComplianceRule.Create("구찌", ComplianceSeverity.Block, "상표권 — 위조품 위험 브랜드"),
    ];
}

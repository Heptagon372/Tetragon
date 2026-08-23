using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Catalog;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Services;

/// <summary>
/// 중복 등록·아이템위너 위험 점검.
///
/// 위탁판매에서 같은 도매 상품을 여러 셀러가 동시에 올리는 일은 흔하다.
/// 그 결과 두 가지 문제가 생긴다:
///   1. <b>내가 같은 상품을 두 번 올리는 것</b> — 쿠팡이 중복으로 보고 노출을 깎는다
///   2. <b>아이템위너 경쟁</b> — 같은 상품에 여러 셀러가 붙어 최저가 싸움이 된다
///
/// 1번은 우리 데이터만으로 확실히 잡을 수 있다.
/// 2번은 쿠팡 전체 검색이 필요한데 Akamai 봇 차단으로 HTTP 수집이 막혀 있어,
/// 지금은 <b>도매 상품 특성으로 위험도를 추정</b>한다.
/// 같은 공급처의 같은 상품을 여러 셀러가 파는 구조 자체가 위험 신호다.
/// </summary>
public sealed class DuplicateAudit(
    IProductRepository products,
    IListingRepository listings,
    ILogger<DuplicateAudit> logger)
{
    public async Task<DuplicateAuditResult> RunAsync(int limit, CancellationToken ct)
    {
        var (items, _) = await products.SearchAsync(null, null, 1, Math.Clamp(limit, 1, 2000), ct);

        var duplicates = new List<DuplicateGroup>();
        var risky = new List<ItemWinnerRisk>();

        // ── 1. 내부 중복 — 같은 상품을 여러 번 수집한 경우 ──────────────
        // 같은 공급처 + 같은 원본 상품번호면 명백한 중복이다.
        foreach (var group in items.GroupBy(p => (p.Source.SupplierCode, p.Source.SourceProductId)))
        {
            if (group.Count() <= 1) continue;
            duplicates.Add(new DuplicateGroup
            {
                Kind = "동일 원본",
                Key = $"{group.Key.SupplierCode}/{group.Key.SourceProductId}",
                Reason = "같은 공급처의 같은 상품을 여러 번 수집했습니다. 하나만 남기고 정리하세요.",
                Products = group.Select(ToRef).ToList(),
            });
        }

        // 상품명이 똑같으면 공급처가 달라도 같은 물건일 가능성이 크다
        foreach (var group in items.GroupBy(p => NormalizeName(DisplayName(p))))
        {
            if (group.Key.Length < 8 || group.Count() <= 1) continue;
            var distinctSources = group
                .Select(p => (p.Source.SupplierCode, p.Source.SourceProductId))
                .Distinct()
                .Count();
            if (distinctSources <= 1) continue;   // 위에서 이미 잡았다

            duplicates.Add(new DuplicateGroup
            {
                Kind = "동일 상품명",
                Key = group.Key[..Math.Min(group.Key.Length, 40)],
                Reason = "상품명이 같습니다. 공급처만 다른 같은 물건일 수 있어 중복 등록으로 걸릴 수 있습니다.",
                Products = group.Select(ToRef).ToList(),
            });
        }

        // ── 2. 아이템위너 위험 추정 ──────────────────────────────────────
        foreach (var product in items)
        {
            var signals = new List<string>();
            var name = DisplayName(product);

            // 도매 상품은 여러 셀러가 같은 사진·상품명으로 올린다.
            // 공급처가 준 상품명을 그대로 쓰면 그 자체가 경쟁에 뛰어드는 것이다.
            if (product.Attributes.GetValueOrDefault(LogisticsKeys.SupplierName) is { Length: > 0 })
                signals.Add("도매 공급처 상품 — 다른 셀러도 같은 물건을 팔 수 있습니다");

            // 브랜드 없는 노브랜드 상품은 아이템위너 묶임이 특히 잦다
            var brand = product.Attributes.GetValueOrDefault("브랜드");
            if (string.IsNullOrWhiteSpace(brand) || brand is "브랜드 없음" or "상세정보참조")
                signals.Add("브랜드가 없어 동일 상품으로 묶이기 쉽습니다");

            // 최소구매수량이 1이면 진입장벽이 낮아 경쟁 셀러가 많다
            if (int.TryParse(product.Attributes.GetValueOrDefault(LogisticsKeys.MinOrderQty), out var moq)
                && moq <= 1)
                signals.Add("최소구매수량이 1개라 누구나 소량으로 시작할 수 있습니다");

            // 상품명을 손대지 않았으면 검색에서 그대로 겹친다
            if (product.Name.Values.Count == 1)
                signals.Add("공급처 상품명을 그대로 사용 중 — 상품명을 차별화하면 경쟁이 줄어듭니다");

            if (signals.Count < 3) continue;   // 신호가 약하면 굳이 알리지 않는다

            risky.Add(new ItemWinnerRisk
            {
                ProductId = product.Id,
                ProductName = name,
                SupplierName = product.Attributes.GetValueOrDefault(LogisticsKeys.SupplierName),
                SourceUrl = product.Source.Url,
                Level = signals.Count >= 4 ? "High" : "Medium",
                Signals = signals,
            });
        }

        logger.LogInformation("중복 점검: {Products}건 중 중복 그룹 {Groups}개, 아이템위너 위험 {Risky}건",
            items.Count, duplicates.Count, risky.Count);

        return new DuplicateAuditResult
        {
            ScannedCount = items.Count,
            Duplicates = duplicates.OrderByDescending(d => d.Products.Count).Take(100).ToList(),
            ItemWinnerRisks = risky.OrderByDescending(r => r.Signals.Count).Take(200).ToList(),
        };
    }

    private static string DisplayName(Product p) =>
        p.Name.Get("ko-KR") ?? p.Name.GetOrFirst("ko-KR");

    /// <summary>비교용 정규화 — 공백·특수문자·괄호 내용을 걷어낸다.</summary>
    private static string NormalizeName(string name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"\[[^\]]*\]|\([^)]*\)", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[^\w가-힣]", "");
        return cleaned.ToLowerInvariant();
    }

    private static ProductRef ToRef(Product p) => new()
    {
        ProductId = p.Id,
        ProductName = DisplayName(p),
        SupplierCode = p.Source.SupplierCode,
        SupplierName = p.Attributes.GetValueOrDefault(LogisticsKeys.SupplierName),
        Status = p.Status.ToString(),
        SourceUrl = p.Source.Url,
    };
}

public sealed record DuplicateAuditResult
{
    public int ScannedCount { get; init; }
    public List<DuplicateGroup> Duplicates { get; init; } = [];
    public List<ItemWinnerRisk> ItemWinnerRisks { get; init; } = [];
}

public sealed record DuplicateGroup
{
    public string Kind { get; init; } = "";
    public string Key { get; init; } = "";
    public string Reason { get; init; } = "";
    public List<ProductRef> Products { get; init; } = [];
}

public sealed record ProductRef
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public string SupplierCode { get; init; } = "";
    public string? SupplierName { get; init; }
    public string Status { get; init; } = "";
    public string SourceUrl { get; init; } = "";
}

public sealed record ItemWinnerRisk
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public string? SupplierName { get; init; }
    public string SourceUrl { get; init; } = "";
    /// <summary>High / Medium.</summary>
    public string Level { get; init; } = "Medium";
    public List<string> Signals { get; init; } = [];
}

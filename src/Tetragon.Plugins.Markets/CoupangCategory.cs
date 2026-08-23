using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 쿠팡 카테고리 자동 결정 + 필수 항목 채우기.
///
/// 위탁판매는 상품 수천 개를 다루므로 카테고리를 사람이 일일이 고를 수 없다.
/// 쿠팡의 카테고리 추천 API로 상품명에서 카테고리를 예측하고,
/// 그 카테고리가 요구하는 필수 구매옵션과 고시정보를 메타 API로 조회해 채운다.
///
/// 실측으로 확인한 경로 (경로 접두사가 서로 다르다 — 문서와 다르게 쓰면 404):
///   추천: POST /v2/providers/openapi/apis/api/v1/categorization/predict
///   메타: GET  /v2/providers/seller_api/apis/api/v1/marketplace/meta/category-related-metas/display-category-codes/{code}
/// </summary>
public sealed partial class CoupangAdapter
{
    /// <summary>카테고리 메타는 자주 바뀌지 않으므로 프로세스 수명 동안 캐시한다.</summary>
    private static readonly ConcurrentDictionary<string, CoupangCategoryMeta> MetaCache = new();

    /// <summary>
    /// 등록에 쓸 카테고리를 정한다.
    /// 우선순위: 상품에 매핑된 코드 → 설정의 기본값 → 쿠팡 추천.
    /// </summary>
    private async Task<string?> ResolveCategoryAsync(
        ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        if (payload.MarketCategoryCodes.GetValueOrDefault("coupang") is { Length: > 0 } mapped)
            return mapped;
        if (cred.Get("default_category_code") is { Length: > 0 } configured)
            return configured;

        try
        {
            var body = new
            {
                productName = payload.Name,
                productDescription = payload.Description,
                brand = payload.Attributes.GetValueOrDefault("브랜드"),
            };
            var response = await SendAsync(HttpMethod.Post,
                "/v2/providers/openapi/apis/api/v1/categorization/predict", "", body, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var resultType = data.TryGetProperty("autoCategorizationPredictionResultType", out var t)
                ? t.GetString() : null;
            var categoryId = data.TryGetProperty("predictedCategoryId", out var c) ? c.GetString() : null;
            var categoryName = data.TryGetProperty("predictedCategoryName", out var n) ? n.GetString() : null;

            if (resultType != "SUCCESS" || string.IsNullOrWhiteSpace(categoryId))
            {
                logger.LogWarning("쿠팡 카테고리 추천 실패({Type}) — 상품: {Name}",
                    resultType, Truncate(payload.Name, 40));
                return null;
            }

            logger.LogInformation("쿠팡 카테고리 자동 결정: {Name} → {Id} ({CategoryName})",
                Truncate(payload.Name, 30), categoryId, categoryName);
            return categoryId;
        }
        catch (Exception ex)
        {
            logger.LogWarning("쿠팡 카테고리 추천 오류: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>카테고리가 요구하는 필수 구매옵션·고시정보를 조회한다.</summary>
    private async Task<CoupangCategoryMeta> GetCategoryMetaAsync(
        string categoryCode, MarketCredential cred, CancellationToken ct)
    {
        if (MetaCache.TryGetValue(categoryCode, out var cached)) return cached;

        var meta = new CoupangCategoryMeta();
        try
        {
            var response = await SendAsync(HttpMethod.Get,
                $"/v2/providers/seller_api/apis/api/v1/marketplace/meta/category-related-metas/display-category-codes/{categoryCode}",
                "", null, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return meta;

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return meta;

            // 구매옵션 — MANDATORY + EXPOSED만 상품 등록 시 반드시 채워야 한다.
            // 타입과 단위까지 보관해야 올바른 값을 만들 수 있다
            // (숫자형에 "상세페이지 참조"를 넣으면 "유효하지 않은 구매 옵션 값" 오류가 난다).
            if (data.TryGetProperty("attributes", out var attributes)
                && attributes.ValueKind == JsonValueKind.Array)
            {
                foreach (var attribute in attributes.EnumerateArray())
                {
                    var name = Text(attribute, "attributeTypeName");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (Text(attribute, "required") != "MANDATORY") continue;

                    // exposed=EXPOSED는 구매옵션(고객이 고르는 것),
                    // exposed=NONE은 검색옵션(검색 필터로만 쓰이는 것)이다. 둘 다 필수면 채워야 한다.
                    var isSearchOption = Text(attribute, "exposed") == "NONE";

                    var allowed = new List<string>();
                    if (attribute.TryGetProperty("inputValues", out var values)
                        && values.ValueKind == JsonValueKind.Array)
                    {
                        allowed.AddRange(values.EnumerateArray()
                            .Select(v => v.GetString())
                            .Where(v => !string.IsNullOrWhiteSpace(v))!);
                    }

                    var units = new List<string>();
                    if (attribute.TryGetProperty("usableUnits", out var unitNode)
                        && unitNode.ValueKind == JsonValueKind.Array)
                    {
                        units.AddRange(unitNode.EnumerateArray()
                            .Select(u => u.GetString())
                            .Where(u => !string.IsNullOrWhiteSpace(u))!);
                    }

                    var spec = new CoupangAttributeSpec(
                        name,
                        Text(attribute, "dataType") ?? "STRING",
                        Text(attribute, "basicUnit"),
                        allowed,
                        units);
                    if (isSearchOption) meta.RequiredSearchOptions.Add(spec);
                    else meta.RequiredAttributes.Add(spec);
                }
            }

            // 고시정보 — 첫 번째 카테고리의 필수 항목을 쓴다
            if (data.TryGetProperty("noticeCategories", out var notices)
                && notices.ValueKind == JsonValueKind.Array)
            {
                var first = notices.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    meta.NoticeCategoryName = Text(first, "noticeCategoryName");
                    if (first.TryGetProperty("noticeCategoryDetailNames", out var details)
                        && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in details.EnumerateArray())
                        {
                            var name = Text(detail, "noticeCategoryDetailName");
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            if (Text(detail, "required") != "MANDATORY") continue;
                            meta.RequiredNoticeDetails.Add(name);
                        }
                    }
                }
            }

            logger.LogInformation(
                "쿠팡 카테고리 {Code} 메타: 구매옵션 {Attrs}종, 검색옵션 {Search}종, 고시정보 {Notice}({Details}종)",
                categoryCode, meta.RequiredAttributes.Count, meta.RequiredSearchOptions.Count,
                meta.NoticeCategoryName, meta.RequiredNoticeDetails.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning("쿠팡 카테고리 메타 조회 실패 ({Code}): {Error}", categoryCode, ex.Message);
        }

        MetaCache[categoryCode] = meta;
        return meta;
    }

    /// <summary>
    /// 카테고리 필수 구매옵션을 채운다.
    /// 우리 옵션(색상=블랙 등)을 이름이 비슷한 필수 항목에 매칭하고,
    /// 남는 필수 항목은 상세페이지 참조로 채운다 (비우면 쿠팡이 등록을 거부한다).
    /// </summary>
    private static object[] BuildItemAttributes(
        CoupangCategoryMeta meta, IReadOnlyDictionary<string, string> options)
    {
        var filled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedOptionValues = new HashSet<string>();

        // 구매옵션과 검색옵션 모두 필수면 채워야 한다 — 하나라도 빠지면 등록이 거부된다
        var required = meta.RequiredAttributes.Concat(meta.RequiredSearchOptions).ToList();

        // 1단계: 이름이 겹치는 우리 옵션을 붙인다 (색상 ↔ 색상, 사이즈 ↔ 사이즈)
        var matched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in required)
        {
            var match = options.FirstOrDefault(o =>
                spec.Name.Contains(o.Key, StringComparison.OrdinalIgnoreCase)
                || o.Key.Contains(spec.Name, StringComparison.OrdinalIgnoreCase));
            if (match.Value is { Length: > 0 })
            {
                matched[spec.Name] = match.Value;
                usedOptionValues.Add(match.Value);
            }
        }

        // 2단계: 남은 옵션값을 아직 못 채운 문자열 속성에 순서대로 배정한다.
        //
        // 공급처가 옵션 그룹을 "옵션" 같은 뭉뚱그린 이름으로 주는 경우가 많다.
        // 그러면 이름 매칭이 안 돼 "상세페이지 참조"로 채워지는데,
        // 실제 값("블랙")이 있는데도 그러면 검색·필터에서 걸리지 않는다.
        var leftovers = options.Values
            .Where(v => !usedOptionValues.Contains(v))
            .ToList();
        var leftoverIndex = 0;
        foreach (var spec in required)
        {
            if (matched.ContainsKey(spec.Name)) continue;
            // 숫자형·선택형은 아무 값이나 넣으면 거부되므로 문자열 자유입력에만 배정한다
            if (spec.AllowedValues.Count > 0) continue;
            if (spec.DataType.Equals("NUMBER", StringComparison.OrdinalIgnoreCase)) continue;
            if (leftoverIndex >= leftovers.Count) break;

            var value = leftovers[leftoverIndex++];
            matched[spec.Name] = value;
            usedOptionValues.Add(value);
        }

        foreach (var spec in required)
            filled[spec.Name] = CoerceAttributeValue(spec, matched.GetValueOrDefault(spec.Name));

        // 필수가 아닌 우리 옵션도 함께 보낸다 (구매 옵션으로 노출)
        foreach (var (key, value) in options)
        {
            if (usedOptionValues.Contains(value)) continue;
            filled.TryAdd(key, Truncate(value, 50));
        }

        return filled.Select(kv => new
        {
            attributeTypeName = kv.Key,
            attributeValueName = kv.Value,
        }).Cast<object>().ToArray();
    }

    /// <summary>
    /// 필수 옵션 값을 쿠팡이 받아들이는 형태로 맞춘다.
    ///
    /// 숫자형에 문자열을 넣거나 SELECT형에 허용되지 않은 값을 넣으면
    /// "유효하지 않은 구매 옵션 값 혹은 단위가 존재합니다"로 거부된다.
    /// </summary>
    private static string CoerceAttributeValue(CoupangAttributeSpec spec, string? candidate)
    {
        // SELECT형 — 허용값 중에서만 고를 수 있다
        if (spec.AllowedValues.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var exact = spec.AllowedValues.FirstOrDefault(v =>
                    v.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;

                var partial = spec.AllowedValues.FirstOrDefault(v =>
                    v.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.Contains(v, StringComparison.OrdinalIgnoreCase));
                if (partial is not null) return partial;
            }
            return spec.AllowedValues[0];   // 고를 수 없으면 첫 허용값
        }

        // 숫자형 — 값 + 단위 (예: "1개"). 후보에서 숫자를 못 뽑으면 1로 둔다.
        if (spec.DataType.Equals("NUMBER", StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string((candidate ?? "").Where(char.IsDigit).ToArray());
            var number = digits.Length is > 0 and <= 6 ? digits.TrimStart('0') : "";
            if (string.IsNullOrEmpty(number)) number = "1";
            return $"{number}{spec.EffectiveUnit}";
        }

        // 자유 입력 문자열
        return string.IsNullOrWhiteSpace(candidate) ? "상세페이지 참조" : Truncate(candidate, 50);
    }

    /// <summary>
    /// 상품고시정보를 채운다. 필수 항목을 하나라도 빠뜨리면 등록이 거부된다.
    /// 실제 값을 아는 항목(제조자·원산지 등)은 채우고 나머지는 상세페이지 참조로 둔다.
    /// </summary>
    private static object[] BuildNotices(CoupangCategoryMeta meta, ListingPayload payload)
    {
        if (string.IsNullOrWhiteSpace(meta.NoticeCategoryName) || meta.RequiredNoticeDetails.Count == 0)
        {
            // 메타를 못 가져왔을 때의 안전한 기본값
            return
            [
                new
                {
                    noticeCategoryName = "기타 재화",
                    noticeCategoryDetailName = "품명 및 모델명",
                    content = Truncate(payload.Name, 100),
                },
            ];
        }

        var supplier = payload.Logistics.SupplierName;
        var origin = payload.Attributes.GetValueOrDefault("원산지");
        var brand = payload.Attributes.GetValueOrDefault("브랜드");

        return meta.RequiredNoticeDetails.Select(detail => new
        {
            noticeCategoryName = meta.NoticeCategoryName!,
            noticeCategoryDetailName = detail,
            content = Truncate(ResolveNoticeContent(detail, payload, supplier, origin, brand), 100),
        }).Cast<object>().ToArray();
    }

    private static string ResolveNoticeContent(
        string detail, ListingPayload payload, string? supplier, string? origin, string? brand) => detail switch
        {
            _ when detail.Contains("품명") || detail.Contains("모델") => payload.Name,
            _ when detail.Contains("제조자") || detail.Contains("수입자") => supplier ?? "상세페이지 참조",
            _ when detail.Contains("원산지") || detail.Contains("제조국") => origin ?? "상세페이지 참조",
            _ when detail.Contains("브랜드") => brand ?? "상세페이지 참조",
            _ when detail.Contains("A/S") || detail.Contains("소비자상담") =>
                payload.Logistics.SupplierPhone ?? "판매자에게 문의",
            _ => "상세페이지 참조",
        };

    /// <summary>
    /// 쿠팡 상세페이지 콘텐츠.
    ///
    /// 공급처가 상세 이미지 재사용을 허용한 경우에만 그 HTML을 그대로 쓴다.
    /// 허용하지 않으면 대표 이미지와 상품명만으로 최소한의 상세페이지를 만든다
    /// (남의 이미지를 마켓에 올리면 저작권 문제가 된다).
    /// </summary>
    private static object[] BuildContents(ListingPayload payload)
    {
        var logistics = payload.Logistics;

        var html = logistics is { DetailImagesAllowed: true, DetailHtml: { Length: > 0 } supplierHtml }
            ? supplierHtml
            : BuildFallbackHtml(payload);

        return
        [
            new
            {
                contentsType = "HTML",
                contentDetails = new[]
                {
                    new { content = html, detailType = "TEXT" },
                },
            },
        ];
    }

    /// <summary>상세 이미지를 쓸 수 없을 때의 상세페이지 — 대표 이미지만 사용한다.</summary>
    private static string BuildFallbackHtml(ListingPayload payload)
    {
        var html = new System.Text.StringBuilder();
        html.Append("<div style=\"text-align:center\">");
        html.Append($"<h2>{System.Net.WebUtility.HtmlEncode(payload.Name)}</h2>");
        foreach (var url in payload.ImageUrls.Take(10))
            html.Append($"<img src=\"{url}\" style=\"max-width:100%\" alt=\"\">");
        html.Append("</div>");
        return html.ToString();
    }

    /// <summary>
    /// 쿠팡 검색어(searchTags).
    ///
    /// 공급처가 정리해 둔 키워드를 우선 쓴다 — 상품명에서 추측하는 것보다 정확하다
    /// (도매꾹은 basis.keywords.kw로 10개 안팎을 준다).
    /// 부족하면 상품명에서 의미 있는 낱말을 뽑아 채운다.
    /// </summary>
    private static string[] BuildSearchTags(ListingPayload payload)
    {
        var tags = new List<string>();

        // 1. 공급처가 준 키워드
        if (payload.Attributes.GetValueOrDefault(LogisticsKeys.SearchKeywords) is { Length: > 0 } supplied)
            tags.AddRange(supplied.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // 2. 브랜드
        if (payload.Attributes.GetValueOrDefault("브랜드") is { Length: > 1 } brand
            && brand != "브랜드 없음" && brand != "상세정보참조")
            tags.Add(brand);

        // 3. 상품명에서 낱말 보충 (숫자·기호만 남는 토큰과 너무 짧은 것은 뺀다)
        if (tags.Count < MinSearchTags)
        {
            var words = payload.Name
                .Split([' ', '/', '[', ']', '(', ')', ',', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim())
                .Where(w => w.Length >= 2 && w.Any(char.IsLetter));
            tags.AddRange(words);
        }

        return tags
            .Select(t => t.Length > 20 ? t[..20] : t)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSearchTags)
            .ToArray();
    }

    /// <summary>쿠팡 검색어 상한 (초과분은 무시된다).</summary>
    private const int MaxSearchTags = 20;
    /// <summary>이보다 적으면 상품명에서 낱말을 보충한다.</summary>
    private const int MinSearchTags = 5;

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null,
            }
            : null;
}

/// <summary>카테고리가 요구하는 필수 항목.</summary>
public sealed class CoupangCategoryMeta
{
    /// <summary>필수 구매옵션 — 고객이 상품 페이지에서 고르는 값 (색상·사이즈 등).</summary>
    public List<CoupangAttributeSpec> RequiredAttributes { get; } = [];
    /// <summary>필수 검색옵션 — 노출되지 않고 검색 필터로만 쓰이는 값.</summary>
    public List<CoupangAttributeSpec> RequiredSearchOptions { get; } = [];
    public string? NoticeCategoryName { get; set; }
    public List<string> RequiredNoticeDetails { get; } = [];
}

/// <summary>
/// 필수 구매옵션 정의.
/// </summary>
/// <param name="DataType">STRING / NUMBER / DATE</param>
/// <param name="BasicUnit">숫자형의 기본 단위 (예: "개")</param>
/// <param name="AllowedValues">SELECT형이면 허용값 목록. 비어 있으면 자유 입력.</param>
public sealed record CoupangAttributeSpec(
    string Name,
    string DataType,
    string? BasicUnit,
    List<string> AllowedValues,
    List<string> UsableUnits)
{
    /// <summary>
    /// 실제로 보낼 단위.
    /// basicUnit이 usableUnits에 없는 경우가 있어(예: '개당 수량'은 basicUnit "개"인데
    /// 허용 단위는 개입/롤/매/매입/세트) 반드시 허용 목록 안에서 골라야 한다.
    /// </summary>
    public string? EffectiveUnit =>
        UsableUnits.Count == 0
            ? BasicUnit
            : UsableUnits.FirstOrDefault(u => u == BasicUnit) ?? UsableUnits[0];
}

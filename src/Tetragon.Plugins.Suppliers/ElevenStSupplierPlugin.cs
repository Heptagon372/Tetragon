using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 11번가 공급처 플러그인 (국내 오픈마켓에서 소싱).
///
/// 상세 페이지는 schema.org JSON-LD를 서버사이드로 내려준다 — 실측 확인:
/// name / image / brand / productID / category(전체 경로) / offers.price / availability
/// 자격증명 없이 동작한다.
///
/// 다만 검색·카테고리 목록 페이지는 클라이언트 렌더링(2.4KB 셸)이라
/// ICategoryCrawler는 구현하지 않는다. 카테고리 단위 수집이 필요하면
/// 11번가 오픈API(제휴) 키가 필요하다.
/// </summary>
public sealed partial class ElevenStSupplierPlugin(
    IHttpClientFactory httpClientFactory,
    ILogger<ElevenStSupplierPlugin> logger) : ISupplierPlugin
{
    public string Code => "11st";
    public string DisplayName => "11번가";
    public string Version => "1.0.0";
    public bool IsLive => true;
    public bool IsAvailable => true;   // 자격증명 불필요

    public bool CanHandle(Uri url) =>
        url.Host.EndsWith("11st.co.kr", StringComparison.OrdinalIgnoreCase);

    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var productId = ExtractProductId(productUrl)
            ?? throw new PermanentScrapeException($"11번가 상품번호를 URL에서 찾을 수 없습니다: {productUrl}");

        // 모바일 URL도 PC URL로 정규화 (JSON-LD는 PC 페이지에 있다)
        var pcUrl = new Uri($"https://www.11st.co.kr/products/{productId}");

        var client = httpClientFactory.CreateClient("scraper");
        using var request = ScraperHttp.BuildRequest(pcUrl, ctx.CookieHeader, "https://www.11st.co.kr/");
        using var response = await client.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new TransientScrapeException($"11번가 응답 오류: HTTP {(int)response.StatusCode}");

        // 판매중지/잘못된 상품번호는 alert 스크립트만 담긴 짧은 페이지로 온다
        if (html.Contains("판매가 중지된 상품", StringComparison.Ordinal)
            || html.Contains("잘못된 상품번호", StringComparison.Ordinal))
            throw new PermanentScrapeException("11번가에서 판매가 중지되었거나 존재하지 않는 상품번호입니다.");

        var product = ExtractJsonLdProduct(html)
            ?? throw new TransientScrapeException(
                "11번가 상품 정보(JSON-LD)를 찾을 수 없습니다 — 페이지 구조 변경 가능성 (파서 v1.0.0)");

        var title = GetString(product, "name")
            ?? throw new TransientScrapeException("11번가 상품명을 찾을 수 없습니다.");

        var offers = product.TryGetProperty("offers", out var offersNode) ? offersNode : default;
        var price = offers.ValueKind == JsonValueKind.Object
            ? ParseDecimal(GetString(offers, "price"))
            : null;
        if (price is null or 0)
            throw new TransientScrapeException("11번가 가격 정보를 찾을 수 없습니다.");

        var availability = offers.ValueKind == JsonValueKind.Object ? GetString(offers, "availability") : null;
        var soldOut = availability?.Contains("OutOfStock", StringComparison.OrdinalIgnoreCase) == true;

        // category는 "컴퓨터 주변기기>마우스>무선/블루투스" 형태의 전체 경로
        var categoryPath = (GetString(product, "category") ?? "")
            .Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var images = new List<string>();
        if (GetString(product, "image") is { } image) images.Add(image);
        images.AddRange(ExtractDetailImages(html));

        var attributes = new Dictionary<string, string>();
        if (product.TryGetProperty("brand", out var brand) && GetString(brand, "name") is { } brandName
            && brandName != "상세정보참조")
            attributes["브랜드"] = brandName;

        logger.LogInformation("11번가 수집 성공: {Id} ({Title})", productId, Truncate(title, 40));

        return new RawProduct
        {
            SupplierCode = Code,
            SourceProductId = productId,
            Url = pcUrl.ToString(),
            Title = title,
            TitleLocale = "ko-KR",    // 국내 마켓 — 번역 불필요
            Description = MetaContent(html, "og:description") ?? "",
            ImageUrls = images.Distinct().Take(10).ToList(),
            CategoryPath = categoryPath,
            // 11번가 상세 페이지의 옵션은 별도 XHR로 로드되므로 단일 SKU로 취급한다
            OptionGroups = [],
            Variants =
            [
                new RawVariant
                {
                    SourceSkuId = productId,
                    Price = price.Value,
                    Stock = soldOut ? 0 : 999,
                },
            ],
            Currency = "KRW",
            BasePrice = price.Value,
            Attributes = attributes,
            RawJson = product.GetRawText(),
        };
    }

    public async Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct)
    {
        try
        {
            var raw = await CollectAsync(new Uri(source.Url), new ScrapeContext(), ct);
            return new RawInventory
            {
                SourceProductId = source.SourceProductId,
                IsAvailable = raw.Variants.Any(v => v.Stock > 0),
                Variants = raw.Variants,
            };
        }
        catch (PermanentScrapeException)
        {
            return new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = false };
        }
    }

    // ── 파싱 ─────────────────────────────────────────────────────────────

    /// <summary>@type이 Product인 JSON-LD 블록을 찾는다 (페이지에 여러 개 있을 수 있다).</summary>
    private static JsonElement? ExtractJsonLdProduct(string html)
    {
        foreach (Match match in JsonLdRegex().Matches(html))
        {
            var content = match.Groups[1].Value.Trim();
            if (content.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(content); }
            catch (JsonException) { continue; }

            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                    if (GetString(element, "@type") == "Product")
                        return element.Clone();
            }
            else if (GetString(root, "@type") == "Product")
            {
                return root.Clone();
            }
        }
        return null;
    }

    /// <summary>상세 이미지 (cdn.011st.com 호스팅 이미지만 취한다).</summary>
    private static IEnumerable<string> ExtractDetailImages(string html) =>
        DetailImageRegex().Matches(html)
            .Select(m => m.Groups[1].Value)
            .Where(u => u.Contains("11st", StringComparison.OrdinalIgnoreCase))
            .Select(u => u.StartsWith("//") ? "https:" + u : u)
            .Distinct()
            .Take(9);

    private static string? ExtractProductId(Uri url)
    {
        var match = ProductIdRegex().Match(url.AbsolutePath);
        if (match.Success) return match.Groups[1].Value;
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["prdNo"] ?? query["prdNm"];
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value?.Replace(",", ""), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? MetaContent(string html, string property)
    {
        var match = Regex.Match(html,
            $"""<meta[^>]+property=["']{Regex.Escape(property)}["'][^>]+content=["']([^"']*)["']""",
            RegexOptions.IgnoreCase);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"/products/(?:m/)?(\d{6,})")]
    private static partial Regex ProductIdRegex();
    [GeneratedRegex("""<script[^>]*type=["']application/ld\+json["'][^>]*>(.*?)</script>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdRegex();
    [GeneratedRegex("""<img[^>]+src=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex DetailImageRegex();
}

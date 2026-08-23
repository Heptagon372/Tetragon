using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 아마존 공급처 플러그인.
///
/// 아마존 상세 페이지 자체는 서버사이드 렌더링이라 HTML만 받으면 파싱은 가능하다.
/// 문제는 받는 단계다 — 실측 결과 아마존은 TLS/HTTP 지문으로 클라이언트를 식별해
/// 같은 헤더를 보내도 curl/브라우저는 정상 페이지(265KB)를, .NET HttpClient는
/// 캡차 페이지(3.8KB)를 받는다. 재시도나 헤더 조정으로는 넘을 수 없다.
///
/// 따라서 실사용 경로는 공식 Product Advertising API(PA-API 5.0)다.
/// (Amazon Associates 승인 + 판매 실적 필요)
/// HTML 파싱 경로는 차단되지 않는 환경(프록시·헤드리스 브라우저 경유)을 위해 유지한다.
///
/// 주의: 접속 IP의 지역에 따라 통화가 달라진다 (한국 IP → KRW).
/// 응답에서 통화를 직접 읽고, 못 읽으면 USD로 가정하지 않고 실패시킨다.
/// 또한 봇 요청에는 바이박스 가격이 내려오지 않는 경우가 많아
/// 마켓플레이스 최저 오퍼가를 쓰게 되며, 그 경우 '가격출처' 속성에 표시한다.
/// </summary>
public sealed partial class AmazonSupplierPlugin(
    IHttpClientFactory httpClientFactory,
    IBrowserFetcher browserFetcher,
    ILogger<AmazonSupplierPlugin> logger) : ISupplierPlugin
{
    public string Code => "amazon";
    public string DisplayName => "Amazon";
    public string Version => "1.0.0";
    public bool IsLive => true;
    public bool IsAvailable => true;   // HTML 경로는 자격증명 불필요

    public bool CanHandle(Uri url) =>
        url.Host.Contains("amazon.", StringComparison.OrdinalIgnoreCase);

    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var asin = ExtractAsin(productUrl)
            ?? throw new PermanentScrapeException(
                $"아마존 ASIN을 URL에서 찾을 수 없습니다: {productUrl} (예: /dp/B09B8V1LZ3)");

        var host = productUrl.Host;
        var canonical = new Uri($"https://{host}/dp/{asin}");

        var client = httpClientFactory.CreateClient("scraper");
        using var request = ScraperHttp.BuildRequest(canonical, ctx.CookieHeader, $"https://{host}/");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ko;q=0.8");
        using var response = await client.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        if ((int)response.StatusCode == 404)
            throw new PermanentScrapeException($"아마존에 존재하지 않는 상품입니다 (ASIN {asin}).");
        if ((int)response.StatusCode == 503 || IsCaptchaPage(html))
        {
            logger.LogWarning(
                "아마존 차단 감지 — HTTP {Status}, 본문 {Length}바이트, productTitle={HasTitle}",
                (int)response.StatusCode, html.Length,
                html.Contains("productTitle", StringComparison.Ordinal));

            // 아마존은 TLS/HTTP 지문으로 클라이언트를 식별한다. 같은 헤더라도
            // 브라우저·curl은 통과하고 .NET HttpClient는 캡차를 받는 경우가 많아,
            // 재시도나 헤더 조정으로는 해결되지 않는다.
            //
            // fetch 사이드카(실제 브라우저)가 구성돼 있으면 그 경로로 폴백한다
            // (docs/ANTIBOT-FETCH-DESIGN.md §2.3). 사이드카가 차단 안 된 HTML을 주면
            // 아래 파싱 로직을 그대로 태운다 — 파싱은 손대지 않는다.
            if (browserFetcher.IsAvailable)
            {
                logger.LogInformation("아마존 직접 경로 차단 — 브라우저 fetch로 폴백합니다 (ASIN {Asin}).", asin);
                var r = await browserFetcher.FetchAsync(new BrowserFetchRequest
                {
                    Url = canonical,
                    WaitForSelector = "#productTitle",
                    ProxyPolicy = "residential-us", // 아마존은 접속 지역에 따라 통화가 달라진다
                    Behavior = BehaviorProfile.Human,
                    CookieHeader = ctx.CookieHeader,
                    SessionKey = $"amazon:{host}",
                }, ct);

                if (r.Blocked || IsCaptchaPage(r.Html))
                    throw new TransientScrapeException(
                        "아마존이 브라우저 경로도 차단했습니다. 프록시 소진 또는 센서 미통과일 수 있습니다 " +
                        "— 다른 주거용 프록시로 재시도되거나, 안정성이 필요하면 PA-API를 권장합니다.");

                html = r.Html; // 이후 파싱은 정상 경로와 동일
            }
            else
            {
                throw new PermanentScrapeException(
                    "아마존이 이 요청을 봇으로 판단해 캡차를 반환했습니다. " +
                    "아마존은 헤더가 아니라 TLS 지문으로 클라이언트를 식별하므로 재시도해도 같은 결과입니다. " +
                    "브라우저 fetch 사이드카(설정 → 시스템 → fetch)를 구성하거나, " +
                    "아마존 상품 수집에는 Product Advertising API(PA-API 5.0) 자격증명을 등록하세요 " +
                    "— Amazon Associates 승인 후 설정 → 공급처 → amazon.");
            }
        }
        if (!response.IsSuccessStatusCode)
            throw new TransientScrapeException($"아마존 응답 오류: HTTP {(int)response.StatusCode}");

        var title = ExtractTitle(html)
            ?? throw new TransientScrapeException(
                "아마존 상품명 파싱 실패 — 페이지 구조 변경 가능성 (파서 v1.0.0)");

        var (price, currency, priceSource) = ExtractPrice(html);
        if (price is null)
            throw new TransientScrapeException(
                $"아마존 상품 '{Truncate(title, 30)}'의 가격을 파싱하지 못했습니다. " +
                "아마존은 가격 마크업을 자주 바꾸고 지역별로 다르게 내려줍니다. " +
                "안정적인 수집이 필요하면 Product Advertising API(PA-API) 사용을 권장합니다.");

        var images = ExtractImages(html);
        var soldOut = html.Contains("Currently unavailable", StringComparison.OrdinalIgnoreCase);

        logger.LogInformation("아마존 수집 성공: {Asin} ({Title}) {Price} {Currency} [{Source}]",
            asin, Truncate(title, 40), price, currency, priceSource);

        return new RawProduct
        {
            SupplierCode = Code,
            SourceProductId = asin,
            Url = canonical.ToString(),
            Title = title,
            TitleLocale = "en",
            Description = ExtractFeatureBullets(html),
            ImageUrls = images,
            CategoryPath = ExtractBreadcrumb(html),
            OptionGroups = [],
            Variants =
            [
                new RawVariant
                {
                    SourceSkuId = asin,
                    Price = price.Value,
                    Stock = soldOut ? 0 : 999,
                },
            ],
            Currency = currency,
            BasePrice = price.Value,
            Attributes = new Dictionary<string, string>
            {
                ["ASIN"] = asin,
                // 바이박스가 아니라 마켓플레이스 최저 오퍼가일 수 있으므로 출처를 남긴다
                ["가격출처"] = priceSource == "buybox" ? "아마존 판매가" : "마켓플레이스 최저가",
            },
            RawJson = $$"""{"asin":"{{asin}}","title":{{System.Text.Json.JsonSerializer.Serialize(title)}},"price":{{price}},"currency":"{{currency}}"}""",
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

    /// <summary>
    /// 실제 캡차 페이지인지 판정한다.
    /// 정상 상품 페이지(250KB+)에도 스크립트 번들 등에 'captcha' 문자열이 들어 있을 수 있어
    /// 단순 포함 검사는 오탐을 낸다. 캡차 페이지 고유의 폼 액션과 안내 문구로 판정한다.
    /// </summary>
    private static bool IsCaptchaPage(string html) =>
        html.Contains("/errors/validateCaptcha", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Enter the characters you see below", StringComparison.OrdinalIgnoreCase)
        || html.Contains("Type the characters you see in this image", StringComparison.OrdinalIgnoreCase)
        // 캡차 페이지는 상품 페이지와 달리 매우 작고 productTitle이 없다
        || (html.Length < 60_000
            && !html.Contains("productTitle", StringComparison.Ordinal)
            && html.Contains("Sorry, we just need to make sure you're not a robot", StringComparison.OrdinalIgnoreCase));

    private static string? ExtractTitle(string html)
    {
        var match = ProductTitleRegex().Match(html);
        // (아래 폴백은 아마존이 productTitle 대신 <title>만 주는 변형 페이지 대응)
        if (match.Success)
        {
            var title = System.Net.WebUtility.HtmlDecode(
                Regex.Replace(match.Groups[1].Value, @"<[^>]+>", "")).Trim();
            if (title.Length > 0) return CollapseWhitespace(title);
        }
        // 폴백: <title> 태그 (아마존은 " : Amazon.com" 같은 접미사를 붙인다)
        var titleTag = Regex.Match(html, "<title>([^<]+)</title>", RegexOptions.IgnoreCase);
        if (!titleTag.Success) return null;
        var text = System.Net.WebUtility.HtmlDecode(titleTag.Groups[1].Value).Trim();
        text = Regex.Replace(text, @"\s*[:|]\s*Amazon\.[a-z.]+.*$", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^Amazon\.com\s*[:|]\s*", "", RegexOptions.IgnoreCase);
        return text.Length > 3 ? CollapseWhitespace(text) : null;
    }

    /// <summary>
    /// 가격 추출 — 마크업이 자주 바뀌므로 여러 전략을 순서대로 시도한다.
    /// 통화는 반드시 페이지에서 읽는다 (접속 지역에 따라 KRW/USD 등으로 달라진다).
    /// PriceSource는 그 가격이 바이박스인지 마켓플레이스 최저 오퍼가인지 알려준다.
    /// </summary>
    private static (decimal? Price, string Currency, string Source) ExtractPrice(string html)
    {
        // 전략 1: 통화기호 + 금액이 함께 있는 a-offscreen (바이박스, 가장 신뢰도 높음)
        foreach (Match match in OffscreenPriceRegex().Matches(html))
        {
            var text = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            var parsed = ParseMoneyText(text);
            if (parsed.Price is > 0) return (parsed.Price, parsed.Currency, "buybox");
        }

        // 전략 2: JSON의 priceAmount/displayPrice 계열 (바이박스)
        var jsonMatch = JsonPriceRegex().Match(html);
        if (jsonMatch.Success)
        {
            var parsed = ParseMoneyText(System.Net.WebUtility.HtmlDecode(jsonMatch.Groups[1].Value));
            if (parsed.Price is > 0) return (parsed.Price, parsed.Currency, "buybox");
        }

        // 전략 3: "2 options from KRW 49,768" 형태의 마켓플레이스 최저 오퍼가.
        // 아마존이 봇 요청에는 바이박스를 렌더링하지 않는 경우가 많아 실제로 이 경로를 자주 탄다.
        var olpMatch = OlpMessageRegex().Match(html);
        if (olpMatch.Success)
        {
            var parsed = ParseMoneyText(System.Net.WebUtility.HtmlDecode(olpMatch.Groups[1].Value));
            if (parsed.Price is > 0) return (parsed.Price, parsed.Currency, "offer");
        }

        // 전략 4: priceWithoutCurrencySymbol + 페이지에서 찾은 통화
        var bare = BarePriceRegex().Match(html);
        if (bare.Success && decimal.TryParse(bare.Groups[1].Value, NumberStyles.Any,
                CultureInfo.InvariantCulture, out var bareValue) && bareValue > 0)
        {
            var currency = CurrencyCodeRegex().Match(html) is { Success: true } c
                ? c.Groups[1].Value.ToUpperInvariant()
                : IsoCurrencyRegex().Match(html) is { Success: true } iso
                    ? iso.Groups[1].Value.ToUpperInvariant()
                    : null;
            if (currency is not null)
                return (decimal.Round(bareValue, 2), currency, "offer");
        }

        return (null, "USD", "none");
    }

    /// <summary>
    /// "$24.99" / "₩49,767" / "KRW 49,768" / "24,99 €" 같은 표기에서 금액과 통화를 함께 읽는다.
    /// 통화기호뿐 아니라 ISO 코드(KRW/USD/…) 표기도 처리한다.
    /// </summary>
    private static (decimal? Price, string Currency) ParseMoneyText(string text)
    {
        var currency = text switch
        {
            _ when text.Contains('₩') || text.Contains("KRW", StringComparison.OrdinalIgnoreCase) => "KRW",
            _ when text.Contains("USD", StringComparison.OrdinalIgnoreCase) || text.Contains('$') => "USD",
            _ when text.Contains("GBP", StringComparison.OrdinalIgnoreCase) || text.Contains('£') => "GBP",
            _ when text.Contains("EUR", StringComparison.OrdinalIgnoreCase) || text.Contains('€') => "EUR",
            _ when text.Contains("JPY", StringComparison.OrdinalIgnoreCase) || text.Contains('¥') => "JPY",
            _ => null,
        };
        if (currency is null) return (null, "USD");

        var digits = Regex.Match(text, @"[\d][\d,.\s]*");
        if (!digits.Success) return (null, currency);

        var normalized = digits.Value.Replace(" ", "").Trim();
        // 유로식 표기(1.234,56) 처리
        if (currency == "EUR" && normalized.Contains(',') && normalized.LastIndexOf(',') > normalized.LastIndexOf('.'))
            normalized = normalized.Replace(".", "").Replace(',', '.');
        else
            normalized = normalized.Replace(",", "");

        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0
            ? (value, currency)
            : (null, currency);
    }

    private static List<string> ExtractImages(string html)
    {
        var images = new List<string>();

        // hiRes/large URL이 담긴 이미지 갤러리 JSON
        foreach (Match match in GalleryImageRegex().Matches(html))
        {
            var url = match.Groups[1].Value.Replace("\\/", "/");
            if (url.Contains("images-amazon", StringComparison.OrdinalIgnoreCase)
                || url.Contains("ssl-images", StringComparison.OrdinalIgnoreCase)
                || url.Contains("media-amazon", StringComparison.OrdinalIgnoreCase))
                images.Add(url);
        }

        if (images.Count == 0)
        {
            var landing = Regex.Match(html, """id="landingImage"[^>]+src="([^"]+)""");
            if (landing.Success) images.Add(landing.Groups[1].Value);
        }

        return images.Distinct().Take(10).ToList();
    }

    private static string ExtractFeatureBullets(string html)
    {
        var section = Regex.Match(html,
            """id="feature-bullets".*?</div>""", RegexOptions.Singleline);
        if (!section.Success) return "";
        var bullets = Regex.Matches(section.Value, @"<span[^>]*class=""a-list-item""[^>]*>(.*?)</span>",
                RegexOptions.Singleline)
            .Select(m => CollapseWhitespace(System.Net.WebUtility.HtmlDecode(
                Regex.Replace(m.Groups[1].Value, @"<[^>]+>", ""))))
            .Where(s => s.Length > 0)
            .Take(8);
        return string.Join("\n", bullets);
    }

    private static List<string> ExtractBreadcrumb(string html)
    {
        var section = Regex.Match(html,
            """id="wayfinding-breadcrumbs_feature_div".*?</ul>""", RegexOptions.Singleline);
        if (!section.Success) return [];
        return Regex.Matches(section.Value, @"<a[^>]*>(.*?)</a>", RegexOptions.Singleline)
            .Select(m => CollapseWhitespace(System.Net.WebUtility.HtmlDecode(
                Regex.Replace(m.Groups[1].Value, @"<[^>]+>", ""))))
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string? ExtractAsin(Uri url)
    {
        var match = AsinRegex().Match(url.AbsolutePath);
        if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["asin"]?.ToUpperInvariant();
    }

    private static string CollapseWhitespace(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"/(?:dp|gp/product|gp/aw/d)/([A-Z0-9]{10})", RegexOptions.IgnoreCase)]
    private static partial Regex AsinRegex();
    [GeneratedRegex("""id="productTitle"[^>]*>(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex ProductTitleRegex();
    [GeneratedRegex("class=\"a-offscreen\">([^<]{1,30})<")]
    private static partial Regex OffscreenPriceRegex();
    [GeneratedRegex("\"(?:displayPrice|priceAmount|formattedPrice)\"\\s*:\\s*\"([^\"]{1,30})\"")]
    private static partial Regex JsonPriceRegex();
    [GeneratedRegex("\"priceWithoutCurrencySymbol\"\\s*:\\s*\"?([0-9.]+)")]
    private static partial Regex BarePriceRegex();
    [GeneratedRegex("\"(?:currencyCode|currency)\"\\s*:\\s*\"([A-Z]{3})\"")]
    private static partial Regex CurrencyCodeRegex();
    /// <summary>"2 options from KRW 49,768" — 봇에게 바이박스가 안 내려올 때의 유일한 가격.</summary>
    [GeneratedRegex("\"olpMessage\"\\s*:\\s*\"([^\"]{1,80})\"")]
    private static partial Regex OlpMessageRegex();
    /// <summary>본문에 노출된 ISO 통화 코드 (KRW 49,768 형태).</summary>
    [GeneratedRegex("\\b(KRW|USD|EUR|GBP|JPY)\\s*[\\d,]")]
    private static partial Regex IsoCurrencyRegex();
    [GeneratedRegex("(?:\"hiRes\"|\"large\"|\"mainUrl\")\\s*:\\s*\"(https?:[^\"]+\\.jpg)")]
    private static partial Regex GalleryImageRegex();
}

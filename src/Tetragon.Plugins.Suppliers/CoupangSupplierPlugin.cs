using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 쿠팡 공급처(스크래퍼) 플러그인.
///
/// 쿠팡은 Akamai Bot Manager로 보호되어 <c>HttpClient</c> 직접 요청은 엣지에서 즉시 차단된다
/// (검색/카테고리/상품 페이지 = 403 Access Denied). 실측 결과 통과하는 유일한 경로는
/// 실제 브라우저(headed) + 홈 예열 → 목표 이동이며, 이는 <see cref="IBrowserFetcher"/>
/// (tetragon-fetch 사이드카)가 담당한다. 자세한 내용: docs/ANTIBOT-FETCH-DESIGN.md.
///
/// 따라서 이 플러그인은 사이드카가 구성돼야만 동작한다(<see cref="IsAvailable"/>).
/// 미구성이면 라우터가 후보에서 제외한다 — 직접 요청으로는 어차피 차단되기 때문이다.
///
/// 상품 상세는 og:title / og:image / salePrice(JSON) 등을 파싱한다(실측 검증됨).
/// 검색 목록(ICategoryCrawler)은 상품 링크(/vp/products/{id})만 뽑고, 상세는 CollectAsync가 채운다.
/// </summary>
public sealed class CoupangSupplierPlugin(
    IBrowserFetcher browserFetcher,
    ILogger<CoupangSupplierPlugin> logger) : ISupplierPlugin, ICategoryCrawler
{
    public string Code => "coupang";
    public string DisplayName => "쿠팡";
    public string Version => "1.0.0";
    public bool IsLive => true;

    /// <summary>쿠팡은 브라우저 fetch 사이드카 없이는 수집 불가 — 사이드카 구성 여부에 종속.</summary>
    public bool IsAvailable => browserFetcher.IsAvailable;

    public string SupplierCode => Code;

    public bool CanHandle(Uri url) =>
        url.Host.EndsWith("coupang.com", StringComparison.OrdinalIgnoreCase);

    public string? TryGetSourceProductId(Uri productUrl) => ExtractProductId(productUrl);

    // ── 상품 상세 수집 ─────────────────────────────────────────────
    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var productId = ExtractProductId(productUrl)
            ?? throw new PermanentScrapeException(
                $"쿠팡 상품번호를 URL에서 찾을 수 없습니다: {productUrl} (예: /vp/products/9179029564)");

        var canonical = new Uri($"https://www.coupang.com/vp/products/{productId}");

        if (!browserFetcher.IsAvailable)
            throw new PermanentScrapeException(
                "쿠팡 수집에는 브라우저 fetch 사이드카가 필요합니다 (Akamai 차단). " +
                "설정 → 시스템 → fetch에 사이드카(tetragon-fetch)를 등록하세요.");

        var result = await browserFetcher.FetchAsync(new BrowserFetchRequest
        {
            Url = canonical,
            Behavior = BehaviorProfile.Human,     // 홈 예열 → 상품 이동 (사이드카가 처리)
            ProxyPolicy = "residential-kr",
            CookieHeader = ctx.CookieHeader,
            SessionKey = $"coupang:{ctx.TenantId}",
        }, ct);

        if (result.Blocked)
            throw new TransientScrapeException(
                $"쿠팡이 상품 {productId} 요청을 차단했습니다 (Akamai). " +
                "사이드카가 headed/real_chrome로 동작하는지, 프록시/IP 상태를 확인하세요.");

        var html = result.Html;

        if (html.Contains("존재하지 않는 상품", StringComparison.Ordinal)
            || html.Contains("페이지를 찾을 수 없습니다", StringComparison.Ordinal))
            throw new PermanentScrapeException($"쿠팡에 존재하지 않는 상품입니다 (ID {productId}).");

        var title = CleanTitle(Meta(html, "og:title"))
            ?? throw new TransientScrapeException(
                "쿠팡 상품명(og:title)을 찾을 수 없습니다 — 차단 또는 페이지 구조 변경 (파서 v1.0.0)");

        var (price, priceSource) = ExtractPrice(html);
        if (price is null)
            throw new TransientScrapeException(
                $"쿠팡 상품 '{Truncate(title, 30)}'의 가격을 파싱하지 못했습니다 (파서 v1.0.0).");

        var images = ExtractImages(html);
        var soldOut = html.Contains("일시품절", StringComparison.Ordinal)
            || html.Contains("품절된 상품", StringComparison.Ordinal);

        // 옵션/변형 파싱 (여러 전략). 못 찾으면 단일 변형으로 폴백 — 옵션 없는 상품이 다수라 정상.
        var (optionGroups, variants) = ExtractOptionsAndVariants(html, productId, price.Value, soldOut,
            images.Count > 0 ? images[0] : null);

        logger.LogInformation(
            "쿠팡 수집 성공: {Id} ({Title}) {Price}원 이미지 {ImgCount}장 옵션 {OptCount}그룹/{VarCount}변형 [{Source}]",
            productId, Truncate(title, 40), price, images.Count, optionGroups.Count, variants.Count, priceSource);

        return new RawProduct
        {
            SupplierCode = Code,
            SourceProductId = productId,
            Url = canonical.ToString(),
            Title = title,
            TitleLocale = "ko-KR",
            ImageUrls = images,
            OptionGroups = optionGroups,
            Variants = variants,
            Currency = "KRW",
            BasePrice = price.Value,
            Attributes = new Dictionary<string, string>
            {
                ["상품번호"] = productId,
                ["가격출처"] = priceSource,
            },
            RawJson = $$"""{"productId":"{{productId}}","title":{{System.Text.Json.JsonSerializer.Serialize(title)}},"price":{{price}},"currency":"KRW"}""",
        };
    }

    public async Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct)
    {
        try
        {
            var raw = await CollectAsync(new Uri(source.Url), new ScrapeContext(), ct);
            var v = raw.Variants.Count > 0 ? raw.Variants[0] : null;
            return new RawInventory
            {
                SourceProductId = raw.SourceProductId,
                IsAvailable = v is not null && v.Stock > 0,
                Variants = raw.Variants,
            };
        }
        catch (PermanentScrapeException)
        {
            return new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = false };
        }
    }

    // ── 카테고리/검색 크롤링 (ICategoryCrawler) ───────────────────────
    /// <summary>
    /// 쿠팡 카테고리 트리는 스크래핑으로 안정 열거가 어렵다(대량 CSR + 잦은 변경).
    /// 실사용은 키워드 검색(CrawlAsync의 Keyword)이며, 카테고리 코드는 사용자가
    /// 쿠팡 URL의 categories/{id}를 직접 넣는 경우만 지원한다. 트리 열거는 빈 목록.
    /// </summary>
    public Task<IReadOnlyList<SupplierCategory>> GetCategoriesAsync(string? parentCode, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SupplierCategory>>([]);

    public async Task<CategoryCrawlPage> CrawlAsync(CategoryCrawlRequest request, CancellationToken ct)
    {
        if (!browserFetcher.IsAvailable)
            throw new PermanentScrapeException(
                "쿠팡 검색에는 브라우저 fetch 사이드카가 필요합니다 (Akamai 차단).");

        var page = Math.Max(1, request.Page);
        Uri searchUrl;
        if (!string.IsNullOrWhiteSpace(request.Keyword))
            searchUrl = new Uri($"https://www.coupang.com/np/search?q={Uri.EscapeDataString(request.Keyword)}&page={page}");
        else if (!string.IsNullOrWhiteSpace(request.CategoryCode))
            searchUrl = new Uri($"https://www.coupang.com/np/categories/{request.CategoryCode}?page={page}");
        else
            throw new PermanentScrapeException("쿠팡 크롤링에는 검색어(Keyword) 또는 카테고리 코드가 필요합니다.");

        var result = await browserFetcher.FetchAsync(new BrowserFetchRequest
        {
            Url = searchUrl,
            Behavior = BehaviorProfile.Human,
            ProxyPolicy = "residential-kr",
            SessionKey = $"coupang:{request.TenantId}",
        }, ct);

        if (result.Blocked)
            throw new TransientScrapeException("쿠팡 검색이 차단되었습니다 (Akamai).");

        // 검색 결과에서 상품 링크(/vp/products/{id})만 추출한다.
        // 상세(제목/가격/이미지)는 이후 CollectAsync가 채운다 (책임 분리).
        var ids = ProductIdRegex.Matches(result.Html)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var items = ids.Select(id => new CrawledProductRef
        {
            SourceProductId = id,
            Url = $"https://www.coupang.com/vp/products/{id}",
        }).ToList();

        logger.LogInformation("쿠팡 검색 '{Q}' p{Page}: 상품 {Count}건",
            request.Keyword ?? request.CategoryCode, page, items.Count);

        return new CategoryCrawlPage
        {
            Items = items,
            Page = page,
            TotalCount = items.Count,
            HasMore = items.Count > 0,   // 쿠팡은 총 개수를 안정적으로 안 주므로 결과 유무로 판단
        };
    }

    // ── 파싱 헬퍼 ─────────────────────────────────────────────────
    private static readonly Regex ProductIdRegex =
        new(@"/vp/products/(\d+)", RegexOptions.Compiled);

    private static string? ExtractProductId(Uri url)
    {
        var m = ProductIdRegex.Match(url.AbsoluteUri);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly Regex OgMetaRegex =
        new(@"<meta[^>]+property=[""']og:(?<k>[\w:]+)[""'][^>]+content=[""'](?<v>[^""']*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex OgMetaRevRegex =
        new(@"<meta[^>]+content=[""'](?<v>[^""']*)[""'][^>]+property=[""']og:(?<k>[\w:]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? Meta(string html, string ogProp)
    {
        var key = ogProp.StartsWith("og:", StringComparison.Ordinal) ? ogProp[3..] : ogProp;
        foreach (Match m in OgMetaRegex.Matches(html))
            if (string.Equals(m.Groups["k"].Value, key, StringComparison.OrdinalIgnoreCase))
                return System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value);
        foreach (Match m in OgMetaRevRegex.Matches(html))
            if (string.Equals(m.Groups["k"].Value, key, StringComparison.OrdinalIgnoreCase))
                return System.Net.WebUtility.HtmlDecode(m.Groups["v"].Value);
        return null;
    }

    /// <summary>og:title은 "상품명 - 카테고리 | 쿠팡" 형태 — 꼬리표를 떼어낸다.</summary>
    private static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Trim();
        var bar = t.LastIndexOf(" | 쿠팡", StringComparison.Ordinal);
        if (bar > 0) t = t[..bar];
        // "… - 무선마우스" 처럼 마지막 " - 카테고리" 꼬리표가 붙는 경우가 있어 떼어낸다.
        var dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 20) t = t[..dash];   // 너무 앞이면 상품명 일부일 수 있어 보존
        return t.Trim();
    }

    private static readonly Regex SalePriceJsonRegex =
        new(@"""(?:salePrice|couponPrice|finalPrice)""\s*:\s*(\d{3,})", RegexOptions.Compiled);
    private static readonly Regex WonRegex =
        new(@"([0-9]{1,3}(?:,[0-9]{3})+)\s*원", RegexOptions.Compiled);

    private static (decimal? Price, string Source) ExtractPrice(string html)
    {
        var j = SalePriceJsonRegex.Match(html);
        if (j.Success && decimal.TryParse(j.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jp))
            return (jp, "판매가(JSON)");

        var w = WonRegex.Match(html);
        if (w.Success && decimal.TryParse(w.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wp))
            return (wp, "표시가격");

        return (null, "");
    }

    private static readonly Regex CdnImageRegex =
        new(@"(?:https?:)?//[^""'\s]*coupangcdn\.com/[^""'\s]*vendor_inventory[^""'\s]*\.(?:jpg|jpeg|png|webp)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<string> ExtractImages(string html)
    {
        var set = new List<string>();
        var seen = new HashSet<string>();

        var og = Meta(html, "og:image");
        if (!string.IsNullOrWhiteSpace(og)) { var n = Normalize(og); if (seen.Add(n)) set.Add(n); }

        foreach (Match m in CdnImageRegex.Matches(html))
        {
            var n = Normalize(m.Value);
            if (seen.Add(n)) set.Add(n);
            if (set.Count >= 20) break;   // 상세설명 이미지가 수백 장일 수 있어 대표 이미지 위주로 캡
        }
        return set;

        static string Normalize(string u) => u.StartsWith("//", StringComparison.Ordinal) ? "https:" + u : u;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ── 옵션/변형 파싱 ────────────────────────────────────────────
    // 쿠팡 상세는 옵션을 여러 형태로 임베드한다. 전략을 순서대로 시도하고,
    // 무엇도 못 찾으면 단일 변형으로 폴백한다(옵션 없는 상품이 다수).
    // ⚠️ 정확한 키/셀렉터는 라이브 검증(inspect_options.py)으로 튜닝 대상 — 파서 v1.0.0.

    // 전략 A: 임베드 JSON의 속성 쌍 "attributeTypeName":"색상","attributeValueName":"블랙"
    private static readonly Regex AttrPairRegex = new(
        @"""attributeTypeName""\s*:\s*""(?<t>[^""]+)""\s*,\s*""attributeValueName""\s*:\s*""(?<v>[^""]+)""",
        RegexOptions.Compiled);

    // 전략 A': vendorItem 배열 — itemName + (선택) 가격/재고
    private static readonly Regex VendorItemRegex = new(
        @"""vendorItemId""\s*:\s*""?(?<sku>\d+)""?[^}]*?""itemName""\s*:\s*""(?<name>[^""]+)""(?:[^}]*?""(?:salePrice|discountedPrice|price)""\s*:\s*(?<price>\d+))?",
        RegexOptions.Compiled);

    // 전략 B: DOM <select> 옵션 — <option value="...">라벨</option>
    private static readonly Regex SelectRegex = new(
        @"<select[^>]*>(?<body>.*?)</select>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex OptionTagRegex = new(
        @"<option[^>]*value=""(?<val>[^""]+)""[^>]*>(?<label>[^<]+)</option>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static (List<RawOptionGroup> Groups, List<RawVariant> Variants) ExtractOptionsAndVariants(
        string html, string productId, decimal basePrice, bool soldOut, string? mainImage)
    {
        try
        {
            // 전략 A: 속성 타입→값 그룹핑
            var byType = new Dictionary<string, List<string>>();
            foreach (Match m in AttrPairRegex.Matches(html))
            {
                var t = m.Groups["t"].Value.Trim();
                var v = m.Groups["v"].Value.Trim();
                if (t.Length == 0 || v.Length == 0) continue;
                if (!byType.TryGetValue(t, out var list)) byType[t] = list = [];
                if (!list.Contains(v)) list.Add(v);
            }

            // 전략 A': vendorItem → 변형
            var variants = new List<RawVariant>();
            foreach (Match m in VendorItemRegex.Matches(html))
            {
                var sku = m.Groups["sku"].Value;
                if (variants.Exists(x => x.SourceSkuId == sku)) continue;
                decimal vp = basePrice;
                if (m.Groups["price"].Success
                    && decimal.TryParse(m.Groups["price"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                    vp = p;
                variants.Add(new RawVariant
                {
                    SourceSkuId = sku,
                    Price = vp,
                    Stock = soldOut ? 0 : 999,
                    ImageUrl = mainImage,
                });
                if (variants.Count >= 100) break;   // 방어적 상한
            }

            // 전략 B: <select> 옵션 (A에서 아무것도 못 얻었을 때만)
            if (byType.Count == 0)
            {
                foreach (Match sel in SelectRegex.Matches(html))
                {
                    var values = new List<string>();
                    foreach (Match opt in OptionTagRegex.Matches(sel.Groups["body"].Value))
                    {
                        var label = System.Net.WebUtility.HtmlDecode(opt.Groups["label"].Value).Trim();
                        // 플레이스홀더("옵션 선택" 등)·빈 값 제외
                        if (label.Length == 0 || label.Contains("선택", StringComparison.Ordinal)) continue;
                        if (!values.Contains(label)) values.Add(label);
                    }
                    if (values.Count >= 2)   // 실제 옵션으로 볼 수 있는 최소 개수
                    {
                        byType[$"옵션{byType.Count + 1}"] = values;
                        if (byType.Count >= 3) break;
                    }
                }
            }

            var groups = byType
                .Select(kv => new RawOptionGroup(
                    kv.Key,
                    kv.Value.Select((v, i) => new RawOptionValue($"{kv.Key}:{i}", v, null)).ToList()))
                .ToList();

            // 변형을 못 찾았으면 단일 기본 변형 (기존 동작 보존)
            if (variants.Count == 0)
                variants.Add(new RawVariant
                {
                    SourceSkuId = productId,
                    Price = basePrice,
                    Stock = soldOut ? 0 : 999,
                    ImageUrl = mainImage,
                });

            return (groups, variants);
        }
        catch
        {
            // 파싱 실패는 치명적이지 않다 — 단일 변형으로 폴백.
            return ([], [new RawVariant
            {
                SourceSkuId = productId, Price = basePrice, Stock = soldOut ? 0 : 999, ImageUrl = mainImage,
            }]);
        }
    }
}

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
    ILogger<CoupangSupplierPlugin> logger) : ISupplierPlugin, ICategoryCrawler, IStockScanner
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

    // ── 품절·저재고 스캔 (IStockScanner) ──────────────────────────
    // 목록 페이지 하나(1 fetch)에서 상품별 재고 상태를 읽는다. 상세를 상품마다 열지 않아
    // 요청 수가 페이지 수만큼으로 줄어 IP 차단 위험이 크게 낮아진다.
    // 파싱 규약은 실제 목록 HTML로 검증됨(파서 v1.0.0):
    //   · 카드 앵커  : <a href="/vp/products/{id}"> (DOM). 다음 앵커 전까지가 한 카드.
    //   · 상품명     : 카드 안 <img alt="...">
    //   · 판매가     : PriceArea_priceArea__ 안 첫 <span>{n}원</span> (없으면 {n,nnn}원 폴백)
    //   · 저재고     : "단 N개 남음" (딜 카운트다운 "N일 남음"과 구분 — 개/일)
    //   · 품절       : 임베드 JSON의 soldoutArea":{..."soldout":true} → 앞쪽 가장 가까운 상품에 매핑
    // ⚠️ custom-oos 클래스는 재고가 아니라 가격/할인 스타일이라 무시한다.
    // (IStockScanner.SupplierCode는 위의 SupplierCode => Code 로 이미 충족)
    public async Task<StockScanPage> ScanStockAsync(StockScanRequest request, CancellationToken ct)
    {
        if (!browserFetcher.IsAvailable)
            throw new PermanentScrapeException(
                "쿠팡 재고 스캔에는 브라우저 fetch 사이드카가 필요합니다 (Akamai 차단).");

        var page = Math.Max(1, request.Page);
        Uri listUrl;
        if (!string.IsNullOrWhiteSpace(request.Keyword))
            listUrl = new Uri($"https://www.coupang.com/np/search?q={Uri.EscapeDataString(request.Keyword)}&page={page}");
        else if (!string.IsNullOrWhiteSpace(request.CategoryCode))
            listUrl = new Uri($"https://www.coupang.com/np/categories/{request.CategoryCode}?page={page}");
        else
            throw new PermanentScrapeException("쿠팡 재고 스캔에는 검색어(Keyword) 또는 카테고리 코드가 필요합니다.");

        var result = await browserFetcher.FetchAsync(new BrowserFetchRequest
        {
            Url = listUrl,
            Behavior = BehaviorProfile.Human,
            ProxyPolicy = "residential-kr",
            SessionKey = $"coupang:{request.TenantId}",
        }, ct);

        if (result.Blocked)
            throw new TransientScrapeException("쿠팡 목록이 차단되었습니다 (Akamai).");

        var items = ParseListingStock(result.Html);
        logger.LogInformation("쿠팡 재고 스캔 '{Q}' p{Page}: {Total}개 중 품절 {Sold}·저재고 {Low}",
            request.Keyword ?? request.CategoryCode, page, items.Count,
            items.Count(i => i.Status == StockStatus.SoldOut),
            items.Count(i => i.Status == StockStatus.LowStock));

        return new StockScanPage { Items = items, Page = page, HasMore = items.Count > 0 };
    }

    // 목록 HTML → 상품별 재고. static이라 실제 저장된 HTML로 단위 테스트 가능(요청 0).
    public static IReadOnlyList<StockScanItem> ParseListingStock(string html)
    {
        if (string.IsNullOrEmpty(html)) return [];

        // 1) 임베드 JSON에서 id → 품절 여부 맵 (soldoutArea 앞쪽 가장 가까운 상품 링크에 귀속)
        var soldOut = new HashSet<string>();
        foreach (Match m in SoldoutAreaRegex.Matches(html))
        {
            if (m.Groups["flag"].Value != "true") continue;
            var backStart = Math.Max(0, m.Index - 4000);
            var back = html.Substring(backStart, m.Index - backStart);
            var link = LastProductLink(back);
            if (link is not null) soldOut.Add(link);
        }

        // 2) DOM 카드 단위(<a href="/vp/products/{id}"> ~ 다음 앵커)로 파싱
        var cards = DomCardAnchorRegex.Matches(html);
        var items = new List<StockScanItem>();
        var seen = new HashSet<string>();
        for (int i = 0; i < cards.Count; i++)
        {
            var id = cards[i].Groups[1].Value;
            if (!seen.Add(id)) continue;   // 같은 상품 재등장(광고/중복) 무시
            var start = cards[i].Index;
            var end = i + 1 < cards.Count ? cards[i + 1].Index : Math.Min(html.Length, start + 6000);
            var seg = html.Substring(start, end - start);

            var nameM = ImgAltRegex.Match(seg);
            var name = nameM.Success ? System.Net.WebUtility.HtmlDecode(nameM.Groups[1].Value).Trim() : null;

            decimal? price = null;
            var priceM = PriceSpanRegex.Match(seg);
            if (!priceM.Success) priceM = PriceWonRegex.Match(seg);
            if (priceM.Success && decimal.TryParse(priceM.Groups[1].Value.Replace(",", ""),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                price = p;

            int? remaining = null;
            var lowM = LowStockRegex.Match(seg);
            if (lowM.Success && int.TryParse(lowM.Groups[1].Value, out var n)) remaining = n;

            var status = soldOut.Contains(id) ? StockStatus.SoldOut
                : remaining is not null ? StockStatus.LowStock
                : StockStatus.InStock;

            items.Add(new StockScanItem
            {
                SourceProductId = id,
                Url = $"https://www.coupang.com/vp/products/{id}",
                Name = name,
                Price = price,
                Currency = "KRW",
                Status = status,
                Remaining = remaining,
            });
        }
        return items;
    }

    private static string? LastProductLink(string s)
    {
        string? last = null;
        foreach (Match m in ProductIdRegex.Matches(s)) last = m.Groups[1].Value;
        return last;
    }

    // 스캔 파서 정규식 (실측 검증)
    private static readonly Regex DomCardAnchorRegex =
        new(@"href=""/vp/products/(\d+)", RegexOptions.Compiled);
    private static readonly Regex ImgAltRegex =
        new(@"<img\s+alt=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex PriceSpanRegex =
        new(@"PriceArea_priceArea__\w+"".*?<span>([\d,]+)\s*원", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PriceWonRegex =
        new(@">(\d{1,3}(?:,\d{3})+)\s*원", RegexOptions.Compiled);
    private static readonly Regex LowStockRegex =
        new(@"단\s*(\d+)개\s*남음", RegexOptions.Compiled);
    private static readonly Regex SoldoutAreaRegex =
        new(@"soldoutArea\\"":\{\\""soldOutText\\"":\\""[^\\]*\\"",\\""soldout\\"":(?<flag>true|false)\}",
            RegexOptions.Compiled);

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
    // 쿠팡 상세의 옵션은 fashion-option DOM으로 렌더된다 (실측 검증, product.html 기준):
    //   · 그룹명   : <div class="…twc-font-bold twc-mb-[4px]…">사이즈</div> (색상은 <span>색상</span>)
    //   · 드롭다운형(사이즈): fashion-option-select__content 의 <li> 텍스트 = L,M,S,XL,…
    //   · 스와치형(색상)   : fashion-option__button-list 의 이미지 <li> (텍스트 없음) → 개수+선택라벨
    // 조합별 가격/재고는 정적 HTML에 없고(선택 시 API 로드) → 변형은 단일 기본만 만든다.
    // 옵션이 없거나 파싱 실패면 단일 변형으로 안전 폴백.

    private static readonly Regex OptionSectionRegex = new(
        @"<section class=""twc-my-\[16px\]", RegexOptions.Compiled);
    private static readonly Regex OptionNameRegex = new(
        @"twc-font-bold twc-mb-\[4px\][^>]*>(?:<span>)?([^<:]{1,20})", RegexOptions.Compiled);
    private static readonly Regex DropdownContentRegex = new(
        @"fashion-option-select__content.*?</ul>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LiTextRegex = new(
        @"<li[^>]*>([^<]{1,40})</li>", RegexOptions.Compiled);
    private static readonly Regex SwatchListRegex = new(
        @"fashion-option__button-list.*?</ul>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex SwatchLiRegex = new(@"<li>", RegexOptions.Compiled);
    private static readonly Regex SelectedLabelRegex = new(
        @"fashion-option__label-item-text[^>]*>([^<]+)", RegexOptions.Compiled);

    private static (List<RawOptionGroup> Groups, List<RawVariant> Variants) ExtractOptionsAndVariants(
        string html, string productId, decimal basePrice, bool soldOut, string? mainImage)
    {
        var groups = new List<RawOptionGroup>();
        try
        {
            // fashion-option 영역의 각 그룹은 <section class="twc-my-[16px] …"> 로 시작한다.
            var parts = OptionSectionRegex.Split(html);
            foreach (var sec in parts)
            {
                if (!sec.Contains("fashion-option-select", StringComparison.Ordinal)
                    && !sec.Contains("fashion-option__button-list", StringComparison.Ordinal))
                    continue;

                var nm = OptionNameRegex.Match(sec);
                if (!nm.Success) continue;
                var name = System.Net.WebUtility.HtmlDecode(nm.Groups[1].Value).Trim();
                if (name.Length == 0) continue;

                var values = new List<string>();

                // 드롭다운형: __content 의 <li> 텍스트
                var content = DropdownContentRegex.Match(sec);
                if (content.Success)
                {
                    foreach (Match li in LiTextRegex.Matches(content.Value))
                    {
                        var v = System.Net.WebUtility.HtmlDecode(li.Groups[1].Value).Trim();
                        if (v.Length > 0 && !values.Contains(v)) values.Add(v);
                    }
                }

                // 스와치형: __button-list 의 이미지 <li> 개수 + 선택 라벨
                if (values.Count == 0)
                {
                    var blist = SwatchListRegex.Match(sec);
                    if (blist.Success)
                    {
                        var cnt = SwatchLiRegex.Matches(blist.Value).Count;
                        if (cnt > 0)
                        {
                            var sel = SelectedLabelRegex.Match(sec);
                            values.Add(sel.Success
                                ? $"{cnt}종 (선택:{System.Net.WebUtility.HtmlDecode(sel.Groups[1].Value).Trim()})"
                                : $"{cnt}종");
                        }
                    }
                }

                if (values.Count > 0)
                    groups.Add(new RawOptionGroup(
                        name, values.Select((v, i) => new RawOptionValue($"{name}:{i}", v, null)).ToList()));

                if (groups.Count >= 5) break;   // 방어적 상한
            }
        }
        catch
        {
            groups = [];   // 파싱 실패는 치명적이지 않다 — 옵션 없이 진행
        }

        // 변형: 조합별 가격이 정적 HTML에 없으므로 단일 기본 변형만.
        var variants = new List<RawVariant>
        {
            new()
            {
                SourceSkuId = productId,
                Price = basePrice,
                Stock = soldOut ? 0 : 999,
                ImageUrl = mainImage,
            },
        };
        return (groups, variants);
    }
}

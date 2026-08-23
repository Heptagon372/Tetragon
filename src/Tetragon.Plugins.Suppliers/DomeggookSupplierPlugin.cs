using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 도매꾹 / 도매매 공급처 플러그인 (국내 B2B 도매).
///
/// 공식 Open API(openapi.domeggook.com) 사용 — 스크래핑이 아니라 정식 경로다.
///  - mode=getItemView : 상품 상세
///  - mode=getItemList : 카테고리/키워드 목록 (ICategoryCrawler 구현)
///  - mode=getCategoryList : 카테고리 트리
///
/// market 파라미터로 도매꾹(dome)과 도매매(supply)를 구분한다.
/// 국내 상품이므로 통화는 KRW이고 번역이 필요 없다.
/// </summary>
public sealed partial class DomeggookSupplierPlugin(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<DomeggookSupplierPlugin> logger) : ISupplierPlugin, ICategoryCrawler
{
    public string Code => "domeggook";
    public string SupplierCode => Code;
    public string DisplayName => "도매꾹/도매매";
    public string Version => "1.0.0";
    public bool IsLive => true;
    public bool IsAvailable => credentials.HasKey(CredentialScope, "api_key");

    private const string CredentialScope = "supplier:domeggook";
    private const string ApiBase = "https://domeggook.com/ssl/api/";

    public bool CanHandle(Uri url) =>
        url.Host.Contains("domeggook.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.Contains("domemedb.domeggook.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.Contains("domeme.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 도매꾹 상품번호는 URL에 그대로 들어 있다 — 수집(API 호출) 없이 중복 검사를 할 수 있다.
    /// domeggook.com/64858155, .../item.php?no=64858155 둘 다 같은 상품이다.
    /// </summary>
    public string? TryGetSourceProductId(Uri productUrl) => ExtractItemNo(productUrl);

    // ── 상품 상세 수집 ───────────────────────────────────────────────────

    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var itemNo = ExtractItemNo(productUrl)
            ?? throw new PermanentScrapeException($"도매꾹 상품번호(no=)를 URL에서 찾을 수 없습니다: {productUrl}");

        var apiKey = RequireApiKey();
        var json = await CallApiAsync(new Dictionary<string, string>
        {
            ["ver"] = "4.4",
            ["mode"] = "getItemView",
            ["aid"] = apiKey,
            ["no"] = itemNo,
            ["om"] = "json",
        }, ct);

        using var doc = JsonDocument.Parse(json);
        EnsureNoError(doc.RootElement, itemNo);

        var root = doc.RootElement.TryGetProperty("domeggook", out var wrapper) ? wrapper : doc.RootElement;

        var basis = root.TryGetProperty("basis", out var b) ? b : root;
        var title = GetString(basis, "title")
            ?? throw new TransientScrapeException("도매꾹 응답에서 상품명을 찾을 수 없습니다.");

        var (price, isDomeChannel) = ExtractPrice(root)
            ?? throw new TransientScrapeException("도매꾹 응답에서 가격을 찾을 수 없습니다.");

        var soldOut = GetString(basis, "status") is { } status && status != "판매중";
        var stock = int.TryParse(GetString(root.TryGetProperty("qty", out var q) ? q : default, "inventory"),
            out var inventory) ? inventory : 999;

        // 옵션별 추가금은 도매꾹(domPrice)과 도매매(supPrice)가 다르다.
        // 기준가를 어느 쪽에서 가져왔는지에 맞춰야 원가가 어긋나지 않는다.
        var (groups, variants) = ExtractOptions(root, price, soldOut ? 0 : stock, isDomeChannel);

        logger.LogInformation("도매꾹 수집 성공: {ItemNo} ({Title}) {Price}원", itemNo, Truncate(title, 40), price);

        return new RawProduct
        {
            SupplierCode = Code,
            SourceProductId = itemNo,
            Url = productUrl.ToString(),
            Title = title,
            TitleLocale = "ko-KR",   // 국내 도매 — 번역 불필요
            Description = ExtractDescription(root),
            ImageUrls = ExtractImages(root),
            CategoryPath = ExtractCategoryPath(root),
            OptionGroups = groups,
            Variants = variants.Count > 0 ? variants :
            [
                new RawVariant { SourceSkuId = itemNo, Price = price, Stock = soldOut ? 0 : stock },
            ],
            Currency = "KRW",
            BasePrice = price,
            Attributes = ExtractAttributes(root),
            RawJson = json,
        };
    }

    /// <summary>
    /// 도매꾹 가격 추출.
    /// price.dome은 두 형태로 온다 (실측):
    ///   - 단일가:   "1180"
    ///   - 수량별가: "1+4664|50+4650|100+4620"  ← 수량+단가를 |로 구분
    /// 수량별가는 최소 수량 구간의 단가(=정가)를 기준가로 쓴다.
    ///
    /// 어느 채널의 가격을 썼는지도 함께 돌려준다 — 옵션 추가금을 같은 채널에서 읽어야 하기 때문이다.
    /// </summary>
    private static (decimal Price, bool IsDomeChannel)? ExtractPrice(JsonElement root)
    {
        if (!root.TryGetProperty("price", out var price)) return null;

        var dome = GetString(price, "dome");
        if (!string.IsNullOrWhiteSpace(dome))
        {
            if (ParseDecimal(dome) is { } flat && flat > 0) return (flat, true);

            // "수량+단가|수량+단가" — 첫 구간(최소 수량)의 단가
            var firstTier = dome.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var unitPrice = firstTier?.Split('+', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (ParseDecimal(unitPrice) is { } tiered && tiered > 0) return (tiered, true);
        }

        // 도매매(supply) 가격 폴백
        return ParseDecimal(GetString(price, "supply")) is { } supply && supply > 0
            ? (supply, false)
            : null;
    }

    public async Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct)
    {
        try
        {
            var raw = await CollectAsync(new Uri(source.Url), new ScrapeContext(), ct);
            return new RawInventory
            {
                SourceProductId = source.SourceProductId,
                IsAvailable = raw.Variants.Count == 0 || raw.Variants.Any(v => v.Stock > 0),
                Variants = raw.Variants,
            };
        }
        catch (PermanentScrapeException)
        {
            return new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = false };
        }
    }

    // ── 카테고리 크롤링 (ICategoryCrawler) ───────────────────────────────

    public async Task<IReadOnlyList<SupplierCategory>> GetCategoriesAsync(string? parentCode, CancellationToken ct)
    {
        var apiKey = RequireApiKey();
        // 주의: 도매꾹은 모드마다 버전이 다르다. getCategoryList는 1.0이며
        // 다른 모드의 버전(4.1 등)을 보내면 "해당 오픈 API 서비스가 없습니다"가 온다.
        var parameters = new Dictionary<string, string>
        {
            ["ver"] = "1.0",
            ["mode"] = "getCategoryList",
            ["aid"] = apiKey,
            ["om"] = "json",
        };
        if (!string.IsNullOrWhiteSpace(parentCode)) parameters["ca"] = parentCode;

        var json = await CallApiAsync(parameters, ct);
        using var doc = JsonDocument.Parse(json);
        EnsureNoError(doc.RootElement, parentCode ?? "root");

        // 실제 응답 구조 (확인함):
        // { "domeggook": { "items": {
        //     "1": { "code":"01_00_00_00_00", "name":"패션잡화", "child": {
        //              "7": { "code":"01_07_00_00_00", "name":"남성가방", "child": {...} } } } } } }
        // 전체 트리가 한 번에 오므로, 요청한 부모의 자식 레벨만 잘라서 반환한다.
        var root = doc.RootElement.TryGetProperty("domeggook", out var wrapper) ? wrapper : doc.RootElement;
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            return [];

        var level = string.IsNullOrWhiteSpace(parentCode)
            ? items
            : FindChildren(items, parentCode) ?? default;

        if (level.ValueKind != JsonValueKind.Object) return [];

        var categories = new List<SupplierCategory>();
        foreach (var entry in level.EnumerateObject())
        {
            var node = entry.Value;
            if (node.ValueKind != JsonValueKind.Object) continue;
            var code = GetString(node, "code");
            var name = GetString(node, "name");
            if (code is null || name is null) continue;

            var hasChildren = node.TryGetProperty("child", out var child)
                              && child.ValueKind == JsonValueKind.Object
                              && child.EnumerateObject().Any();

            categories.Add(new SupplierCategory(
                code, name, parentCode, hasChildren, name,
                IsSelectable: !IsTopLevelCategory(code)));
        }
        return categories;
    }

    /// <summary>중첩 트리에서 해당 코드 노드를 찾아 그 child 딕셔너리를 반환한다.</summary>
    private static JsonElement? FindChildren(JsonElement level, string code)
    {
        foreach (var entry in level.EnumerateObject())
        {
            var node = entry.Value;
            if (node.ValueKind != JsonValueKind.Object) continue;

            if (GetString(node, "code") == code)
                return node.TryGetProperty("child", out var child)
                       && child.ValueKind == JsonValueKind.Object ? child : null;

            if (node.TryGetProperty("child", out var deeper) && deeper.ValueKind == JsonValueKind.Object
                && FindChildren(deeper, code) is { } found)
                return found;
        }
        return null;
    }

    public async Task<CategoryCrawlPage> CrawlAsync(CategoryCrawlRequest request, CancellationToken ct)
    {
        var apiKey = RequireApiKey();
        var market = credentials.Get(CredentialScope, "market") ?? "dome"; // dome=도매꾹, supply=도매매

        var parameters = new Dictionary<string, string>
        {
            ["ver"] = "4.1",
            ["mode"] = "getItemList",
            ["aid"] = apiKey,
            ["market"] = market,
            ["om"] = "json",
            ["sz"] = Math.Clamp(request.PageSize, 1, 100).ToString(),
            ["pg"] = Math.Max(request.Page, 1).ToString(),
            ["so"] = "rd", // 등록일 역순
        };
        // ca(카테고리)/kw(키워드) 중 최소 하나는 필수.
        // 단, 도매꾹은 대분류를 조건으로 인정하지 않으므로(실측 확인) 미리 막는다 —
        // 그대로 보내면 "최소 한 가지 이상의 검색 조건" 오류가 나서 원인을 알기 어렵다.
        if (!string.IsNullOrWhiteSpace(request.CategoryCode))
        {
            if (IsTopLevelCategory(request.CategoryCode) && string.IsNullOrWhiteSpace(request.Keyword))
                throw new PermanentScrapeException(
                    $"도매꾹은 대분류('{request.CategoryCode}')로는 상품을 조회할 수 없습니다. " +
                    "중분류 이하를 선택하거나(하위 → 버튼) 검색어를 함께 입력하세요.");
            parameters["ca"] = request.CategoryCode;
        }
        if (!string.IsNullOrWhiteSpace(request.Keyword)) parameters["kw"] = request.Keyword;
        if (request.MinPrice is { } min) parameters["mnp"] = ((int)min).ToString();
        if (request.MaxPrice is { } max) parameters["mxp"] = ((int)max).ToString();

        var json = await CallApiAsync(parameters, ct);
        using var doc = JsonDocument.Parse(json);
        EnsureNoError(doc.RootElement, request.CategoryCode ?? request.Keyword ?? "");

        var root = doc.RootElement.TryGetProperty("domeggook", out var wrapper) ? wrapper : doc.RootElement;
        var items = new List<CrawledProductRef>();

        if (root.TryGetProperty("list", out var list))
        {
            var entries = list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                : list.TryGetProperty("item", out var itemNode)
                    ? (itemNode.ValueKind == JsonValueKind.Array ? itemNode.EnumerateArray() : SingleItem(itemNode))
                    : SingleItem(list);

            foreach (var entry in entries)
            {
                var no = GetString(entry, "no") ?? GetString(entry, "itemNo");
                if (string.IsNullOrWhiteSpace(no)) continue;
                items.Add(new CrawledProductRef
                {
                    SourceProductId = no,
                    Url = $"https://domeggook.com/{no}",
                    Title = GetString(entry, "title"),
                    Price = ParseDecimal(GetString(entry, "price")),
                    Currency = "KRW",
                    ThumbnailUrl = GetString(entry, "thumb") ?? GetString(entry, "img"),
                });
            }
        }

        var total = 0;
        if (root.TryGetProperty("header", out var header))
            total = int.TryParse(GetString(header, "numberOfItems"), out var t) ? t : 0;

        logger.LogInformation("도매꾹 카테고리 조회: {Page}페이지 {Count}건 (전체 {Total})",
            request.Page, items.Count, total);

        return new CategoryCrawlPage
        {
            Items = items,
            Page = request.Page,
            TotalCount = total,
            HasMore = items.Count >= Math.Clamp(request.PageSize, 1, 100)
                      && (total == 0 || request.Page * request.PageSize < total),
        };
    }

    // ── API 호출 ─────────────────────────────────────────────────────────

    private string RequireApiKey() =>
        credentials.Get(CredentialScope, "api_key")
        ?? throw new PermanentScrapeException(
            "도매꾹 API 키가 없습니다. openapi.domeggook.com에서 발급 후 " +
            "설정 → 공급처 → domeggook에 api_key를 등록하세요.");

    private async Task<string> CallApiAsync(Dictionary<string, string> parameters, CancellationToken ct)
    {
        var query = string.Join("&", parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var client = httpClientFactory.CreateClient("scraper");
        using var response = await client.GetAsync($"{ApiBase}?{query}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new TransientScrapeException($"도매꾹 API 오류: HTTP {(int)response.StatusCode}");
        return body;
    }

    /// <summary>도매꾹 API는 오류도 HTTP 200으로 주고 errors 노드에 담는다.</summary>
    private static void EnsureNoError(JsonElement root, string context)
    {
        if (!root.TryGetProperty("errors", out var errors)) return;

        var code = GetString(errors, "code") ?? "";
        var message = GetString(errors, "dmessage") ?? GetString(errors, "message") ?? "알 수 없는 오류";

        // 401(인증)·403(권한)은 재시도해도 동일하다
        if (code is "401" or "403")
            throw new PermanentScrapeException($"도매꾹 API 인증 실패: {message} (설정에서 api_key를 확인하세요)");

        // 404는 "상품 없음"과 "모드/버전 조합 없음" 두 경우를 모두 쓴다 — 구분해서 안내한다
        if (code is "404")
            throw new PermanentScrapeException(
                message.Contains("서비스가 없", StringComparison.Ordinal)
                    ? $"도매꾹 API 호출 형식 오류 ({context}): {message} — mode/ver 조합을 확인하세요."
                    : $"도매꾹 상품을 찾을 수 없습니다 ({context}): {message}");

        throw new TransientScrapeException($"도매꾹 API 오류 [{code}]: {message}");
    }

    // ── 파싱 헬퍼 ────────────────────────────────────────────────────────

    /// <summary>
    /// 대표 이미지(thumb) + 상세설명 HTML 안의 이미지.
    ///
    /// 상세 이미지는 공급처가 재사용을 허용한 경우에만 가져온다.
    /// 도매꾹은 desc.license.usable로 허용 여부를 명시하는데,
    /// 허용하지 않은 이미지를 마켓에 올리면 저작권 문제가 된다.
    /// </summary>
    private static List<string> ExtractImages(JsonElement root)
    {
        var images = new List<string>();
        if (root.TryGetProperty("thumb", out var thumb) && thumb.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "original", "large", "small" })
                if (GetString(thumb, key) is { Length: > 0 } url) images.Add(url);
        }

        if (IsDetailImageAllowed(root) && ExtractDetailHtml(root) is { Length: > 0 } descHtml)
        {
            images.AddRange(DescImageRegex().Matches(descHtml)
                // HTML 속성값이므로 &amp; 등을 디코딩해야 URL이 살아 있다
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
                .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)));
        }

        return images.Distinct().Take(10).ToList();
    }

    /// <summary>desc.license.usable — 상세설명 이미지를 다른 곳에 써도 되는지.</summary>
    private static bool IsDetailImageAllowed(JsonElement root) =>
        root.TryGetProperty("desc", out var desc)
        && desc.TryGetProperty("license", out var license)
        && GetString(license, "usable") is "true" or "TRUE" or "Y";

    /// <summary>
    /// 상품상세 HTML 원본 ('상품상세 더보기'를 눌렀을 때 나오는 그 내용).
    ///
    /// 도매꾹은 상세 영역을 용도별로 나눠 준다:
    ///   desc.contents.item      ← 실제 상품 상세 (보통 길다란 이미지 1~n장)
    ///   desc.contents.deli      ← 배송 안내
    ///   desc.contents.event     ← 이벤트 배너
    ///   desc.contents.otherItem ← 다른 상품 홍보
    ///   desc.notice             ← 공급사 공지사항 (상품과 무관)
    ///
    /// 마켓에 올릴 것은 item뿐이다. notice나 otherItem을 올리면
    /// 남의 공지·타상품 광고가 우리 상세페이지에 실린다.
    /// </summary>
    private static string ExtractDetailHtml(JsonElement root)
    {
        if (!root.TryGetProperty("desc", out var desc)) return "";

        if (desc.TryGetProperty("contents", out var contents)
            && contents.ValueKind == JsonValueKind.Object
            && GetString(contents, "item") is { Length: > 0 } item)
            return item;

        // 구버전 응답 호환 — contents가 문자열로 오는 경우
        if (contents.ValueKind == JsonValueKind.String && contents.GetString() is { Length: > 0 } plain)
            return plain;

        return "";
    }

    /// <summary>
    /// 도매꾹 옵션 파싱.
    ///
    /// selectOpt는 JSON이 문자열로 한 번 더 감싸여 온다 (실측). 구조는 이렇다:
    /// <code>
    /// {
    ///   "type": "combination",
    ///   "set":  [ {"name":"color","opts":["카키블랙","레드"]},
    ///             {"name":"size", "opts":["6(230)","7(240)"]} ],
    ///   "data": { "00_01": {"name":"카키블랙/7(240)","qty":"135","domPrice":"2000",
    ///                       "dom":"1","sup":"1","hid":"0"} }
    /// }
    /// </code>
    ///
    /// <b>핵심은 <c>data</c>다.</b> 예전에는 <c>set</c>만 보고 옵션값 하나당 변형을 하나씩 만들었는데,
    /// 그러면 색상×사이즈 상품이 "카키블랙"과 "6(230)"이라는 별개 상품 두 개가 되어 버린다.
    /// 실제로 팔리는 단위는 조합("카키블랙/6(230)")이고, 그 목록이 <c>data</c>다.
    ///
    /// data 키는 <c>set</c> 배열의 인덱스다(정렬·필터가 적용된 <c>set</c>이지 <c>orgSet</c>이 아니다).
    /// 수집해 둔 3,753개 조합으로 인덱스→이름 복원을 전량 대조해 확인했다.
    ///
    /// data가 주는 것들 — 예전에는 전부 놓치고 있었다:
    ///   - <c>qty</c>      : 조합별 실제 재고 (전체 재고를 옵션 수로 나누던 추정값을 대체)
    ///   - <c>domPrice</c> : 조합별 추가금 (set의 추가금 배열과 값이 다른 경우가 있다 — data가 맞다)
    ///   - <c>hid</c>      : 공급처가 숨긴 옵션
    ///   - <c>dom</c>/<c>sup</c> : 채널별 판매 가능 여부
    /// </summary>
    private static (List<RawOptionGroup>, List<RawVariant>) ExtractOptions(
        JsonElement root, decimal basePrice, int totalStock, bool isDomeChannel)
    {
        if (!root.TryGetProperty("selectOpt", out var selectOpt)) return ([], []);

        // 문자열로 감싸인 JSON을 한 번 더 파싱한다
        JsonDocument? optDoc = null;
        try
        {
            optDoc = selectOpt.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(selectOpt.GetString() ?? "{}")
                : null;
        }
        catch (JsonException)
        {
            return ([], []);
        }

        using (optDoc)
        {
            var optRoot = optDoc?.RootElement ?? selectOpt;
            if (!optRoot.TryGetProperty("set", out var setsNode) || setsNode.ValueKind != JsonValueKind.Array)
                return ([], []);

            var sets = setsNode.EnumerateArray()
                .Select(s => (
                    Name: NormalizeGroupName(GetString(s, "name")),
                    Opts: s.TryGetProperty("opts", out var o) && o.ValueKind == JsonValueKind.Array
                        ? o.EnumerateArray().Select(v => v.GetString() ?? "").ToList()
                        : []))
                .ToList();
            if (sets.Count == 0) return ([], []);

            // 그룹명이 겹치면 한 변형 안에서 값이 서로 덮어써진다 (색상 두 개짜리 상품 등).
            // 축은 이름으로 구분되므로 여기서 유일하게 만든다.
            MakeGroupNamesUnique(sets);
            // 축 이름 중에 "옵션"이 이미 있으면 조합용 그룹은 다른 이름을 쓴다
            var flatGroupName = sets.Any(s => s.Name == FlatGroupName) ? "옵션 조합" : FlatGroupName;

            if (!optRoot.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return ([], []);

            var isCombination = GetString(optRoot, "type") is not "independent";
            var priceField = isDomeChannel ? "domPrice" : "supPrice";
            var sellableField = isDomeChannel ? "dom" : "sup";

            var variants = new List<RawVariant>();
            // 실제로 팔리는 조합이 쓰는 값만 옵션 목록에 남긴다.
            // 숨김 처리된 값이 마켓 옵션에 남아 있으면 주문은 들어오는데 발주가 안 된다.
            var usedValues = sets.Select(_ => new SortedSet<int>()).ToList();

            foreach (var entry in data.EnumerateObject())
            {
                var combo = entry.Value;
                if (GetString(combo, "hid") == "1") continue;                 // 공급처가 숨긴 옵션
                if (GetString(combo, sellableField) is { } flag && flag != "1") continue;  // 이 채널에서 안 팔림

                var indices = ParseComboKey(entry.Name);
                if (indices is null) continue;

                var displayName = GetString(combo, "name");
                var optionValues = new Dictionary<string, string>();

                if (isCombination && indices.Count == sets.Count && InRange(indices, sets))
                {
                    // 축별로 값을 나눈다 — 쿠팡이 색상·사이즈를 각각 인식할 수 있다
                    for (var axis = 0; axis < indices.Count; axis++)
                    {
                        optionValues[sets[axis].Name] = indices[axis].ToString();
                        usedValues[axis].Add(indices[axis]);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(displayName))
                {
                    // 독립형 옵션 등 축으로 나눌 수 없는 형태 — 조합 이름을 값 하나로 쓴다.
                    // 억지로 축을 만들면 존재하지 않는 조합이 생긴다.
                    optionValues[flatGroupName] = entry.Name;
                }
                else continue;

                variants.Add(new RawVariant
                {
                    SourceSkuId = entry.Name,   // "00_01" — 도매꾹이 이 조합을 부르는 이름
                    OptionValueIds = optionValues,
                    Price = basePrice + (ParseDecimal(GetString(combo, priceField)) ?? 0),
                    Stock = totalStock == 0 ? 0 : ParseStock(GetString(combo, "qty")),
                });
            }

            if (variants.Count == 0) return ([], []);

            var groups = BuildGroups(sets, usedValues, variants, data, flatGroupName);
            return (groups, variants);
        }
    }

    /// <summary>축으로 나눌 수 없는 옵션을 담는 단일 그룹 이름.</summary>
    private const string FlatGroupName = "옵션";

    /// <summary>
    /// 옵션 그룹 이름 정리.
    ///
    /// 공급처가 "color", "size"처럼 영문으로 적어두는 경우가 많다.
    /// 그대로 쿠팡에 올리면 구매자에게 영문 항목명이 보이고,
    /// 쿠팡 구매옵션 자동 매칭(색상·사이즈 등 한글 속성명과 대조)도 빗나간다.
    /// 아는 이름만 바꾸고 나머지는 공급처 표기를 그대로 둔다.
    /// </summary>
    private static string NormalizeGroupName(string? raw)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name)) return FlatGroupName;

        return name.ToLowerInvariant() switch
        {
            "color" or "colour" or "컬러" or "칼라" => "색상",
            "size" or "사이즈선택" => "사이즈",
            "type" or "타입" => "종류",
            "option" or "옵션선택" or "선택" or "선택하세요" or "필수선택" => FlatGroupName,
            "model" => "모델",
            "style" => "스타일",
            "qty" or "quantity" => "수량",
            _ => name,
        };
    }

    /// <summary>"00_01" → [0, 1]. 숫자가 아니면 null (형태를 모르면 손대지 않는다).</summary>
    private static List<int>? ParseComboKey(string key)
    {
        var indices = new List<int>();
        foreach (var segment in key.Split('_'))
        {
            if (!int.TryParse(segment, out var index) || index < 0) return null;
            indices.Add(index);
        }
        return indices.Count > 0 ? indices : null;
    }

    private static bool InRange(List<int> indices, List<(string Name, List<string> Opts)> sets)
    {
        for (var i = 0; i < indices.Count; i++)
            if (indices[i] >= sets[i].Opts.Count) return false;
        return true;
    }

    /// <summary>재고는 문자열로 온다. 없거나 이상하면 0으로 본다 — 있다고 가정하면 주문을 취소하게 된다.</summary>
    private static int ParseStock(string? raw) =>
        int.TryParse(raw, out var qty) && qty > 0 ? qty : 0;

    /// <summary>같은 이름의 그룹이 여러 개면 뒤쪽에 번호를 붙인다.</summary>
    private static void MakeGroupNamesUnique(List<(string Name, List<string> Opts)> sets)
    {
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < sets.Count; i++)
        {
            var name = sets[i].Name;
            if (seen.TryGetValue(name, out var count))
            {
                seen[name] = count + 1;
                sets[i] = ($"{name} {count + 1}", sets[i].Opts);
            }
            else seen[name] = 1;
        }
    }

    /// <summary>실제 쓰인 값만 모아 옵션 그룹을 만든다.</summary>
    private static List<RawOptionGroup> BuildGroups(
        List<(string Name, List<string> Opts)> sets,
        List<SortedSet<int>> usedValues,
        List<RawVariant> variants,
        JsonElement data,
        string flatGroupName)
    {
        var groups = new List<RawOptionGroup>();

        for (var axis = 0; axis < sets.Count; axis++)
        {
            if (usedValues[axis].Count == 0) continue;
            groups.Add(new RawOptionGroup(
                sets[axis].Name,
                usedValues[axis]
                    .Select(i => new RawOptionValue(i.ToString(), sets[axis].Opts[i], null))
                    .ToList()));
        }

        // 축으로 나누지 못한 조합들은 이름 그대로 단일 그룹에 담는다
        var flatKeys = variants
            .Where(v => v.OptionValueIds.ContainsKey(flatGroupName))
            .Select(v => v.OptionValueIds[flatGroupName])
            .Distinct()
            .ToList();

        if (flatKeys.Count > 0)
        {
            groups.Add(new RawOptionGroup(flatGroupName, flatKeys
                .Select(key => new RawOptionValue(
                    key,
                    data.TryGetProperty(key, out var combo) ? GetString(combo, "name") ?? key : key,
                    null))
                .ToList()));
        }

        return groups;
    }

    /// <summary>
    /// category: { parents: { elem: [{name,code,depth}, …] }, current: {name,code,depth} }
    /// </summary>
    private static List<string> ExtractCategoryPath(JsonElement root)
    {
        if (!root.TryGetProperty("category", out var category)) return [];

        var path = new List<string>();
        if (category.TryGetProperty("parents", out var parents)
            && parents.TryGetProperty("elem", out var elem) && elem.ValueKind == JsonValueKind.Array)
        {
            path.AddRange(elem.EnumerateArray()
                .Select(e => GetString(e, "name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!));
        }
        if (category.TryGetProperty("current", out var current) && GetString(current, "name") is { } leaf)
            path.Add(leaf);

        return path;
    }

    /// <summary>
    /// 마켓에 올릴 상세설명.
    /// 라이선스가 없으면 이미지가 박힌 HTML을 그대로 쓸 수 없으므로 비운다
    /// (호출부가 상품명 기반 기본 설명으로 대체한다).
    /// </summary>
    private static string ExtractDescription(JsonElement root) =>
        IsDetailImageAllowed(root) ? ExtractDetailHtml(root) : "";

    private static Dictionary<string, string> ExtractAttributes(JsonElement root)
    {
        var attributes = new Dictionary<string, string>();

        // ── 위탁판매 물류: 출고지·반품지는 판매자가 아니라 공급처 주소를 쓴다 ──

        if (root.TryGetProperty("seller", out var seller))
        {
            if (GetString(seller, "nick") is { } nick) attributes[LogisticsKeys.SupplierName] = nick;
            if (seller.TryGetProperty("company", out var company))
            {
                // 공급사 사업장 주소 = 출고지
                if (GetString(company, "addr") is { Length: > 0 } addr)
                    attributes[LogisticsKeys.OutboundAddress] = addr;
                if (GetString(company, "phone") is { Length: > 0 } phone)
                    attributes[LogisticsKeys.SupplierPhone] = phone.Trim(' ', '-', '/');
            }
        }

        // return.addr = 공급처가 지정한 반품지 (실측: 주소·우편번호·전화까지 옴)
        if (root.TryGetProperty("return", out var ret))
        {
            if (ret.TryGetProperty("addr", out var addr))
            {
                var address1 = GetString(addr, "address1") ?? "";
                var address2 = GetString(addr, "address2") ?? "";
                var full = $"{address1} {address2}".Trim();
                if (full.Length > 0) attributes[LogisticsKeys.ReturnAddress] = full;
                if (GetString(addr, "zipcode") is { Length: > 0 } zip)
                    attributes[LogisticsKeys.ReturnZipcode] = zip;
                var phone = GetString(addr, "mobile") ?? GetString(addr, "phone");
                if (!string.IsNullOrWhiteSpace(phone)) attributes[LogisticsKeys.ReturnPhone] = phone;
            }
            // deliAmt = 편도 반품 배송비. deliAmtDouble=true면 왕복(초기 무료배송 회수비 포함) 청구
            if (GetString(ret, "deliAmt") is { } returnFee)
            {
                var isDouble = GetString(ret, "deliAmtDouble") is "true" or "TRUE";
                attributes[LogisticsKeys.ReturnFee] = isDouble && decimal.TryParse(returnFee, out var fee)
                    ? ((int)fee * 2).ToString()
                    : returnFee;
            }
        }

        // deli = 공급처 → 구매자 배송 조건 (판매가·배송비 정책에 반영해야 함)
        if (root.TryGetProperty("deli", out var deli))
        {
            if (deli.TryGetProperty("dome", out var dome) && GetString(dome, "fee") is { } deliveryFee)
                attributes[LogisticsKeys.DeliveryFee] = deliveryFee;
            if (deli.TryGetProperty("feeExtra", out var extra))
            {
                if (GetString(extra, "jeju") is { } jeju) attributes[LogisticsKeys.JejuExtraFee] = jeju;
                if (GetString(extra, "islands") is { } islands) attributes[LogisticsKeys.IslandExtraFee] = islands;
            }
            // 평균 출고일 — 마켓에 표기할 배송 소요일의 근거가 된다
            if (GetString(deli, "sendAvg") is { } sendAvg) attributes[LogisticsKeys.AvgOutboundDays] = sendAvg;
            if (GetString(deli, "wating") is { } waiting) attributes[LogisticsKeys.OutboundNote] = waiting;
        }

        // 상세설명 이미지 재사용 허용 여부 — 허용된 경우에만 HTML을 보관한다
        var licensed = IsDetailImageAllowed(root);
        attributes[LogisticsKeys.DetailImageLicense] = licensed ? "Y" : "N";
        if (licensed && ExtractDetailHtml(root) is { Length: > 0 } detailHtml)
            attributes[LogisticsKeys.DetailHtml] = detailHtml;

        // ── 일반 속성 ──

        if (root.TryGetProperty("qty", out var qty))
        {
            if (GetString(qty, "domeMoq") is { } moq) attributes[LogisticsKeys.MinOrderQty] = moq;
            if (GetString(qty, "inventory") is { } inventory) attributes["공급처재고"] = inventory;
        }

        if (root.TryGetProperty("basis", out var basis))
        {
            if (GetString(basis, "status") is { } status) attributes["판매상태"] = status;
            if (GetString(basis, "origin") is { } origin) attributes["원산지"] = origin;

            // 공급처가 정리해 둔 검색 키워드 — 마켓 검색어로 그대로 쓴다
            if (basis.TryGetProperty("keywords", out var keywords)
                && keywords.TryGetProperty("kw", out var kw)
                && kw.ValueKind == JsonValueKind.Array)
            {
                var words = kw.EnumerateArray()
                    .Select(k => k.GetString())
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Select(k => k!.Trim())
                    .Distinct()
                    .Take(20)
                    .ToList();
                if (words.Count > 0)
                    attributes[LogisticsKeys.SearchKeywords] = string.Join(",", words);
            }
        }

        return attributes;
    }

    private static IEnumerable<JsonElement> SingleItem(JsonElement element)
    {
        yield return element;
    }

    /// <summary>
    /// 대분류 여부. 도매꾹 코드는 <c>대_중_소_세_세세</c> 5단이며
    /// 대분류는 <c>01_00_00_00_00</c>처럼 뒤가 모두 00이다.
    /// 실측 결과 대분류만으로는 상품 조회가 거부된다(검색 조건으로 인정 안 됨).
    /// </summary>
    private static bool IsTopLevelCategory(string code) =>
        TopLevelCategoryRegex().IsMatch(code);

    private static string? ExtractItemNo(Uri url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (query["no"] is { Length: > 0 } no) return no;
        var match = ItemNoRegex().Match(url.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };
    }

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value?.Replace(",", ""), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"/(\d{6,})")]
    private static partial Regex ItemNoRegex();
    [GeneratedRegex(@"^\d+(_00){4}$")]
    private static partial Regex TopLevelCategoryRegex();
    [GeneratedRegex("""<img[^>]+src=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex DescImageRegex();
}

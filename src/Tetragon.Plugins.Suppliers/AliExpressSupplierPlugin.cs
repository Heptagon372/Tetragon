using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// AliExpress 수집 플러그인.
///
/// 중요 — 2026년 현재 AliExpress 상세 페이지는 완전 클라이언트 사이드 렌더링(CSR)이다.
/// 서버가 내려주는 HTML에는 og: 메타태그(상품명·대표이미지)만 있고
/// 가격·옵션·재고는 로그인 서명이 필요한 XHR로만 로드된다. (직접 확인함:
/// window.runParams는 빈 객체, &lt;title&gt;도 빈 문자열)
///
/// 따라서 두 가지 모드로 동작한다:
///  1) 공식 Open Platform API 자격증명이 있으면 → aliexpress.ds.product.get 호출 (완전 수집)
///  2) 없으면 → og: 메타태그로 상품명/이미지만 확인하고, 가격을 얻을 수 없다는
///     명확한 안내와 함께 영구 실패 처리 (재시도해도 소용없음)
/// </summary>
public sealed partial class AliExpressSupplierPlugin(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<AliExpressSupplierPlugin> logger) : ISupplierPlugin
{
    public string Code => "aliexpress";
    public string DisplayName => "AliExpress";
    public string Version => "2.0.0";
    public bool IsLive => true;

    /// <summary>공식 API 키가 있어야 가격까지 수집할 수 있다.</summary>
    public bool IsAvailable =>
        credentials.HasKey(CredentialScope, "app_key") && credentials.HasKey(CredentialScope, "app_secret");

    private const string CredentialScope = "supplier:aliexpress";
    private const string ApiGateway = "https://api-sg.aliexpress.com/sync";

    public bool CanHandle(Uri url) =>
        url.Host.EndsWith("aliexpress.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith("aliexpress.us", StringComparison.OrdinalIgnoreCase);

    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var productId = ExtractProductId(productUrl)
            ?? throw new PermanentScrapeException($"AliExpress 상품 ID를 URL에서 찾을 수 없습니다: {productUrl}");

        var appKey = credentials.Get(CredentialScope, "app_key");
        var appSecret = credentials.Get(CredentialScope, "app_secret");

        if (!string.IsNullOrWhiteSpace(appKey) && !string.IsNullOrWhiteSpace(appSecret))
            return await CollectViaOpenApiAsync(productId, productUrl, appKey, appSecret, ct);

        // 자격증명이 없으면 페이지에서 확인 가능한 것만 검사하고 정확한 안내를 남긴다.
        var (title, _) = await FetchMetaAsync(productUrl, ctx, ct);
        throw new PermanentScrapeException(
            $"AliExpress 상품 '{Truncate(title, 30)}'을(를) 찾았지만 가격·옵션을 가져올 수 없습니다. " +
            "AliExpress 상세 페이지는 클라이언트 렌더링이라 가격이 HTML에 없습니다. " +
            "설정 → 공급처 → aliexpress에 Open Platform의 app_key / app_secret / access_token을 등록하세요.");
    }

    // ── 공식 Open Platform API ──────────────────────────────────────────

    private async Task<RawProduct> CollectViaOpenApiAsync(
        string productId, Uri productUrl, string appKey, string appSecret, CancellationToken ct)
    {
        var accessToken = credentials.Get(CredentialScope, "access_token")
            ?? throw new PermanentScrapeException(
                "AliExpress access_token이 없습니다. 설정 → 공급처 → aliexpress에 등록하세요.");

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = "aliexpress.ds.product.get",
            ["app_key"] = appKey,
            ["access_token"] = accessToken,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            ["sign_method"] = "sha256",
            ["product_id"] = productId,
            ["target_currency"] = credentials.Get(CredentialScope, "currency") ?? "USD",
            ["target_language"] = "en",
            ["ship_to_country"] = "KR",
        };
        parameters["sign"] = Sign(parameters, appSecret);

        var client = httpClientFactory.CreateClient("scraper");
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await client.PostAsync(ApiGateway, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new TransientScrapeException($"AliExpress API 오류: HTTP {(int)response.StatusCode}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error_response", out var error))
        {
            var message = error.TryGetProperty("msg", out var m) ? m.GetString() : body;
            var code = error.TryGetProperty("code", out var c) ? c.ToString() : "";
            // 인증/권한 오류는 재시도해도 동일하다
            throw code.StartsWith('1') || code.StartsWith('2')
                ? new PermanentScrapeException($"AliExpress API 거부 ({code}): {message}")
                : new TransientScrapeException($"AliExpress API 오류 ({code}): {message}");
        }

        var result = FindResultNode(root)
            ?? throw new TransientScrapeException("AliExpress API 응답 구조를 해석할 수 없습니다.");

        return MapToRawProduct(result, productId, productUrl, body);
    }

    /// <summary>TOP 게이트웨이 서명: secret + 정렬된 key/value 연결 + secret → HMAC-SHA256 대문자 HEX.</summary>
    private static string Sign(SortedDictionary<string, string> parameters, string appSecret)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (key == "sign") continue;
            sb.Append(key).Append(value);
        }
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    /// <summary>API 응답의 result 노드를 찾는다 (래핑 구조가 버전마다 다름).</summary>
    private static JsonElement? FindResultNode(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Contains("response", StringComparison.OrdinalIgnoreCase)) continue;
            var node = property.Value;
            if (node.TryGetProperty("result", out var result)) return result;
            if (node.TryGetProperty("rsp_result", out var rsp)
                && rsp.TryGetProperty("result", out var inner)) return inner;
            return node;
        }
        return root.TryGetProperty("result", out var direct) ? direct : null;
    }

    private static RawProduct MapToRawProduct(
        JsonElement result, string productId, Uri productUrl, string rawJson)
    {
        var title = GetString(result, "subject") ?? GetString(result, "product_title") ?? "제목 없음";
        var currency = GetString(result, "target_sale_price_currency")
                       ?? GetString(result, "currency_code") ?? "USD";

        var images = new List<string>();
        if (GetString(result, "product_main_image_url") is { } mainImage) images.Add(mainImage);
        if (GetString(result, "image_urls") is { } imageUrls)
            images.AddRange(imageUrls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries));
        if (result.TryGetProperty("ae_multimedia_info_dto", out var media)
            && media.TryGetProperty("image_urls", out var urls) && urls.ValueKind == JsonValueKind.String)
            images.AddRange((urls.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));

        var (groups, variants, basePrice) = ParseSkus(result, currency);
        if (basePrice == 0)
            basePrice = ParseDecimal(GetString(result, "target_sale_price"))
                        ?? ParseDecimal(GetString(result, "sale_price")) ?? 0;

        if (basePrice == 0)
            throw new TransientScrapeException("AliExpress API 응답에서 가격을 찾을 수 없습니다.");

        return new RawProduct
        {
            SupplierCode = "aliexpress",
            SourceProductId = productId,
            Url = productUrl.ToString(),
            Title = title,
            TitleLocale = "en",
            Description = GetString(result, "detail") ?? "",
            ImageUrls = images.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct().Take(10).ToList(),
            CategoryPath = GetString(result, "category_name") is { } category ? [category] : [],
            OptionGroups = groups,
            Variants = variants,
            Currency = currency,
            BasePrice = basePrice,
            RawJson = rawJson,
        };
    }

    private static (List<RawOptionGroup>, List<RawVariant>, decimal) ParseSkus(JsonElement result, string currency)
    {
        var groups = new Dictionary<string, List<RawOptionValue>>();
        var variants = new List<RawVariant>();
        decimal minPrice = 0;

        if (!result.TryGetProperty("ae_item_sku_info_dtos", out var skuContainer))
            return ([], [], 0);

        var skuList = skuContainer.TryGetProperty("ae_item_sku_info_d_t_o", out var inner) ? inner : skuContainer;
        if (skuList.ValueKind != JsonValueKind.Array) return ([], [], 0);

        foreach (var sku in skuList.EnumerateArray())
        {
            var skuId = GetString(sku, "sku_id") ?? Guid.NewGuid().ToString("N");
            var price = ParseDecimal(GetString(sku, "offer_sale_price"))
                        ?? ParseDecimal(GetString(sku, "sku_price")) ?? 0;
            var stock = int.TryParse(GetString(sku, "sku_available_stock"), out var s) ? s : 0;

            var optionIds = new Dictionary<string, string>();
            if (sku.TryGetProperty("ae_sku_property_dtos", out var propContainer))
            {
                var props = propContainer.TryGetProperty("ae_sku_property_d_t_o", out var pInner) ? pInner : propContainer;
                if (props.ValueKind == JsonValueKind.Array)
                {
                    foreach (var prop in props.EnumerateArray())
                    {
                        var groupName = GetString(prop, "sku_property_name") ?? "옵션";
                        var valueId = GetString(prop, "property_value_id_long")
                                      ?? GetString(prop, "property_value_id") ?? "";
                        var valueName = GetString(prop, "sku_property_value")
                                        ?? GetString(prop, "property_value_definition_name") ?? valueId;

                        if (!groups.TryGetValue(groupName, out var values))
                            groups[groupName] = values = [];
                        if (values.All(v => v.Id != valueId))
                            values.Add(new RawOptionValue(valueId, valueName, GetString(prop, "sku_image")));
                        optionIds[groupName] = valueId;
                    }
                }
            }

            variants.Add(new RawVariant
            {
                SourceSkuId = skuId,
                OptionValueIds = optionIds,
                Price = price,
                Stock = stock,
            });
            if (price > 0 && (minPrice == 0 || price < minPrice)) minPrice = price;
        }

        return (groups.Select(g => new RawOptionGroup(g.Key, g.Value)).ToList(), variants, minPrice);
    }

    // ── 재고 확인 ────────────────────────────────────────────────────────

    public async Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct)
    {
        if (!IsAvailable)
            return new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = true };

        try
        {
            var raw = await CollectViaOpenApiAsync(
                source.SourceProductId, new Uri(source.Url),
                credentials.Get(CredentialScope, "app_key")!,
                credentials.Get(CredentialScope, "app_secret")!, ct);
            return new RawInventory
            {
                SourceProductId = source.SourceProductId,
                IsAvailable = raw.Variants.Any(v => v.Stock > 0),
                Variants = raw.Variants,
            };
        }
        catch (PermanentScrapeException)
        {
            // 판매 종료로 간주
            return new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = false };
        }
    }

    // ── 페이지 메타태그 확인 (자격증명 없을 때 상품 존재 여부만) ──────────

    private async Task<(string Title, string? Image)> FetchMetaAsync(
        Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("scraper");
        using var request = ScraperHttp.BuildRequest(productUrl, ctx.CookieHeader);
        using var response = await client.SendAsync(request, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        if ((int)response.StatusCode == 404)
            throw new PermanentScrapeException("AliExpress 상품을 찾을 수 없습니다 (404).");
        if (!response.IsSuccessStatusCode)
            throw new TransientScrapeException($"AliExpress 응답 오류: HTTP {(int)response.StatusCode}");
        if (ScraperHttp.LooksBlocked(html))
            throw new TransientScrapeException("AliExpress 봇 차단 감지 — 잠시 후 재시도하세요.");

        // 삭제·판매종료 상품은 og:title이 빈 문자열로 온다 (실제 상품은 채워져 있음).
        // 주의: PAGE_NOT_FOUND_* 문자열은 모든 페이지의 i18n 번들에 존재하므로 판단 근거로 쓸 수 없다.
        var title = MetaContent(html, "og:title");
        if (string.IsNullOrWhiteSpace(title))
            throw new PermanentScrapeException("AliExpress에서 판매 종료되었거나 존재하지 않는 상품입니다.");

        logger.LogInformation("AliExpress 상품 확인: {Title}", Truncate(title, 40));
        return (title, MetaContent(html, "og:image"));
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────

    private static string? ExtractProductId(Uri url)
    {
        var match = ItemIdRegex().Match(url.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;

    private static string? MetaContent(string html, string property)
    {
        var match = Regex.Match(html,
            $"""<meta[^>]+property=["']{Regex.Escape(property)}["'][^>]+content=["']([^"']*)["']""",
            RegexOptions.IgnoreCase);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"/item/(?:[^/]*/)?(\d+)\.html")]
    private static partial Regex ItemIdRegex();
}

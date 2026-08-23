using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 타오바오/티몰 스크래퍼 (실연동 — 로그인 쿠키 필요).
/// h5 mtop API(mtop.taobao.detail.getdetail)를 _m_h5_tk 토큰 서명 방식으로 호출한다.
/// 쿠키 미설정 시 명확한 안내 오류를 던진다.
/// </summary>
public sealed partial class TaobaoSupplierPlugin(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<TaobaoSupplierPlugin> logger) : ISupplierPlugin
{
    public string Code => "taobao";
    public string DisplayName => "타오바오/티몰";
    public string Version => "1.0.0";
    public bool IsLive => true;
    /// <summary>로그인 쿠키가 있어야 수집할 수 있다.</summary>
    public bool IsAvailable => credentials.HasKey("supplier:taobao", "cookie");

    private const string AppKey = "12574478"; // 타오바오 h5 공용 appKey

    public bool CanHandle(Uri url) =>
        url.Host.EndsWith("taobao.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith("tmall.com", StringComparison.OrdinalIgnoreCase);

    public async Task<RawProduct> CollectAsync(Uri productUrl, ScrapeContext ctx, CancellationToken ct)
    {
        var itemId = ExtractItemId(productUrl)
            ?? throw new PermanentScrapeException($"타오바오 상품 ID(id=)를 URL에서 찾을 수 없습니다: {productUrl}");

        // 설정 문제는 재시도해도 해결되지 않는다 — 사용자가 쿠키를 넣어야 한다.
        if (string.IsNullOrWhiteSpace(ctx.CookieHeader))
            throw new PermanentScrapeException(
                "타오바오 수집에는 로그인 쿠키가 필요합니다. 설정 → 공급처 → taobao에 'cookie' 값을 등록하세요. " +
                "(브라우저 개발자도구 → Network → 요청 헤더의 Cookie 전체 복사)");

        var client = httpClientFactory.CreateClient("scraper");

        // _m_h5_tk 토큰 추출 (쿠키에 있어야 함)
        var token = ExtractH5Token(ctx.CookieHeader)
            ?? throw new PermanentScrapeException(
                "쿠키에 _m_h5_tk 토큰이 없습니다. taobao.com에 로그인한 브라우저의 쿠키 전체를 복사했는지 확인하세요.");

        var dataJson = JsonSerializer.Serialize(new { itemNumId = itemId });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var sign = Md5Hex($"{token}&{timestamp}&{AppKey}&{dataJson}");

        var apiUrl = "https://h5api.m.taobao.com/h5/mtop.taobao.detail.getdetail/6.0/" +
                     $"?jsv=2.6.1&appKey={AppKey}&t={timestamp}&sign={sign}" +
                     $"&api=mtop.taobao.detail.getdetail&v=6.0&dataType=jsonp&type=jsonp" +
                     $"&data={Uri.EscapeDataString(dataJson)}";

        using var request = ScraperHttp.BuildRequest(new Uri(apiUrl), ctx.CookieHeader, "https://item.taobao.com/");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // jsonp 벗기기: mtopjsonp1({...})
        var json = StripJsonp(body);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var ret = root.TryGetProperty("ret", out var retArr) && retArr.ValueKind == JsonValueKind.Array
            ? retArr[0].GetString() ?? "" : "";
        if (!ret.StartsWith("SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            // 토큰 만료(FAIL_SYS_TOKEN_*)는 재시도 시 갱신될 수 있으나,
            // 세션 만료/권한 오류는 사용자가 쿠키를 다시 넣어야 한다.
            var isTokenIssue = ret.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);
            var message = $"타오바오 API 거부: {ret} — 쿠키가 만료됐거나 차단됐을 수 있습니다. 쿠키를 갱신하세요.";
            throw isTokenIssue
                ? new TransientScrapeException(message)
                : new PermanentScrapeException(message);
        }

        var data = root.GetProperty("data");
        var item = data.GetProperty("item");
        var title = item.GetProperty("title").GetString() ?? "제목 없음";
        var images = item.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array
            ? imgs.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).Select(s => s!).Take(10).ToList()
            : [];

        var (optionGroups, variants, basePrice) = ParseSkus(data);

        logger.LogInformation("타오바오 수집 성공: {ItemId} ({Title})", itemId, title.Length > 40 ? title[..40] : title);

        return new RawProduct
        {
            SupplierCode = Code,
            SourceProductId = itemId,
            Url = productUrl.ToString(),
            Title = title,
            TitleLocale = "zh-CN",
            Description = "",
            ImageUrls = images,
            OptionGroups = optionGroups,
            Variants = variants,
            Currency = "CNY",
            BasePrice = basePrice,
            RawJson = json,
        };
    }

    public Task<RawInventory> CheckInventoryAsync(SourceRef source, CancellationToken ct) =>
        Task.FromResult(new RawInventory { SourceProductId = source.SourceProductId, IsAvailable = true });

    private static (List<RawOptionGroup>, List<RawVariant>, decimal) ParseSkus(JsonElement data)
    {
        var groups = new List<RawOptionGroup>();
        var variants = new List<RawVariant>();
        decimal basePrice = 0;

        // skuBase.props → 옵션, skuBase.skus + skuCore.sku2info → SKU 가격/재고
        if (data.TryGetProperty("skuBase", out var skuBase))
        {
            var pidToName = new Dictionary<string, string>();
            if (skuBase.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                foreach (var prop in props.EnumerateArray())
                {
                    var name = prop.TryGetProperty("name", out var n) ? n.GetString() ?? "옵션" : "옵션";
                    var pid = prop.TryGetProperty("pid", out var p) ? p.GetString() ?? "" : "";
                    pidToName[pid] = name;
                    var values = new List<RawOptionValue>();
                    if (prop.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                        foreach (var v in vals.EnumerateArray())
                            values.Add(new RawOptionValue(
                                v.TryGetProperty("vid", out var vid) ? vid.GetString() ?? "" : "",
                                v.TryGetProperty("name", out var vn) ? vn.GetString() ?? "" : "",
                                v.TryGetProperty("image", out var vi) ? vi.GetString() : null));
                    groups.Add(new RawOptionGroup(name, values));
                }
            }

            var skuInfo = data.TryGetProperty("skuCore", out var skuCore)
                          && skuCore.TryGetProperty("sku2info", out var s2i) ? s2i : default;

            if (skuBase.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Array)
            {
                foreach (var sku in skus.EnumerateArray())
                {
                    var skuId = sku.TryGetProperty("skuId", out var sid) ? sid.GetString() ?? "" : "";
                    var propPath = sku.TryGetProperty("propPath", out var pp) ? pp.GetString() ?? "" : "";
                    // propPath 형식: "pid:vid;pid:vid"
                    var optionIds = new Dictionary<string, string>();
                    foreach (var pair in propPath.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = pair.Split(':');
                        if (parts.Length == 2 && pidToName.TryGetValue(parts[0], out var groupName))
                            optionIds[groupName] = parts[1];
                    }

                    decimal price = 0;
                    var stock = 0;
                    if (skuInfo.ValueKind == JsonValueKind.Object && skuInfo.TryGetProperty(skuId, out var info))
                    {
                        if (info.TryGetProperty("price", out var priceObj)
                            && priceObj.TryGetProperty("priceText", out var priceText))
                            decimal.TryParse(priceText.GetString()?.Split('-')[0].Trim(), out price);
                        if (info.TryGetProperty("quantity", out var qty))
                            int.TryParse(qty.GetString() ?? qty.ToString(), out stock);
                    }

                    variants.Add(new RawVariant
                    {
                        SourceSkuId = skuId,
                        OptionValueIds = optionIds,
                        Price = price,
                        Stock = stock,
                    });
                }
            }

            // 기준가: sku2info의 "0" (전체) 또는 최소 SKU 가격
            if (skuInfo.ValueKind == JsonValueKind.Object && skuInfo.TryGetProperty("0", out var baseInfo)
                && baseInfo.TryGetProperty("price", out var bp) && bp.TryGetProperty("priceText", out var bpt))
                decimal.TryParse(bpt.GetString()?.Split('-')[0].Trim(), out basePrice);
            if (basePrice == 0 && variants.Count > 0)
                basePrice = variants.Where(v => v.Price > 0).Select(v => v.Price).DefaultIfEmpty(0).Min();
        }

        return (groups, variants, basePrice);
    }

    private static string? ExtractItemId(Uri url)
    {
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (query["id"] is string id && id.Length > 0) return id;
        var match = ItemPathRegex().Match(url.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractH5Token(string cookie)
    {
        var match = H5TokenRegex().Match(cookie);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string StripJsonp(string body)
    {
        var start = body.IndexOf('(');
        var end = body.LastIndexOf(')');
        return start >= 0 && end > start ? body[(start + 1)..end] : body;
    }

    private static string Md5Hex(string input) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    [GeneratedRegex(@"_m_h5_tk=([0-9a-f]+)_")]
    private static partial Regex H5TokenRegex();
    [GeneratedRegex(@"/item/(\d+)")]
    private static partial Regex ItemPathRegex();
}

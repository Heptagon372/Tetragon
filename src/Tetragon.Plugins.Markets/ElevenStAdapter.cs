using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 11번가 셀러오피스 OpenAPI 어댑터.
///
/// 인증: openapikey 헤더 (셀러오피스 → API 관리에서 발급)
/// 요청/응답이 XML이다 (JSON 미지원).
/// 필요 자격증명: api_key, 그리고 발송지/반품지 주소 코드.
/// </summary>
public sealed class ElevenStAdapter(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentialProvider,
    ILogger<ElevenStAdapter> logger) : IMarketplaceAdapter
{
    public string Code => "11st";
    public string DisplayName => "11번가";
    public string Version => "1.0.0";
    public bool IsLive => true;
    public bool IsAvailable => credentialProvider.HasKey("market:11st", "api_key");

    private const string BaseUrl = "https://api.11st.co.kr/rest";

    private HttpRequestMessage Build(HttpMethod method, string path, MarketCredential cred, string? xmlBody = null)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("openapikey", cred.Require("api_key"));
        if (xmlBody is not null)
            request.Content = new StringContent(xmlBody, Encoding.UTF8, "text/xml");
        return request;
    }

    public async Task<ListingResult> RegisterAsync(ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var xml = BuildProductXml(payload, cred);
            var client = httpClientFactory.CreateClient("11st");
            using var request = Build(HttpMethod.Post, "/prodservices/product", cred, xml);
            using var response = await client.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            var (ok, code, message, productNo) = ParseResult(raw);
            if (!response.IsSuccessStatusCode || !ok)
            {
                logger.LogWarning("11번가 등록 실패 [{Code}]: {Message}", code, message);
                return ListingResult.Fail(code ?? $"HTTP_{(int)response.StatusCode}", message ?? raw, raw);
            }
            return ListingResult.Ok(productNo ?? "unknown", raw);
        }
        catch (PluginCredentialException ex) { return ListingResult.Fail("CREDENTIAL", ex.Message); }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    private static string BuildProductXml(ListingPayload payload, MarketCredential cred)
    {
        var categoryCode = payload.MarketCategoryCodes.GetValueOrDefault("11st")
            ?? cred.Get("default_category_code")
            ?? throw new PluginCredentialException(
                "11번가 카테고리 코드가 없습니다. 설정에서 default_category_code를 등록하세요.");

        var mainImage = payload.ImageUrls.FirstOrDefault() ?? "";

        // 공급처가 허용한 경우에만 상세 HTML을 그대로 쓴다 (저작권)
        var detailHtml = new StringBuilder();
        if (payload.Logistics is { DetailImagesAllowed: true, DetailHtml: { Length: > 0 } supplierHtml })
        {
            detailHtml.Append(supplierHtml);
        }
        else
        {
            detailHtml.Append("<div style=\"text-align:center\">");
            detailHtml.Append(System.Net.WebUtility.HtmlEncode(payload.Name));
            foreach (var url in payload.ImageUrls.Take(10))
                detailHtml.Append($"<img src=\"{url}\" style=\"max-width:100%\">");
            detailHtml.Append("</div>");
        }

        // 11번가는 XML 요청을 받는다. XDocument로 만들어 이스케이프를 보장한다.
        var product = new XElement("Product",
            new XElement("selMthdCd", "01"),                       // 판매방식: 고정가
            new XElement("dispCtgrNo", categoryCode),
            new XElement("prdNm", Truncate(payload.Name, 100)),
            new XElement("brand", payload.Attributes.GetValueOrDefault("브랜드", "")),
            new XElement("prdStatCd", "01"),                        // 새 상품
            new XElement("selPrdClfCd", "01"),
            new XElement("prdImage01", mainImage),
            new XElement("htmlDetail", new XCData(detailHtml.ToString())),
            new XElement("selPrc", (int)payload.SalePrice.Amount),
            new XElement("prdSelQty", payload.Stock),
            new XElement("dlvCnAreaCd", "01"),
            new XElement("dlvEtprsCd", cred.Get("delivery_company_code") ?? "00034"),
            new XElement("dlvCstInstBasiCd", "01"),
            new XElement("dlvCst1", cred.Get("delivery_fee") ?? "0"),
            new XElement("rtngdDlvCst", ((int)(payload.Logistics.ReturnFee ?? 5000)).ToString()),
            new XElement("exchDlvCst", cred.Get("exchange_fee") ?? "10000"),
            new XElement("addrSeq", cred.Get("outbound_address_seq") ?? ""),    // 발송지 주소 순번
            new XElement("rtngdDlvCn", cred.Get("return_address_seq") ?? ""),   // 반품지 주소 순번
            // 위탁판매: 반품·A/S 문의는 공급처로 연결되어야 한다
            new XElement("asDetail",
                payload.Logistics.SupplierName is { Length: > 0 } supplier
                    ? $"{supplier} ({payload.Logistics.SupplierPhone ?? cred.Get("contact_number")})"
                    : cred.Get("as_guide") ?? "판매자에게 문의 바랍니다."),
            new XElement("rtnDetail", cred.Get("return_guide") ?? "수령 후 7일 이내 반품 가능합니다."),
            new XElement("prdImageCn", string.Join(",", payload.ImageUrls.Skip(1).Take(9))),
            // 해외구매대행 표기
            new XElement("abrdBuyPlaceNo", cred.Get("abroad_place_code") ?? ""),
            new XElement("prdWght", "1"));

        // 옵션 (조합형)
        if (payload.OptionGroups.Count > 0 && payload.Variants.Count > 0)
        {
            var options = new XElement("ProductOption");
            foreach (var variant in payload.Variants.Take(300))
            {
                options.Add(new XElement("ProductOptionExt",
                    new XElement("colOptNm", string.Join(",", payload.OptionGroups.Select(g => g.Name))),
                    new XElement("colOptVal", string.Join(",", variant.Options.Values)),
                    new XElement("optPrc", (int)(variant.Price.Amount - payload.SalePrice.Amount)),
                    new XElement("optSelQty", variant.Stock)));
            }
            product.Add(options);
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), product).ToString();
    }

    /// <summary>11번가 응답 XML: &lt;ResultCode&gt;, &lt;ResultMsg&gt;, &lt;prdNo&gt;</summary>
    private static (bool Ok, string? Code, string? Message, string? ProductNo) ParseResult(string raw)
    {
        try
        {
            var doc = XDocument.Parse(raw);
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultCode")?.Value;
            var message = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultMsg")?.Value;
            var productNo = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "prdNo")?.Value;
            // ResultCode 200/0 이 성공
            return (code is "200" or "0", code, message, productNo);
        }
        catch (System.Xml.XmlException)
        {
            return (false, "PARSE", raw.Length > 200 ? raw[..200] : raw, null);
        }
    }

    public async Task<ListingResult> UpdateAsync(string marketItemId, ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var xml = BuildProductXml(payload, cred);
            var client = httpClientFactory.CreateClient("11st");
            using var request = Build(HttpMethod.Put, $"/prodservices/product/{marketItemId}", cred, xml);
            using var response = await client.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            var (ok, code, message, _) = ParseResult(raw);
            return response.IsSuccessStatusCode && ok
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail(code ?? "ERROR", message ?? raw, raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<ListingResult> DeleteAsync(string marketItemId, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("11st");
            using var request = Build(HttpMethod.Delete, $"/prodservices/product/{marketItemId}", cred);
            using var response = await client.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            var (ok, code, message, _) = ParseResult(raw);
            return response.IsSuccessStatusCode && ok
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail(code ?? "ERROR", message ?? raw, raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<ListingResult> UpdatePriceStockAsync(
        string marketItemId, Money price, int stock, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("11st");
            // 가격과 재고는 별도 엔드포인트
            var priceXml = new XDocument(new XElement("Product",
                new XElement("selPrc", (int)price.Amount))).ToString();
            using var priceRequest = Build(HttpMethod.Put, $"/prodservices/product/{marketItemId}/price", cred, priceXml);
            using var priceResponse = await client.SendAsync(priceRequest, ct);
            var priceRaw = await priceResponse.Content.ReadAsStringAsync(ct);

            var stockXml = new XDocument(new XElement("Product",
                new XElement("prdSelQty", stock))).ToString();
            using var stockRequest = Build(HttpMethod.Put, $"/prodservices/product/{marketItemId}/stock", cred, stockXml);
            using var stockResponse = await client.SendAsync(stockRequest, ct);
            var stockRaw = await stockResponse.Content.ReadAsStringAsync(ct);

            var (priceOk, _, priceMessage, _) = ParseResult(priceRaw);
            var (stockOk, _, stockMessage, _) = ParseResult(stockRaw);
            return priceOk && stockOk
                ? ListingResult.Ok(marketItemId)
                : ListingResult.Fail("PARTIAL", $"가격: {priceMessage} / 재고: {stockMessage}");
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<IReadOnlyList<MarketOrder>> FetchOrdersAsync(DateRange range, MarketCredential cred, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("11st");
        var path = $"/ordservices/complete/{range.From:yyyyMMdd}/{range.To:yyyyMMdd}";
        using var request = Build(HttpMethod.Get, path, cred);
        using var response = await client.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"11번가 주문 조회 실패: HTTP {(int)response.StatusCode}");

        var orders = new List<MarketOrder>();
        try
        {
            var doc = XDocument.Parse(raw);
            foreach (var order in doc.Descendants().Where(e => e.Name.LocalName == "order"))
            {
                string? Value(string name) =>
                    order.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

                orders.Add(new MarketOrder
                {
                    MarketOrderId = Value("ordNo") ?? "",
                    MarketCode = Code,
                    MarketItemId = Value("prdNo"),
                    ProductName = Value("prdNm"),
                    OptionName = Value("optNm"),
                    Quantity = int.TryParse(Value("ordQty"), out var q) ? q : 1,
                    PaidAmount = Money.Krw(decimal.TryParse(Value("ordPrc"), out var amount) ? amount : 0),
                    OrdererName = Value("ordNm"),
                    OrderedAt = DateTimeOffset.TryParse(Value("ordDt"), out var at) ? at : DateTimeOffset.UtcNow,
                    Status = Value("ordStat") ?? "정상주문",
                });
            }
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException($"11번가 주문 응답 파싱 실패: {ex.Message}");
        }
        return orders;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

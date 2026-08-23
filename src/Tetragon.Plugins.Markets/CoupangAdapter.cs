using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 쿠팡 WING OpenAPI 어댑터 (실연동).
/// 인증: HMAC-SHA256 서명 (CEA 스킴).
/// 필요 자격증명: access_key, secret_key, vendor_id (+ 출고지/반품지 코드).
/// </summary>
public sealed partial class CoupangAdapter(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    CoupangThrottle throttle,
    ILogger<CoupangAdapter> logger) : IMarketplaceAdapter
{
    public string Code => "coupang";
    public string DisplayName => "쿠팡";
    public string Version => "1.0.0";
    public bool IsLive => true;
    public bool IsAvailable =>
        credentials.HasKey("market:coupang", "access_key")
        && credentials.HasKey("market:coupang", "secret_key")
        && credentials.HasKey("market:coupang", "vendor_id");

    private const string BaseUrl = "https://api-gateway.coupang.com";

    /// <summary>
    /// 쿠팡 최소 판매가. 이보다 싸게 보내면 옵션 단위로 거부된다 (실측 오류 메시지 기준).
    /// 도매 원가가 낮은 상품(900원짜리 등)이 자주 걸린다.
    /// </summary>
    internal const decimal MinCoupangSalePrice = 1000m;

    /// <summary>판매가를 쿠팡이 받아주는 범위로 올린다. 낮추는 일은 없다.</summary>
    internal static decimal CoupangSalePrice(decimal amount) =>
        amount > 0 && amount < MinCoupangSalePrice ? MinCoupangSalePrice : amount;

    // ── HMAC 서명 (쿠팡 CEA 스킴) ───────────────────────────────────────

    private static string BuildAuthorization(string method, string path, string query, string accessKey, string secretKey)
    {
        var datetime = DateTimeOffset.UtcNow.ToString("yyMMdd'T'HHmmss'Z'");
        var message = datetime + method + path + query;
        var signature = Convert.ToHexString(
            new HMACSHA256(Encoding.UTF8.GetBytes(secretKey))
                .ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        return $"CEA algorithm=HmacSHA256, access-key={accessKey}, signed-date={datetime}, signature={signature}";
    }

    /// <summary>
    /// 서명해서 보낸다. 응답 본문까지 함께 돌려주는 이유는
    /// 조절기가 빈 응답을 판단해 재시도해야 하기 때문이다.
    ///
    /// 서명에는 요청 시각이 들어가므로 <b>재시도마다 요청을 새로 만들어야</b> 한다
    /// (같은 HttpRequestMessage를 두 번 보낼 수도 없다).
    /// </summary>
    private async Task<(HttpResponseMessage Response, string Body)> SendWithBodyAsync(
        HttpMethod method, string path, string query, object? jsonBody,
        MarketCredential cred, string operation, CancellationToken ct)
    {
        var accessKey = cred.Require("access_key");
        var secretKey = cred.Require("secret_key");
        var client = httpClientFactory.CreateClient("coupang");
        var url = BaseUrl + path + (query.Length > 0 ? "?" + query : "");

        return await throttle.ExecuteAsync(async token =>
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization",
                BuildAuthorization(method.Method, path, query, accessKey, secretKey));
            request.Headers.TryAddWithoutValidation("X-EXTENDED-TIMEOUT", "90000");
            if (jsonBody is not null)
                request.Content = JsonContent.Create(jsonBody, options: new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                });
            return await client.SendAsync(request, token);
        }, operation, ct);
    }

    /// <summary>본문이 필요 없는 호출용 (기존 호출부 호환).</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string query, object? jsonBody, MarketCredential cred, CancellationToken ct)
    {
        var (response, body) = await SendWithBodyAsync(method, path, query, jsonBody, cred, path, ct);
        // 조절기가 이미 본문을 읽었으므로 다시 읽을 수 있게 되돌려 준다
        response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return response;
    }

    /// <summary>
    /// "반품배송비는 0원 ~ 5,000원 까지 가능합니다" 형태의 오류에서 상한을 읽는다.
    /// 상한은 판매가에 따라 달라지고 공식이 공개돼 있지 않아, 응답을 근거로 맞추는 편이 확실하다.
    /// </summary>
    private static int? ParseReturnChargeLimit(string raw)
    {
        if (!raw.Contains("반품배송비", StringComparison.Ordinal)) return null;
        var match = ReturnChargeLimitRegex().Match(raw);
        return match.Success && int.TryParse(match.Groups[1].Value.Replace(",", ""), out var limit)
            ? limit
            : null;
    }

    /// <summary>
    /// 쿠팡이 자주 돌려주는 정책성 오류를 사람이 바로 조치할 수 있는 안내로 바꾼다.
    /// 원문(영문 + 콜센터 번호)만으로는 무엇을 해야 하는지 알기 어렵다.
    /// 매칭되지 않으면 null을 반환해 원문을 그대로 쓴다.
    /// </summary>
    private static string? TranslateKnownError(int statusCode, string raw)
    {
        // IP 화이트리스트 — 쿠팡 오픈API는 WING에 등록한 IP에서만 호출을 허용한다
        if (statusCode == 403 && raw.Contains("ip address", StringComparison.OrdinalIgnoreCase)
            && raw.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            var ip = IpAddressRegex().Match(raw) is { Success: true } m ? m.Value : "(응답에서 IP를 찾지 못함)";
            return $"쿠팡이 이 서버의 IP({ip})를 허용하지 않았습니다. " +
                   $"쿠팡 WING → 판매자정보 → 오픈API 키 발급/관리 → 접근 가능 IP에 {ip}를 등록하세요. " +
                   "가정용 인터넷은 IP가 바뀔 수 있으니, 다시 같은 오류가 나면 변경된 IP로 갱신해야 합니다.";
        }

        if (statusCode == 401)
            return "쿠팡 인증에 실패했습니다. 설정 화면의 access_key / secret_key를 확인하세요. " +
                   "(서버 시각이 크게 어긋나도 서명 검증에 실패할 수 있습니다)";

        return null;
    }

    /// <summary>
    /// JSON 파싱 실패 시 원인을 알 수 있는 메시지를 만든다.
    /// 압축이 풀리지 않으면 gzip 매직바이트(0x1F) 때문에 파싱이 깨지는데,
    /// 기본 예외 메시지만으로는 원인을 알 수 없다.
    /// </summary>
    private static JsonDocument ParseJsonOrThrow(string raw, HttpResponseMessage response)
    {
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
            var hint = raw.Length > 0 && raw[0] == '\u001F'
                ? $" (응답이 압축({encoding ?? "gzip"})된 상태로 도착했습니다 — HttpClient의 AutomaticDecompression 설정을 확인하세요)"
                : $" (응답 앞부분: {raw[..Math.Min(raw.Length, 80)]})";
            throw new InvalidOperationException(
                $"쿠팡 응답을 JSON으로 해석할 수 없습니다: {ex.Message}{hint}");
        }
    }

    // ── 상품 등록 ───────────────────────────────────────────────────────

    public async Task<ListingResult> RegisterAsync(ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var vendorId = cred.Require("vendor_id");

            // 카테고리를 정하고 그 카테고리가 요구하는 필수 항목을 조회한다.
            // 위탁판매는 상품이 많아 사람이 카테고리를 고를 수 없으므로 쿠팡 추천을 쓴다.
            var categoryCode = await ResolveCategoryAsync(payload, cred, ct);
            if (string.IsNullOrWhiteSpace(categoryCode))
                return ListingResult.Fail("NO_CATEGORY",
                    "쿠팡 카테고리를 정하지 못했습니다. 상품명이 너무 짧거나 모호하면 추천이 실패합니다 — " +
                    "설정에서 default_category_code를 지정하거나 상품명을 보완하세요.");

            var meta = await GetCategoryMetaAsync(categoryCode, cred, ct);
            var body = BuildProductBody(payload, vendorId, cred, categoryCode, meta);

            var response = await SendAsync(HttpMethod.Post,
                "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products", "", body, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            // 쿠팡은 판매가에 따라 반품배송비 상한을 다르게 둔다. 공식이 공개돼 있지 않지만
            // 오류 메시지가 상한을 알려주므로, 그 값으로 한 번 다시 시도한다.
            // (공급처 반품비 6,000원 > 상한 5,000원 → 차액은 판매자 부담이 된다)
            if (ParseReturnChargeLimit(raw) is { } limit)
            {
                logger.LogWarning(
                    "쿠팡 반품배송비 상한 초과 — {Limit:N0}원으로 낮춰 재시도합니다. " +
                    "공급처 청구액과의 차액은 판매자 부담입니다. (상품: {Name})",
                    limit, Truncate(payload.Name, 30));

                body = BuildProductBody(payload, vendorId, cred, categoryCode, meta, limit);
                using var retry = await SendAsync(HttpMethod.Post,
                    "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products", "", body, cred, ct);
                raw = await retry.Content.ReadAsStringAsync(ct);
                response = retry;
            }

            // 정책성 오류(IP 미등록·인증 실패)는 JSON이 아닐 수 있으므로 파싱 전에 먼저 본다
            if (TranslateKnownError((int)response.StatusCode, raw) is { } guidance)
            {
                logger.LogWarning("쿠팡 등록 거부 [{Status}]: {Guidance}", (int)response.StatusCode, guidance);
                return ListingResult.Fail($"HTTP_{(int)response.StatusCode}", guidance, raw);
            }

            using var doc = ParseJsonOrThrow(raw, response);
            var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (response.IsSuccessStatusCode && code == "SUCCESS")
            {
                var sellerProductId = doc.RootElement.TryGetProperty("data", out var data) ? data.ToString() : "unknown";
                return ListingResult.Ok(sellerProductId, raw);
            }

            var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : raw;
            logger.LogWarning("쿠팡 등록 실패: {Message}", message);
            return ListingResult.Fail(code ?? $"HTTP_{(int)response.StatusCode}", message ?? "알 수 없는 오류", raw);
        }
        catch (PluginCredentialException ex)
        {
            return ListingResult.Fail("CREDENTIAL", ex.Message);
        }
        catch (Exception ex)
        {
            return ListingResult.Fail("ERROR", ex.Message);
        }
    }

    private static object BuildProductBody(
        ListingPayload payload, string vendorId, MarketCredential cred,
        string displayCategoryCode, CoupangCategoryMeta meta,
        int? returnChargeLimit = null)
    {
        var now = DateTimeOffset.Now;

        // 위탁판매 물류 — 공급처가 준 값이 우선, 없으면 판매자 기본값
        var logistics = payload.Logistics;

        // 쿠팡은 "초도반품배송비 + 반품배송비"의 합에 상한을 둔다(판매가에 연동).
        // 공급처 반품비는 이미 왕복 기준이므로 두 필드에 중복으로 넣으면 상한을 넘는다.
        // 무료배송으로 등록하므로 초도분은 판매자가 부담하고(0), 반품비만 청구한다.
        var supplierReturnFee = (int)(logistics.ReturnFee
            ?? (decimal.TryParse(cred.Get("return_fee"), out var fee) ? fee : 5000));
        // 쿠팡이 알려준 상한이 있으면 그에 맞춘다 (판매가에 따라 달라진다)
        var maxReturnCharge = returnChargeLimit ?? 10_000;
        var returnCharge = Math.Clamp(supplierReturnFee, 0, maxReturnCharge);
        const int initialReturnCharge = 0;

        // 반품 주소는 기본/상세로 나눠 보내야 한다 (상세가 비면 쿠팡이 거부한다)
        var returnAddressParts = logistics.ReturnAddress is { Length: > 0 } supplierReturn
            ? SplitAddress(supplierReturn)
            : (Address: cred.Get("return_address") ?? "",
               Detail: cred.Get("return_address_detail") is { Length: > 0 } d ? d : "-");

        // 쿠팡은 1,000원 미만 판매가를 옵션 단위로 거부한다
        // ("[옵션(단일옵션): 최소 설정 가격은 1000원 입니다.]").
        // 도매 원가가 낮은 상품에서 흔히 걸리므로 CoupangSalePrice로 하한까지 올려 보낸다.
        // 판매가가 바뀌는 것이므로 링크 미리보기에서 미리 경고한다.
        var items = (payload.Variants.Count > 0
            ? payload.Variants.Select(v => new
            {
                itemName = string.Join(" ", v.Options.Values.DefaultIfEmpty("단일옵션")),
                originalPrice = (long)(CoupangSalePrice(v.Price.Amount) * 1.2m / 10) * 10,
                salePrice = (long)CoupangSalePrice(v.Price.Amount),
                maximumBuyCount = v.Stock,
                maximumBuyForPerson = 0,
                maximumBuyForPersonPeriod = 1,
                // 공급처 평균 출고일 + 여유. 위탁판매는 우리가 발주한 뒤 공급처가
                // 출고하므로 공급처 평균 그대로 약속하면 지연 페널티를 받는다.
                outboundShippingTimeDay = logistics.OutboundShippingDays,
                unitCount = 1,
                adultOnly = "EVERYONE",
                taxType = "TAX",
                parallelImported = "NOT_PARALLEL_IMPORTED",
                // 국내 도매(도매꾹 등)와 해외 소싱은 통관 설정이 다르다
                overseasPurchased = logistics.IsOverseasPurchase
                    ? "OVERSEAS_PURCHASED"
                    : "NOT_OVERSEAS_PURCHASED",
                pccNeeded = logistics.IsOverseasPurchase,   // 개인통관고유부호는 해외구매대행만
                externalVendorSku = v.VariantId,
                emptyBarcode = true,
                emptyBarcodeReason = "구매대행 상품 — 바코드 없음",
                images = payload.ImageUrls.Take(1).Select((url, i) => new
                {
                    imageOrder = i,
                    imageType = "REPRESENTATION",
                    vendorPath = url,
                }).ToArray(),
                // 카테고리 필수 구매옵션을 반드시 채운다 (비면 등록 거부)
                attributes = BuildItemAttributes(meta, v.Options),
                // 검색어는 상품이 아니라 아이템 단위다 (쿠팡 가이드 v1.3 예시 기준).
                // 쿠팡 내부 검색 노출에 직접 쓰이므로 공급처 키워드를 그대로 싣는다.
                searchTags = BuildSearchTags(payload),
                contents = BuildContents(payload),
                // 카테고리별 상품고시정보 필수 항목 (누락 시 등록 거부)
                notices = BuildNotices(meta, payload),
            })
            : []).ToArray();

        return new
        {
            displayCategoryCode,
            sellerProductName = payload.Name.Length > 100 ? payload.Name[..100] : payload.Name,
            vendorId,
            saleStartedAt = now.ToString("yyyy-MM-dd'T'HH:mm:ss"),
            saleEndedAt = "2099-01-01T23:59:59",
            displayProductName = payload.Name.Length > 100 ? payload.Name[..100] : payload.Name,
            // 해외 소싱은 구매대행, 국내 위탁판매는 일반배송.
            // 국내 상품을 AGENT_BUY로 보내면 "배송방법을 확인해 주시기 바랍니다"로 거부된다.
            deliveryMethod = logistics.IsOverseasPurchase ? "AGENT_BUY" : "SEQUENCIAL",
            deliveryCompanyCode = cred.Get("delivery_company_code") ?? "CJGLS",
            deliveryChargeType = "FREE",
            deliveryCharge = 0,
            freeShipOverAmount = 0,
            deliveryChargeOnReturn = initialReturnCharge,
            remoteAreaDeliverable = "N",
            unionDeliveryType = "NOT_UNION_DELIVERY",
            // 위탁판매: 공급처 주소로 만든 코드를 우선 쓰고, 없을 때만 판매자 기본값으로 폴백한다
            // 사전 등록된 반품지 코드가 없으면 NO_RETURN_CENTERCODE를 넣는다.
            // 그러면 아래 returnZipCode/returnAddress/returnAddressDetail이 그대로 반품지가 된다
            // (쿠팡 상품등록 가이드 v1.3 §14) — 공급처마다 다른 반품지를 코드 없이 쓸 수 있는 정식 경로다.
            returnCenterCode = payload.ResolvedReturnCenterCode
                ?? cred.Get("return_center_code")
                ?? NoReturnCenterCode,
            returnChargeName = logistics.SupplierName is { Length: > 0 } supplier
                ? $"{supplier} 반품지"
                : cred.Get("return_charge_name") ?? "반품지",
            // 공급처가 번호를 여러 개 적어 두는 경우가 많아 정제된 값을 쓴다 (쿠팡 16자 상한)
            companyContactNumber = logistics.ContactNumber
                ?? cred.Get("contact_number") ?? "010-0000-0000",
            returnZipCode = logistics.ReturnZipcode ?? cred.Get("return_zip_code"),
            // 쿠팡은 상세주소를 1자 이상 요구한다. 공급처 주소를 기본/상세로 쪼개 둘 다 채운다.
            returnAddress = returnAddressParts.Address,
            returnAddressDetail = returnAddressParts.Detail,
            returnCharge,
            outboundShippingPlaceCode =
                payload.ResolvedOutboundPlaceCode ?? cred.Get("outbound_shipping_place_code"),
            vendorUserId = cred.Get("vendor_user_id") ?? vendorId,
            requested = false, // 등록 후 수동 판매요청 (안전)
            items,
        };
    }

    // ── 수정/삭제/가격재고/주문 ─────────────────────────────────────────

    public async Task<ListingResult> UpdateAsync(string marketItemId, ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var vendorId = cred.Require("vendor_id");
            var categoryCode = await ResolveCategoryAsync(payload, cred, ct);
            if (string.IsNullOrWhiteSpace(categoryCode))
                return ListingResult.Fail("NO_CATEGORY", "쿠팡 카테고리를 정하지 못했습니다.");

            var meta = await GetCategoryMetaAsync(categoryCode, cred, ct);
            var body = BuildProductBody(payload, vendorId, cred, categoryCode, meta);
            var response = await SendAsync(HttpMethod.Put,
                "/v2/providers/seller_api/apis/api/v1/marketplace/seller-products", "", body, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail($"HTTP_{(int)response.StatusCode}", raw, raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<ListingResult> DeleteAsync(string marketItemId, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var path = $"/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/{marketItemId}";
            var response = await SendAsync(HttpMethod.Delete, path, "", null, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail($"HTTP_{(int)response.StatusCode}", raw, raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<ListingResult> UpdatePriceStockAsync(string marketItemId, Money price, int stock, MarketCredential cred, CancellationToken ct)
    {
        // 쿠팡은 vendorItemId 단위 가격/재고 API를 제공. sellerProductId로 조회 후 각 아이템 갱신.
        try
        {
            var detailPath = $"/v2/providers/seller_api/apis/api/v1/marketplace/seller-products/{marketItemId}";
            var detailResponse = await SendAsync(HttpMethod.Get, detailPath, "", null, cred, ct);
            var detailRaw = await detailResponse.Content.ReadAsStringAsync(ct);
            if (!detailResponse.IsSuccessStatusCode)
                return ListingResult.Fail($"HTTP_{(int)detailResponse.StatusCode}", detailRaw, detailRaw);

            using var doc = JsonDocument.Parse(detailRaw);
            var vendorItemIds = new List<long>();
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("vendorItemId", out var vid) && vid.ValueKind == JsonValueKind.Number)
                        vendorItemIds.Add(vid.GetInt64());

            foreach (var vendorItemId in vendorItemIds)
            {
                await SendAsync(HttpMethod.Put,
                    $"/v2/providers/seller_api/apis/api/v1/marketplace/vendor-items/{vendorItemId}/prices/{(long)price.Amount}",
                    "", null, cred, ct);
                await SendAsync(HttpMethod.Put,
                    $"/v2/providers/seller_api/apis/api/v1/marketplace/vendor-items/{vendorItemId}/quantities/{stock}",
                    "", null, cred, ct);
            }
            return ListingResult.Ok(marketItemId);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<IReadOnlyList<MarketOrder>> FetchOrdersAsync(DateRange range, MarketCredential cred, CancellationToken ct)
    {
        var vendorId = cred.Require("vendor_id");
        var path = $"/v2/providers/openapi/apis/api/v4/vendors/{vendorId}/ordersheets";
        var query = $"createdAtFrom={range.From:yyyy-MM-dd}&createdAtTo={range.To:yyyy-MM-dd}&status=ACCEPT";
        var response = await SendAsync(HttpMethod.Get, path, query, null, cred, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"쿠팡 주문 조회 실패: {raw[..Math.Min(raw.Length, 300)]}");

        var orders = new List<MarketOrder>();
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var sheet in data.EnumerateArray())
            {
                var orderId = sheet.TryGetProperty("orderId", out var oid) ? oid.ToString() : "";
                var orderedAt = sheet.TryGetProperty("orderedAt", out var oat)
                    && DateTimeOffset.TryParse(oat.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
                var orderer = sheet.TryGetProperty("orderer", out var o)
                    && o.TryGetProperty("name", out var on) ? on.GetString() : null;

                // 위탁판매의 생명줄 — 이 주소가 곧 공급처 발주의 수령지다.
                // 이걸 안 받아오면 주문은 들어오는데 발주를 할 수 없다.
                var shipTo = ParseReceiver(sheet);

                // 송장 등록에 필요한 키. 주문 수집 때 챙기지 않으면 나중에 방법이 없다.
                var shipmentBoxId = sheet.TryGetProperty("shipmentBoxId", out var sb) ? sb.ToString() : null;

                if (sheet.TryGetProperty("orderItems", out var orderItems) && orderItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in orderItems.EnumerateArray())
                    {
                        orders.Add(new MarketOrder
                        {
                            MarketOrderId = orderId,
                            MarketCode = Code,
                            MarketItemId = item.TryGetProperty("sellerProductId", out var spi) ? spi.ToString() : null,
                            ProductName = item.TryGetProperty("sellerProductName", out var spn) ? spn.GetString() : null,
                            OptionName = item.TryGetProperty("sellerProductItemName", out var sin) ? sin.GetString() : null,
                            Quantity = item.TryGetProperty("shippingCount", out var sc) ? sc.GetInt32() : 1,
                            PaidAmount = Money.Krw(item.TryGetProperty("orderPrice", out var op) ? op.GetDecimal() : 0),
                            OrdererName = orderer,
                            OrderedAt = orderedAt,
                            Status = "ACCEPT",
                            ShipTo = shipTo,
                            ShipmentBoxId = shipmentBoxId,
                            VendorItemId = item.TryGetProperty("vendorItemId", out var vi) ? vi.ToString() : null,
                        });
                    }
                }
            }
        }
        return orders;
    }

    /// <summary>
    /// 주문서에서 구매자 배송지를 뽑는다.
    ///
    /// 쿠팡은 개인정보 보호로 수취인 연락처를 안심번호(safeNumber)로 준다.
    /// 실제 번호는 주지 않으므로 안심번호를 그대로 공급처에 넘겨야 한다 —
    /// 택배사가 이 번호로 연결해 준다.
    /// </summary>
    private static ShippingAddress? ParseReceiver(JsonElement sheet)
    {
        if (!sheet.TryGetProperty("receiver", out var r) || r.ValueKind != JsonValueKind.Object)
            return null;

        string? Text(string name) =>
            r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() is { Length: > 0 } s ? s : null
                : null;

        var address = new ShippingAddress
        {
            ReceiverName = Text("name"),
            // safeNumber(안심번호)가 우선. receiverNumber는 마스킹돼 오는 경우가 있다.
            Phone = Text("safeNumber") ?? Text("receiverNumber"),
            Zipcode = Text("postCode"),
            Address1 = Text("addr1"),
            Address2 = Text("addr2"),
            Message = sheet.TryGetProperty("parcelPrintMessage", out var m) ? m.GetString() : null,
        };

        // 이름과 주소가 모두 없으면 배송지로 쓸 수 없다
        return string.IsNullOrWhiteSpace(address.ReceiverName) && string.IsNullOrWhiteSpace(address.Address1)
            ? null
            : address;
    }

    [GeneratedRegex(@"\d{1,3}(\.\d{1,3}){3}")]
    private static partial Regex IpAddressRegex();

    /// <summary>"0원 ~ 5,000원 까지" 에서 상한(5,000)을 뽑는다.</summary>
    [GeneratedRegex(@"0원\s*~\s*([\d,]+)\s*원")]
    private static partial Regex ReturnChargeLimitRegex();
}

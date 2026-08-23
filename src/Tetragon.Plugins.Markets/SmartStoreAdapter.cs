using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 네이버 스마트스토어 어댑터 (실연동 — 커머스API).
/// 인증: OAuth2 client_credentials + bcrypt 전자서명 (client_secret_sign).
/// 필요 자격증명: client_id, client_secret (커머스API센터 애플리케이션에서 발급).
/// </summary>
public sealed class SmartStoreAdapter(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<SmartStoreAdapter> logger) : IMarketplaceAdapter
{
    public string Code => "smartstore";
    public string DisplayName => "네이버 스마트스토어";
    public string Version => "1.0.0";
    public bool IsLive => true;
    public bool IsAvailable =>
        credentials.HasKey("market:smartstore", "client_id")
        && credentials.HasKey("market:smartstore", "client_secret");

    private const string BaseUrl = "https://api.commerce.naver.com";
    private static (string Token, DateTimeOffset ExpiresAt)? _cachedToken;
    private static readonly SemaphoreSlim TokenLock = new(1);

    // ── 인증 ────────────────────────────────────────────────────────────

    private async Task<string> GetAccessTokenAsync(MarketCredential cred, CancellationToken ct)
    {
        await TokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is { } cached && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return cached.Token;

            var clientId = cred.Require("client_id");
            var clientSecret = cred.Require("client_secret");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 전자서명: bcrypt(client_id + "_" + timestamp, salt=client_secret) → base64
            var password = $"{clientId}_{timestamp}";
            var hashed = BCrypt.Net.BCrypt.HashPassword(password, clientSecret);
            var sign = Convert.ToBase64String(Encoding.UTF8.GetBytes(hashed));

            var client = httpClientFactory.CreateClient("smartstore");
            var response = await client.PostAsync(
                $"{BaseUrl}/external/v1/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["timestamp"] = timestamp.ToString(),
                    ["grant_type"] = "client_credentials",
                    ["client_secret_sign"] = sign,
                    ["type"] = "SELF",
                }), ct);

            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"스마트스토어 인증 실패 (HTTP {(int)response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 10800;
            _cachedToken = (token, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
            return token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? jsonBody, MarketCredential cred, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(cred, ct);
        var client = httpClientFactory.CreateClient("smartstore");
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (jsonBody is not null)
            request.Content = JsonContent.Create(jsonBody, options: new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
        return await client.SendAsync(request, ct);
    }

    // ── 상품 등록 (POST /external/v2/products) ──────────────────────────

    public async Task<ListingResult> RegisterAsync(ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var categoryId = payload.MarketCategoryCodes.GetValueOrDefault(Code)
                ?? cred.Get("default_category_id")
                ?? "50000803"; // 폴백: 여성의류>원피스 (설정에서 default_category_id로 변경 가능)

            var body = BuildProductBody(payload, categoryId, cred);
            var response = await SendAsync(HttpMethod.Post, "/external/v2/products", body, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("스마트스토어 등록 실패: {Status} {Body}", (int)response.StatusCode, raw);
                return ListingResult.Fail($"HTTP_{(int)response.StatusCode}", ExtractErrorMessage(raw), raw);
            }

            using var doc = JsonDocument.Parse(raw);
            var originProductNo = doc.RootElement.TryGetProperty("originProductNo", out var no)
                ? no.ToString() : "unknown";
            return ListingResult.Ok(originProductNo, raw);
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

    private static object BuildProductBody(ListingPayload payload, string categoryId, MarketCredential cred)
    {
        var mainImage = payload.ImageUrls.FirstOrDefault() ?? "";
        var optionalImages = payload.ImageUrls.Skip(1).Take(9)
            .Select(url => new { url }).ToArray();

        // 옵션 조합형 (그룹 최대 3개 — 스마트스토어 제약)
        object? optionInfo = null;
        if (payload.OptionGroups.Count > 0 && payload.Variants.Count > 0)
        {
            var groups = payload.OptionGroups.Take(3).ToList();
            optionInfo = new
            {
                optionCombinationSortType = "CREATE",
                optionCombinationGroupNames = new
                {
                    optionGroupName1 = groups.ElementAtOrDefault(0)?.Name,
                    optionGroupName2 = groups.ElementAtOrDefault(1)?.Name,
                    optionGroupName3 = groups.ElementAtOrDefault(2)?.Name,
                },
                optionCombinations = payload.Variants.Select(v => new
                {
                    optionName1 = OptionAt(v, groups, 0),
                    optionName2 = OptionAt(v, groups, 1),
                    optionName3 = OptionAt(v, groups, 2),
                    price = (int)(v.Price.Amount - payload.SalePrice.Amount), // 기준가 대비 추가금
                    stockQuantity = v.Stock,
                    usable = true,
                }).ToArray(),
            };
        }

        return new
        {
            originProduct = new
            {
                statusType = "SALE",
                saleType = "NEW",
                leafCategoryId = categoryId,
                name = Truncate(payload.Name, 100),
                detailContent = BuildDetailHtml(payload),
                images = new
                {
                    representativeImage = new { url = mainImage },
                    optionalImages,
                },
                salePrice = (int)payload.SalePrice.Amount,
                stockQuantity = payload.Stock,
                deliveryInfo = new
                {
                    deliveryType = "DELIVERY",
                    deliveryAttributeType = "NORMAL",
                    deliveryFee = new { deliveryFeeType = "FREE" },
                    claimDeliveryInfo = new { returnDeliveryFee = 5000, exchangeDeliveryFee = 10000 },
                },
                detailAttribute = new
                {
                    afterServiceInfo = new
                    {
                        afterServiceTelephoneNumber = cred.Get("as_telephone") ?? "010-0000-0000",
                        afterServiceGuideContent = "구매자 문의는 스토어 문의하기를 이용해주세요.",
                    },
                    originAreaInfo = new { originAreaCode = "0200037", importer = "직수입" }, // 수입산(중국)
                    minorPurchasable = true,
                    optionInfo,
                },
            },
            smartstoreChannelProduct = new
            {
                naverShoppingRegistration = true,
                channelProductDisplayStatusType = "ON",
            },
        };
    }

    private static string? OptionAt(PayloadVariant variant, List<PayloadOptionGroup> groups, int index)
    {
        var group = groups.ElementAtOrDefault(index);
        return group is null ? null : variant.Options.GetValueOrDefault(group.Name);
    }

    private static string BuildDetailHtml(ListingPayload payload)
    {
        var sb = new StringBuilder();
        sb.Append("<div style=\"text-align:center\">");
        sb.Append($"<p>{System.Net.WebUtility.HtmlEncode(payload.Description ?? payload.Name)}</p>");
        foreach (var img in payload.ImageUrls.Take(10))
            sb.Append($"<img src=\"{img}\" style=\"max-width:100%\" alt=\"\">");
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── 수정/삭제/가격재고/주문 ─────────────────────────────────────────

    public async Task<ListingResult> UpdateAsync(string marketItemId, ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var categoryId = payload.MarketCategoryCodes.GetValueOrDefault(Code) ?? cred.Get("default_category_id") ?? "50000803";
            var response = await SendAsync(HttpMethod.Put,
                $"/external/v2/products/origin-products/{marketItemId}", BuildProductBody(payload, categoryId, cred), cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail($"HTTP_{(int)response.StatusCode}", ExtractErrorMessage(raw), raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<ListingResult> DeleteAsync(string marketItemId, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var response = await SendAsync(HttpMethod.Delete,
                $"/external/v2/products/origin-products/{marketItemId}", null, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail($"HTTP_{(int)response.StatusCode}", ExtractErrorMessage(raw), raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<ListingResult> UpdatePriceStockAsync(string marketItemId, Money price, int stock, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            // 옵션 없는 단순 상품 기준 부분 수정
            var body = new { originProduct = new { salePrice = (int)price.Amount, stockQuantity = stock } };
            var response = await SendAsync(HttpMethod.Put,
                $"/external/v2/products/origin-products/{marketItemId}", body, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode
                ? ListingResult.Ok(marketItemId, raw)
                : ListingResult.Fail($"HTTP_{(int)response.StatusCode}", ExtractErrorMessage(raw), raw);
        }
        catch (Exception ex) { return ListingResult.Fail("ERROR", ex.Message); }
    }

    public async Task<IReadOnlyList<MarketOrder>> FetchOrdersAsync(DateRange range, MarketCredential cred, CancellationToken ct)
    {
        var from = Uri.EscapeDataString(range.From.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"));
        var response = await SendAsync(HttpMethod.Get,
            $"/external/v1/pay-order/seller/product-orders/last-changed-statuses?lastChangedFrom={from}", null, cred, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"스마트스토어 주문 조회 실패: {ExtractErrorMessage(raw)}");

        var orders = new List<MarketOrder>();
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("lastChangeStatuses", out var statuses)
            && statuses.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in statuses.EnumerateArray())
            {
                orders.Add(new MarketOrder
                {
                    MarketOrderId = item.TryGetProperty("productOrderId", out var poi) ? poi.GetString() ?? "" : "",
                    MarketCode = Code,
                    ProductName = item.TryGetProperty("productName", out var pn) ? pn.GetString() : null,
                    Quantity = item.TryGetProperty("quantity", out var q) ? q.GetInt32() : 1,
                    PaidAmount = Money.Krw(item.TryGetProperty("totalPaymentAmount", out var amt) ? amt.GetDecimal() : 0),
                    OrderedAt = item.TryGetProperty("paymentDate", out var pd)
                        && DateTimeOffset.TryParse(pd.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow,
                    Status = item.TryGetProperty("productOrderStatus", out var st) ? st.GetString() ?? "PAYED" : "PAYED",
                });
            }
        }
        return orders;
    }

    private static string ExtractErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("message", out var msg)) return msg.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("invalidInputs", out var inputs))
                return string.Join("; ", inputs.EnumerateArray().Select(i =>
                    $"{i.GetProperty("name").GetString()}: {i.GetProperty("message").GetString()}"));
        }
        catch (JsonException) { }
        return raw.Length > 300 ? raw[..300] : raw;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

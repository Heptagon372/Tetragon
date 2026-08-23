using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Plugins.Suppliers;

/// <summary>
/// 도매꾹 자동 발주.
///
/// <b>실측 결과 Open API 키로는 발주할 수 없습니다.</b>
/// 주문 관련 mode(setOrder / addOrder / getOrderList …)를 모든 버전으로 호출해도
/// 전부 403 "요청한 API에 대한 호출 권한이 없습니다"가 돌아온다.
/// 도매꾹은 상품 조회(Open)와 주문(Private) 스코프를 분리해 두었고,
/// 주문 스코프는 별도 신청·승인이 필요하다.
///
/// 그래서 이 플러그인은 두 가지를 한다:
///   1. 권한이 있으면 실제로 발주한다 (승인받은 뒤엔 코드 수정 없이 동작)
///   2. 권한이 없으면 그 사실을 정확히 알리고, 수동 발주로 넘긴다
///
/// 권한 없이 조용히 실패하면 주문이 지연되므로, 사유와 해결 방법을 함께 반환한다.
/// </summary>
public sealed class DomeggookOrderPlugin(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<DomeggookOrderPlugin> logger) : ISupplierOrderPlugin
{
    public string SupplierCode => "domeggook";

    private const string ApiBase = "https://domeggook.com/ssl/api/";
    private const string CredentialScope = "supplier:domeggook";

    /// <summary>권한 확인은 매 발주마다 하지 않고 캐시한다 (자격증명이 바뀌면 무효화된다).</summary>
    private SupplierOrderCapability? _cached;
    private string? _cachedForKey;

    public async Task<SupplierOrderCapability> CheckCapabilityAsync(CancellationToken ct)
    {
        var apiKey = credentials.Get(CredentialScope, "api_key");
        if (string.IsNullOrWhiteSpace(apiKey))
            return SupplierOrderCapability.Unavailable(
                "도매꾹 API 키가 없습니다.",
                "설정 → 공급처 → domeggook 에 api_key를 등록하세요.");

        if (_cached is not null && _cachedForKey == apiKey) return _cached;

        // 주문 조회 mode를 찔러 권한을 확인한다 (주문이 생성되지 않는 안전한 호출)
        try
        {
            var client = httpClientFactory.CreateClient("scraper");
            var url = $"{ApiBase}?ver=1.0&mode=getOrderList&aid={Uri.EscapeDataString(apiKey)}&om=json";
            using var response = await client.GetAsync(url, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            var code = ReadErrorCode(raw);
            _cachedForKey = apiKey;
            _cached = code switch
            {
                "403" => SupplierOrderCapability.Unavailable(
                    "이 API 키에는 주문 권한이 없습니다 (도매꾹 Open API는 상품 조회만 가능).",
                    "openapi.domeggook.com → API 키 관리에서 주문(Private) 스코프를 신청하세요. " +
                    "승인 전까지는 '공급사 사이트에서 주문하기'로 수동 발주할 수 있습니다."),
                "401" => SupplierOrderCapability.Unavailable(
                    "도매꾹 API 인증에 실패했습니다.",
                    "설정에서 api_key를 다시 확인하세요."),
                _ => SupplierOrderCapability.Available(),
            };
            return _cached;
        }
        catch (Exception ex)
        {
            logger.LogWarning("도매꾹 발주 권한 확인 실패: {Error}", ex.Message);
            return SupplierOrderCapability.Unavailable(
                $"도매꾹 연결에 실패했습니다: {ex.Message}", "네트워크 상태를 확인하세요.");
        }
    }

    public async Task<SupplierOrderResult> PlaceOrderAsync(SupplierOrderRequest request, CancellationToken ct)
    {
        var capability = await CheckCapabilityAsync(ct);
        if (!capability.CanAutoOrder)
            return SupplierOrderResult.Fail("NO_PERMISSION",
                $"{capability.Reason} {capability.HowToEnable}".Trim());

        var apiKey = credentials.Get(CredentialScope, "api_key")!;

        // 주문 스코프가 승인되면 이 경로를 탄다.
        // 파라미터명은 승인 시 제공되는 문서에 맞춰 조정해야 한다.
        var form = new Dictionary<string, string>
        {
            ["ver"] = "1.0",
            ["mode"] = "setOrder",
            ["aid"] = apiKey,
            ["om"] = "json",
            ["no"] = request.SourceProductId,
            ["qty"] = request.Quantity.ToString(),
            ["receiverName"] = request.ReceiverName,
            ["receiverTel"] = request.ReceiverPhone,
            ["receiverZipcode"] = request.ReceiverZipcode,
            ["receiverAddr"] = request.ReceiverAddress,
            ["deliveryMemo"] = request.DeliveryMessage ?? "",
            // 우리 주문 ID를 실어 중복 발주를 막는다
            ["clientOrderNo"] = request.OrderId.ToString("N"),
        };
        if (!string.IsNullOrWhiteSpace(request.OptionId)) form["optNo"] = request.OptionId;

        try
        {
            var client = httpClientFactory.CreateClient("scraper");
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(ApiBase, content, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (ReadErrorCode(raw) is { } errorCode)
                return SupplierOrderResult.Fail(errorCode, ReadErrorMessage(raw) ?? "도매꾹 발주 실패", raw);

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement.TryGetProperty("domeggook", out var wrapper) ? wrapper : doc.RootElement;
            var orderNo = root.TryGetProperty("orderNo", out var no) ? no.ToString() : null;
            var paid = root.TryGetProperty("totalPrice", out var price)
                && decimal.TryParse(price.ToString(), out var amount) ? amount : (decimal?)null;

            if (string.IsNullOrWhiteSpace(orderNo))
                return SupplierOrderResult.Fail("NO_ORDER_NO",
                    "도매꾹이 주문번호를 반환하지 않았습니다. 도매꾹에서 주문이 실제로 생성됐는지 확인하세요.", raw);

            logger.LogInformation("도매꾹 발주 완료: 주문번호 {OrderNo}, 결제 {Paid}원", orderNo, paid);
            return SupplierOrderResult.Ok(orderNo, paid, raw);
        }
        catch (Exception ex)
        {
            // 발주는 돈이 걸린 호출이라, 실패를 성공으로 오인하면 안 된다
            logger.LogError(ex, "도매꾹 발주 중 오류 (Order {OrderId})", request.OrderId);
            return SupplierOrderResult.Fail("ERROR",
                $"발주 중 오류가 발생했습니다: {ex.Message}. " +
                "도매꾹에서 주문이 생성됐는지 직접 확인한 뒤 재시도하세요.");
        }
    }

    private static string? ReadErrorCode(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("errors", out var errors)
                   && errors.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("errors", out var errors)) return null;
            return (errors.TryGetProperty("dmessage", out var d) ? d.GetString() : null)
                   ?? (errors.TryGetProperty("message", out var m) ? m.GetString() : null);
        }
        catch (JsonException) { return null; }
    }
}

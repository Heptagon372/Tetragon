using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 쿠팡 출고지/반품지 자동 등록 (위탁판매 필수).
///
/// 쿠팡은 상품 등록 시 주소 문자열이 아니라 사전 등록된
/// outboundShippingPlaceCode / returnCenterCode를 요구한다.
/// 위탁판매는 상품마다 공급처가 달라 출고지도 달라지므로, 공급처 주소를 쿠팡에 등록하고 코드를 받아 쓴다.
///
/// 전략은 3단계다 (실측으로 확정한 엔드포인트/스키마 기준):
///   1. 재사용 — 이미 같은 이름/주소로 등록된 배송지가 있으면 그 코드를 쓴다
///   2. 생성   — 없으면 v4 API로 새로 만든다
///   3. 폴백   — 생성이 실패해도 판매자의 기존 배송지로 등록은 진행시킨다
///
/// 3단계가 중요하다. 배송지 등록에 실패했다고 상품 등록 자체를 막으면
/// 판매 기회를 잃는다. 반품지가 판매자 주소가 되는 것은 나중에 고칠 수 있다.
/// </summary>
public sealed partial class CoupangAdapter : IShippingPlaceProvider
{
    // 실측 확인한 경로 —
    //  조회: marketplace_openapi (GET 전용), 생성: openapi v4
    private const string OutboundListPath =
        "/v2/providers/marketplace_openapi/apis/api/v1/vendor/shipping-place/outbound";
    private static string OutboundCreatePath(string vendorId) =>
        $"/v2/providers/openapi/apis/api/v4/vendors/{vendorId}/outboundShippingCenters";
    private static string ReturnCenterPath(string vendorId) =>
        $"/v2/providers/openapi/apis/api/v4/vendors/{vendorId}/returnShippingCenters";

    public async Task<ShippingPlaceResult> EnsureOutboundPlaceAsync(
        ConsignmentLogistics logistics, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var vendorId = cred.Require("vendor_id");
            var placeName = BuildPlaceName("출고", logistics);

            // 출고지 주소와 우편번호는 반드시 같은 곳에서 나온 짝이어야 한다.
            // 사업장 주소에 반품지 우편번호를 붙이면 주소록에 실재하지 않는 출고지가 만들어진다.
            var (outboundAddress, outboundZipcode) = logistics.OutboundPlace;
            if (logistics.OutboundFellBackToReturnAddress)
                logger.LogInformation(
                    "출고지 우편번호가 없어 반품지 주소를 출고지로 씁니다 ({Supplier}): {Address}",
                    logistics.SupplierName ?? "공급처", outboundAddress);

            var places = await FetchOutboundPlacesAsync(cred, ct);

            // 1. 이름 또는 주소가 일치하는 기존 출고지 재사용
            if (FindMatch(places, placeName, outboundAddress, outboundZipcode) is { } existing)
            {
                logger.LogInformation("쿠팡 출고지 재사용: {Name} → {Code}", existing.Name, existing.Code);
                return ShippingPlaceResult.Ok(existing.Code);
            }

            // 2. 신규 생성 — 우편번호가 유효해야 한다.
            // 공급처가 구 우편번호(626-812)를 그대로 둔 경우가 있는데 마켓이 받지 않는다.
            if (logistics.HasValidOutboundPlace)
            {
                var created = await CreateOutboundPlaceAsync(
                    vendorId, placeName, outboundAddress!, outboundZipcode!, logistics, cred, ct);
                if (created.Success)
                {
                    InvalidatePlaceCache(outbound: true);
                    return created;
                }
                logger.LogWarning("쿠팡 출고지 생성 실패, 기본 출고지로 폴백합니다: {Error}", created.Error);
            }

            // 3. 폴백 — 판매자의 기존 출고지 아무거나
            return FallbackOr(places, cred.Get("outbound_shipping_place_code"), "출고지", logistics);
        }
        catch (PluginCredentialException ex) { return ShippingPlaceResult.Fail(ex.Message); }
        catch (Exception ex) { return ShippingPlaceResult.Fail($"출고지 처리 오류: {ex.Message}"); }
    }

    public async Task<ShippingPlaceResult> EnsureReturnCenterAsync(
        ConsignmentLogistics logistics, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var vendorId = cred.Require("vendor_id");
            var placeName = BuildPlaceName("반품", logistics);

            var centers = await FetchReturnCentersAsync(vendorId, cred, ct);

            if (FindMatch(centers, placeName, logistics.ReturnAddress, logistics.ReturnZipcode) is { } existing)
            {
                logger.LogInformation("쿠팡 반품지 재사용: {Name} → {Code}", existing.Name, existing.Code);
                return ShippingPlaceResult.Ok(existing.Code);
            }

            if (!string.IsNullOrWhiteSpace(logistics.ReturnAddress))
            {
                var created = await CreateReturnCenterAsync(vendorId, placeName, logistics, cred, ct);
                if (created.Success)
                {
                    InvalidatePlaceCache(outbound: false);
                    return created;
                }
                logger.LogWarning("쿠팡 반품지 생성 실패, 기본 반품지로 폴백합니다: {Error}", created.Error);
            }

            return FallbackOr(centers, cred.Get("return_center_code"), "반품지", logistics);
        }
        catch (PluginCredentialException ex) { return ShippingPlaceResult.Fail(ex.Message); }
        catch (Exception ex) { return ShippingPlaceResult.Fail($"반품지 처리 오류: {ex.Message}"); }
    }

    // ── 조회 ─────────────────────────────────────────────────────────────

    private sealed record ShippingPlace(string Code, string Name, string Address, string Zipcode);

    /// <summary>
    /// 배송지 목록 캐시.
    ///
    /// 대량등록에서 상품마다 50곳을 다시 받으면 호출이 폭증해 스로틀링에 걸린다.
    /// 목록은 우리가 만들 때만 바뀌므로 짧게 캐시하고, 새로 만들면 그 자리에서 무효화한다.
    /// </summary>
    private static readonly SemaphoreSlim PlaceCacheGate = new(1, 1);
    private static List<ShippingPlace>? _outboundCache;
    private static List<ShippingPlace>? _returnCache;
    private static DateTimeOffset _outboundCachedAt = DateTimeOffset.MinValue;
    private static DateTimeOffset _returnCachedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan PlaceCacheTtl = TimeSpan.FromMinutes(3);

    private static void InvalidatePlaceCache(bool outbound)
    {
        if (outbound) _outboundCachedAt = DateTimeOffset.MinValue;
        else _returnCachedAt = DateTimeOffset.MinValue;
    }

    private async Task<List<ShippingPlace>> FetchOutboundPlacesAsync(MarketCredential cred, CancellationToken ct)
    {
        await PlaceCacheGate.WaitAsync(ct);
        try
        {
            if (_outboundCache is not null && DateTimeOffset.UtcNow - _outboundCachedAt < PlaceCacheTtl)
                return _outboundCache;

            var response = await SendAsync(HttpMethod.Get, OutboundListPath, "pageNum=1&pageSize=50", null, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return _outboundCache ?? [];

            // 응답: { content: [ { outboundShippingPlaceCode: 24921614(숫자), shippingPlaceName, placeAddresses: [...] } ] }
            _outboundCache = ParsePlaces(raw, "content", "outboundShippingPlaceCode");
            _outboundCachedAt = DateTimeOffset.UtcNow;
            return _outboundCache;
        }
        finally { PlaceCacheGate.Release(); }
    }

    private async Task<List<ShippingPlace>> FetchReturnCentersAsync(
        string vendorId, MarketCredential cred, CancellationToken ct)
    {
        await PlaceCacheGate.WaitAsync(ct);
        try
        {
            if (_returnCache is not null && DateTimeOffset.UtcNow - _returnCachedAt < PlaceCacheTtl)
                return _returnCache;

            var response = await SendAsync(HttpMethod.Get, ReturnCenterPath(vendorId), "pageNum=1&pageSize=50", null, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return _returnCache ?? [];

            // 응답: { data: { content: [ { returnCenterCode: "1002640504"(문자열), shippingPlaceName, placeAddresses } ] } }
            _returnCache = ParsePlaces(raw, "data.content", "returnCenterCode");
            _returnCachedAt = DateTimeOffset.UtcNow;
            return _returnCache;
        }
        finally { PlaceCacheGate.Release(); }
    }

    private List<ShippingPlace> ParsePlaces(string raw, string contentPath, string codeField)
    {
        var places = new List<ShippingPlace>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var node = doc.RootElement;
            foreach (var segment in contentPath.Split('.'))
            {
                if (!node.TryGetProperty(segment, out node)) return places;
            }
            if (node.ValueKind != JsonValueKind.Array) return places;

            foreach (var item in node.EnumerateArray())
            {
                // 코드가 숫자(출고지)일 수도 문자열(반품지)일 수도 있다
                if (!item.TryGetProperty(codeField, out var codeNode)) continue;
                var code = codeNode.ValueKind switch
                {
                    JsonValueKind.String => codeNode.GetString(),
                    JsonValueKind.Number => codeNode.ToString(),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(code)) continue;

                var name = item.TryGetProperty("shippingPlaceName", out var n) ? n.GetString() ?? "" : "";
                var address = "";
                var zipcode = "";
                if (item.TryGetProperty("placeAddresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
                {
                    var first = addrs.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Object)
                    {
                        if (first.TryGetProperty("returnAddress", out var a)) address = a.GetString() ?? "";
                        // 우편번호가 가장 신뢰할 수 있는 매칭 키다
                        if (first.TryGetProperty("returnZipCode", out var z)) zipcode = z.GetString() ?? "";
                    }
                }
                places.Add(new ShippingPlace(code, name, address, zipcode));
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning("쿠팡 배송지 목록 파싱 실패: {Error}", ex.Message);
        }
        return places;
    }

    /// <summary>
    /// 공급처 주소에 해당하는 배송지를 찾는다.
    ///
    /// 매칭 순서 (신뢰도 높은 것부터):
    ///   1. 우편번호 — 같은 우편번호면 같은 건물이다. 가장 확실하다.
    ///   2. 도로명+번지 — 행정구역 표기를 정규화한 뒤 비교
    ///   3. 이름 — 우리가 만든 이름 규칙으로 등록해 둔 경우
    ///
    /// 주소 문자열만 비교하면 실패한다. 쿠팡에 등록된 주소는 "인천광역시 계양구…"인데
    /// 공급처가 주는 주소는 "인천 계양구…"라 앞부분이 어긋나기 때문이다.
    /// </summary>
    private static ShippingPlace? FindMatch(
        List<ShippingPlace> places, string placeName, string? address, string? zipcode)
    {
        // 1. 우편번호 + 주소 동시 확인.
        //
        // 우편번호만으로 판단하면 안 된다. 한 우편번호에 여러 건물이 들어가고,
        // 공급처가 우편번호를 잘못 적어 두는 경우도 있다.
        // 실제로 우편번호만 보고 매칭했더니 서로 다른 공급처 4곳이
        // 같은 반품지(경남 양산)로 잡혔다 — 반품이 엉뚱한 곳으로 갈 뻔했다.
        if (!string.IsNullOrWhiteSpace(zipcode) && !string.IsNullOrWhiteSpace(address))
        {
            var digits = OnlyDigits(zipcode);
            if (digits.Length >= 5)
            {
                var normalized = NormalizeAddress(address);
                var byZip = places
                    .Where(p => OnlyDigits(p.Zipcode) == digits)
                    .Select(p => (Place: p, Score: CommonPrefixLength(NormalizeAddress(p.Address), normalized)))
                    .Where(x => x.Score >= MinAddressPrefixMatch)
                    .OrderByDescending(x => x.Score)
                    .ToList();
                if (byZip.Count > 0) return byZip[0].Place;
            }
        }

        // 2. 주소만으로 (우편번호가 없거나 어긋나는 경우)
        if (!string.IsNullOrWhiteSpace(address))
        {
            var normalized = NormalizeAddress(address);
            var byAddress = places
                .Select(p => (Place: p, Score: CommonPrefixLength(NormalizeAddress(p.Address), normalized)))
                .Where(x => x.Score >= MinAddressPrefixMatch)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (byAddress.Place is not null) return byAddress.Place;
        }

        // 3. 이름
        return places.FirstOrDefault(p =>
            p.Name.Trim().Equals(placeName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // 주소 정규화·비교 규칙은 KoreanAddress 한 곳에서 관리한다 (출고지·반품지가 같은 규칙을 써야 한다)
    private static string NormalizeAddress(string address) => KoreanAddress.Normalize(address);
    private static int CommonPrefixLength(string a, string b) => KoreanAddress.CommonPrefixLength(a, b);
    private static string OnlyDigits(string? s) => KoreanAddress.OnlyDigits(s);
    private const int MinAddressPrefixMatch = KoreanAddress.MinPrefixMatch;

    /// <summary>
    /// 사전 등록된 반품지 코드가 없을 때 쓰는 값.
    ///
    /// 쿠팡 상품등록 가이드(v1.3) 14번:
    /// "만약 반품지 생성이 불가능하다면, 'NO_RETURN_CENTERCODE'를 입력하여 직접 반품지를 등록합니다."
    ///
    /// 이 값을 넣으면 returnZipCode / returnAddress / returnAddressDetail 이 그대로 쓰인다.
    /// 반품지 생성 API가 막힌 계정(굿스플로 미계약)에서도 공급처 주소로 반품을 받을 수 있는 정식 경로다.
    /// </summary>
    public const string NoReturnCenterCode = "NO_RETURN_CENTERCODE";

    /// <summary>
    /// 공급처 배송지를 못 찾았을 때의 폴백.
    ///
    /// <b>목록의 아무 배송지나 고르지 않는다.</b> 그렇게 하면 A공급처 상품의 반품이
    /// B공급처 주소로 가버린다(실제로 겪은 문제 — 인천 공급처 상품에 경남 반품지가 붙었다).
    ///
    /// 반품지는 코드 대신 NO_RETURN_CENTERCODE + 공급처 주소를 쓴다.
    /// 출고지는 대체 수단이 없으므로 설정된 기본값을 쓰고, 그것도 없으면 실패한다.
    /// </summary>
    private ShippingPlaceResult FallbackOr(
        List<ShippingPlace> places, string? configured, string label, ConsignmentLogistics logistics)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            logger.LogInformation("쿠팡 {Label} 폴백: 설정된 기본값 {Code} 사용", label, configured);
            return ShippingPlaceResult.Ok(configured);
        }

        var supplier = logistics.SupplierName ?? "공급처";

        // 반품지: 코드 없이 주소를 직접 실어 보낸다 (공식 경로)
        if (label == "반품지" && !string.IsNullOrWhiteSpace(logistics.ReturnAddress))
        {
            logger.LogInformation(
                "쿠팡 반품지 코드 없음 ({Supplier}) — NO_RETURN_CENTERCODE로 공급처 주소를 직접 등록합니다.",
                supplier);
            return ShippingPlaceResult.Ok(NoReturnCenterCode);
        }

        // 왜 못 만들었는지 구체적으로 알려준다 — 공급처 데이터 자체가 부실한 경우가 많다
        var (outboundAddress, outboundZipcode) = logistics.OutboundPlace;
        var reason = string.IsNullOrWhiteSpace(outboundAddress)
            ? "공급처가 출고지·반품지 주소를 모두 제공하지 않았습니다."
            : !KoreanAddress.IsValidZipcode(outboundZipcode)
                ? $"출고지 우편번호를 확보하지 못했습니다 ({outboundZipcode ?? "없음"}) — " +
                  "공급처가 출고지 우편번호를 주지 않고 반품지 주소와도 달라, " +
                  "주소에 맞는 우편번호를 알 수 없습니다. 마켓은 5자리 신 우편번호만 받습니다."
                : $"등록된 {places.Count}곳 중 일치하는 주소가 없고 신규 생성도 실패했습니다.";

        logger.LogWarning("쿠팡 {Label} 확보 실패 ({Supplier}): {Reason}", label, supplier, reason);

        return ShippingPlaceResult.Fail(
            $"{supplier}의 {label}를 확보하지 못했습니다. {reason} " +
            $"설정 → 쿠팡 WING의 outbound_shipping_place_code에 기본 출고지 코드를 넣으면 " +
            $"이런 공급처도 등록할 수 있습니다.");
    }

    // ── 생성 ─────────────────────────────────────────────────────────────

    private async Task<ShippingPlaceResult> CreateOutboundPlaceAsync(
        string vendorId, string placeName, string outboundAddress, string outboundZipcode,
        ConsignmentLogistics logistics, MarketCredential cred, CancellationToken ct)
    {
        var (address, detail) = SplitAddress(outboundAddress);
        var phone = logistics.ContactNumber ?? cred.Get("contact_number");
        if (string.IsNullOrWhiteSpace(phone))
            return ShippingPlaceResult.Fail("출고지 연락처가 없습니다 (설정의 contact_number로도 대체 불가).");

        var body = new
        {
            vendorId,
            userId = cred.Get("vendor_user_id") ?? vendorId,
            shippingPlaceName = placeName,
            globalAddress = false,
            usable = true,
            placeAddresses = new[]
            {
                new
                {
                    addressType = "JIBUN",   // 단일 주소는 JIBUN만 허용된다 (실측)
                    countryCode = "KR",
                    companyContactNumber = phone,
                    phoneNumber2 = "",
                    // 이 우편번호는 위 address와 같은 곳의 것이어야 한다 (OutboundPlace가 보장)
                    returnZipCode = outboundZipcode,
                    returnAddress = address,
                    returnAddressDetail = detail,
                },
            },
            // remoteInfos를 넣으면 "배송비는 0원, 1000원 이상 20000원 이하만 가능합니다"로
            // 거부되는 경우가 있어 생성 시에는 넣지 않는다 (실측).
            // 제주·도서산간 요금은 WING에서 출고지별로 설정한다.
            remoteInfos = Array.Empty<object>(),
        };

        var response = await SendAsync(HttpMethod.Post, OutboundCreatePath(vendorId), "", body, cred, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        return ReadCreatedCode(raw, response.IsSuccessStatusCode, "출고지");
    }

    private async Task<ShippingPlaceResult> CreateReturnCenterAsync(
        string vendorId, string placeName, ConsignmentLogistics logistics,
        MarketCredential cred, CancellationToken ct)
    {
        var (address, detail) = SplitAddress(logistics.ReturnAddress!);
        var phone = logistics.ContactNumber ?? cred.Get("contact_number");
        if (string.IsNullOrWhiteSpace(phone))
            return ShippingPlaceResult.Fail("반품지 연락처가 없습니다.");

        // 공급처가 청구하는 반품비를 그대로 반영한다
        var returnFee = (int)(logistics.ReturnFee
            ?? (decimal.TryParse(cred.Get("return_fee"), out var f) ? f : 5000));

        // 반품비는 무게 구간별 평면 필드로 보낸다 (기존 반품지 조회 결과와 동일한 형태)
        var body = new Dictionary<string, object?>
        {
            ["vendorId"] = vendorId,
            ["userId"] = cred.Get("vendor_user_id") ?? vendorId,
            ["shippingPlaceName"] = placeName,
            ["usable"] = true,
            ["deliverCode"] = cred.Get("delivery_company_code") ?? "CJGLS",
            ["placeAddresses"] = new[]
            {
                new
                {
                    addressType = "JIBUN",
                    countryCode = "KR",
                    companyContactNumber = phone,
                    phoneNumber2 = "",
                    returnZipCode = logistics.ReturnZipcode ?? "00000",
                    returnAddress = address,
                    returnAddressDetail = detail,
                },
            },
        };
        foreach (var prefix in new[] { "vendorCreditFee", "vendorCashFee", "consumerCashFee", "returnFee" })
            foreach (var weight in new[] { "02kg", "05kg", "10kg", "20kg" })
                body[prefix + weight] = returnFee;

        var response = await SendAsync(HttpMethod.Post, ReturnCenterPath(vendorId), "", body, cred, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        var result = ReadCreatedCode(raw, response.IsSuccessStatusCode, "반품지");

        // 굿스플로(반품 회수 서비스) 계약이 없으면 API로는 반품지를 만들 수 없다.
        // 이건 스키마 문제가 아니라 계정 조건이라 재시도해도 소용없다 — 사람이 할 일을 알려준다.
        if (!result.Success && raw.Contains("goods flow", StringComparison.OrdinalIgnoreCase))
            return ShippingPlaceResult.Fail(
                $"쿠팡이 반품지 자동 생성을 거부했습니다(굿스플로 정보 필요). " +
                $"WING → 판매자정보 → 반품지 관리에서 '{placeName}' 주소를 한 번 등록해 두면 " +
                "이후 자동으로 매칭해 사용합니다. 지금은 기존 반품지로 등록을 진행합니다.");

        return result;
    }

    /// <summary>생성 응답에서 코드를 읽는다. 실패 시 사람이 읽을 수 있는 사유를 만든다.</summary>
    private ShippingPlaceResult ReadCreatedCode(string raw, bool isSuccess, string label)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException)
        {
            return ShippingPlaceResult.Fail($"{label} 등록 응답을 해석할 수 없습니다: {Truncate(raw, 120)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            if (!isSuccess)
                return ShippingPlaceResult.Fail($"{label} 등록 실패: {message ?? Truncate(raw, 150)}");

            // 응답 형태가 여러 가지다: data가 코드 문자열이거나, resultCode/{코드필드}를 가진 객체
            if (root.TryGetProperty("data", out var data))
            {
                var code = data.ValueKind switch
                {
                    JsonValueKind.String => data.GetString(),
                    JsonValueKind.Number => data.ToString(),
                    JsonValueKind.Object => FirstNonEmpty(data,
                        "outboundShippingPlaceCode", "returnCenterCode", "shippingPlaceCode", "resultMessage"),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(code) && code != "SUCCESS")
                {
                    logger.LogInformation("쿠팡 {Label} 신규 등록 완료: {Code}", label, code);
                    return ShippingPlaceResult.Ok(code);
                }
            }

            return ShippingPlaceResult.Fail(
                $"{label} 등록 응답에 코드가 없습니다: {Truncate(raw, 150)}");
        }

        static string? FirstNonEmpty(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var value)) continue;
                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.ToString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            return null;
        }
    }

    // ── 공통 ─────────────────────────────────────────────────────────────

    /// <summary>어느 공급처의 주소인지 쿠팡 화면에서 알아볼 수 있게 이름을 만든다.</summary>
    private static string BuildPlaceName(string kind, ConsignmentLogistics logistics)
    {
        var supplier = string.IsNullOrWhiteSpace(logistics.SupplierName) ? "공급처" : logistics.SupplierName;
        var name = $"[위탁] {supplier} {kind}지";
        return name.Length > 50 ? name[..50] : name;
    }

    /// <summary>
    /// "인천 서구 백범로910번길 4-10 (가좌동) 1층 KLAND" → ("인천 서구 백범로910번길 4-10 (가좌동)", "1층 KLAND")
    ///
    /// 쿠팡은 상세주소를 1자 이상 요구하므로 절대 빈 문자열을 반환하지 않는다.
    /// </summary>
    internal static (string Address, string Detail) SplitAddress(string full)
    {
        var trimmed = full.Trim();

        // 괄호로 끝나는 법정동 표기 뒤를 상세주소로 본다
        var closing = trimmed.LastIndexOf(')');
        if (closing > 0 && closing < trimmed.Length - 1)
        {
            var head = trimmed[..(closing + 1)].Trim();
            var tail = trimmed[(closing + 1)..].Trim();
            if (tail.Length > 0) return (Truncate(head, 100), Truncate(tail, 200));
        }

        // 100자를 넘으면 넘치는 부분을 상세주소로 옮긴다
        if (trimmed.Length > 100)
            return (Truncate(trimmed, 100), Truncate(trimmed[100..], 200));

        // 상세주소를 뽑아낼 수 없으면 마지막 어절을 상세주소로 쓴다 (빈 값 금지)
        var lastSpace = trimmed.LastIndexOf(' ');
        if (lastSpace > 0 && lastSpace < trimmed.Length - 1)
            return (trimmed[..lastSpace].Trim(), trimmed[(lastSpace + 1)..].Trim());

        return (trimmed, "-");
    }

    /// <summary>"-- / 032-721-5737" 처럼 섞여 오는 값에서 쓸 수 있는 번호 하나를 고른다.</summary>
    private static string? NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (var candidate in raw.Split(['/', ',', '|'], StringSplitOptions.TrimEntries))
        {
            var digits = new string(candidate.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 9 and <= 11) return candidate.Trim();
        }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 쿠팡 송장 반영 + CS 요청 조회.
///
/// 위탁판매 운영에서 이 둘을 자동화하지 않으면 사람이 매일 WING에 들어가
/// 손으로 옮겨야 한다. 그리고 놓치면 바로 페널티로 돌아온다:
///   - 송장 미등록 → '출고 지연'
///   - 취소 요청 방치 → 이미 발주한 물건을 그대로 떠안는다
/// </summary>
public sealed partial class CoupangAdapter : IOrderFulfillmentProvider
{
    private static string InvoicePath(string vendorId) =>
        $"/v2/providers/openapi/apis/api/v4/vendors/{vendorId}/orders/invoices";
    private static string CancelRequestPath(string vendorId) =>
        $"/v2/providers/openapi/apis/api/v4/vendors/{vendorId}/requested-cancel-orders";
    private static string ReturnRequestPath(string vendorId) =>
        $"/v2/providers/openapi/apis/api/v5/vendors/{vendorId}/returnRequests";

    public async Task<FulfillmentResult> UploadTrackingAsync(
        TrackingUpload upload, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var vendorId = cred.Require("vendor_id");

            // shipmentBoxId는 주문을 수집할 때 받아 둬야 한다.
            // 없으면 어느 배송 묶음에 송장을 붙일지 쿠팡이 알 수 없다.
            if (string.IsNullOrWhiteSpace(upload.ShipmentBoxId))
                return FulfillmentResult.Fail("NO_SHIPMENT_BOX",
                    "이 주문에는 쿠팡 배송 묶음 번호(shipmentBoxId)가 없습니다. " +
                    "이 기능이 생기기 전에 수집된 주문이라면 주문을 다시 수집한 뒤 시도하세요.");

            var body = new
            {
                vendorId,
                orderSheetInvoiceApplyDtos = new[]
                {
                    new
                    {
                        shipmentBoxId = upload.ShipmentBoxId,
                        orderId = upload.MarketOrderId,
                        vendorItemId = upload.VendorItemId,
                        deliveryCompanyCode = upload.DeliveryCompanyCode,
                        invoiceNumber = upload.TrackingNo,
                        splitShipping = false,
                        preSplitShipped = false,
                    },
                },
            };

            var response = await SendAsync(HttpMethod.Post, InvoicePath(vendorId), "", body, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return FulfillmentResult.Fail($"HTTP_{(int)response.StatusCode}", Shorten(raw), raw);

            // 성공 응답이어도 건별로 실패가 섞여 온다 — data[].succeed를 봐야 한다
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    if (entry.TryGetProperty("succeed", out var ok) && ok.ValueKind == JsonValueKind.False)
                    {
                        var reason = entry.TryGetProperty("resultMessage", out var rm)
                            ? rm.GetString() : "쿠팡이 송장 등록을 거부했습니다.";
                        return FulfillmentResult.Fail("REJECTED", reason ?? "거부됨", raw);
                    }
                }
            }

            logger.LogInformation("쿠팡 송장 반영 완료: 주문 {OrderId} → {Tracking}",
                upload.MarketOrderId, upload.TrackingNo);
            return FulfillmentResult.Ok(raw);
        }
        catch (PluginCredentialException ex) { return FulfillmentResult.Fail("NO_CREDENTIAL", ex.Message); }
        catch (Exception ex) { return FulfillmentResult.Fail("ERROR", ex.Message); }
    }

    public async Task<IReadOnlyList<MarketCsTicket>> FetchCsTicketsAsync(
        DateRange range, MarketCredential cred, CancellationToken ct)
    {
        var vendorId = cred.Require("vendor_id");
        var tickets = new List<MarketCsTicket>();

        // 취소와 반품은 별도 API다. 한쪽이 실패해도 다른 쪽은 가져온다 —
        // 반품 조회가 막혔다고 취소까지 놓치면 손해가 커진다.
        await CollectAsync(tickets, CancelRequestPath(vendorId), "Cancel", range, cred, ct);
        await CollectAsync(tickets, ReturnRequestPath(vendorId), "Return", range, cred, ct);

        return tickets;
    }

    private async Task CollectAsync(
        List<MarketCsTicket> into, string path, string kind,
        DateRange range, MarketCredential cred, CancellationToken ct)
    {
        try
        {
            var query =
                $"createdAtFrom={range.From:yyyy-MM-dd}&createdAtTo={range.To:yyyy-MM-dd}" +
                "&status=RU&maxPerPage=50";
            var response = await SendAsync(HttpMethod.Get, path, query, null, cred, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("쿠팡 {Kind} 요청 조회 실패: {Body}", kind, Shorten(raw));
                return;
            }

            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in data.EnumerateArray())
            {
                var orderId = CsText(item, "orderId");
                if (string.IsNullOrWhiteSpace(orderId)) continue;

                // 접수번호가 없으면 주문번호로 대체해 중복만은 막는다
                var ticketId = CsText(item, "receiptId") ?? CsText(item, "cancelId") ?? $"{kind}-{orderId}";

                into.Add(new MarketCsTicket
                {
                    MarketCode = Code,
                    MarketTicketId = ticketId,
                    MarketOrderId = orderId,
                    Kind = kind,
                    ProductName = CsText(item, "vendorItemName") ?? CsText(item, "sellerProductName"),
                    Reason = CsText(item, "reasonCodeText") ?? CsText(item, "cancelReasonCategory")
                        ?? CsText(item, "reasonCode"),
                    Quantity = item.TryGetProperty("cancelCount", out var cc) && cc.TryGetInt32(out var n) ? n : 1,
                    RequestedAt = item.TryGetProperty("createdAt", out var ca)
                        && DateTimeOffset.TryParse(ca.GetString(), out var at) ? at : DateTimeOffset.UtcNow,
                });
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "쿠팡 {Kind} 요청 조회 중 오류", kind);
        }
    }

    /// <summary>CS 응답 필드 읽기. 같은 값이 문자열로도 숫자로도 온다.</summary>
    private static string? CsText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => v.ToString(),
                _ => null,
            }
            : null;

    private static string Shorten(string s) => s.Length <= 200 ? s : s[..200];
}

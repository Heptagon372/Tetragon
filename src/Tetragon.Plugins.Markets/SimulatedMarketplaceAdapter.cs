using System.Collections.Concurrent;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 시뮬레이션 마켓 — 자격증명 없이 등록 파이프라인 전체를 검증하기 위한 어댑터.
/// 등록된 상품 기준으로 데모 주문도 생성한다.
/// </summary>
public sealed class SimulatedMarketplaceAdapter : IMarketplaceAdapter, IOrderFulfillmentProvider
{
    public string Code => "mockmarket";
    public string DisplayName => "시뮬레이션 마켓";
    public string Version => "1.0.0";
    public bool IsLive => false;

    private static readonly ConcurrentDictionary<string, (string Name, Money Price, DateTimeOffset At)> Registered = new();
    private static long _sequence = 202600000;

    public async Task<ListingResult> RegisterAsync(ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(300, 900), ct);

        // 검증 시뮬레이션: 이미지/이름 없으면 실패 (실마켓의 유효성 오류 재현)
        if (string.IsNullOrWhiteSpace(payload.Name))
            return ListingResult.Fail("INVALID_NAME", "상품명이 비어 있습니다.");
        if (payload.ImageUrls.Count == 0)
            return ListingResult.Fail("NO_IMAGE", "대표 이미지가 필요합니다.");
        if (payload.SalePrice.Amount < 100)
            return ListingResult.Fail("PRICE_TOO_LOW", "판매가는 100원 이상이어야 합니다.");

        var itemId = Interlocked.Increment(ref _sequence).ToString();
        Registered[itemId] = (payload.Name, payload.SalePrice, DateTimeOffset.UtcNow);
        return ListingResult.Ok(itemId);
    }

    public async Task<ListingResult> UpdateAsync(string marketItemId, ListingPayload payload, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        if (!Registered.ContainsKey(marketItemId))
            return ListingResult.Fail("NOT_FOUND", "등록된 상품이 아닙니다.");
        Registered[marketItemId] = (payload.Name, payload.SalePrice, DateTimeOffset.UtcNow);
        return ListingResult.Ok(marketItemId);
    }

    public async Task<ListingResult> DeleteAsync(string marketItemId, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        Registered.TryRemove(marketItemId, out _);
        return ListingResult.Ok(marketItemId);
    }

    public async Task<ListingResult> UpdatePriceStockAsync(string marketItemId, Money price, int stock, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(200, ct);
        if (Registered.TryGetValue(marketItemId, out var existing))
            Registered[marketItemId] = (existing.Name, price, DateTimeOffset.UtcNow);
        return ListingResult.Ok(marketItemId);
    }

    public async Task<IReadOnlyList<MarketOrder>> FetchOrdersAsync(DateRange range, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        // 등록 상품 중 일부에 데모 주문 생성 (결정적: 상품 ID 시드)
        var orders = new List<MarketOrder>();
        string[] names = ["김주문", "이구매", "박소비", "최쇼핑"];
        // 위탁판매는 구매자 배송지가 곧 공급처 발주의 수령지이므로 함께 만들어 준다
        (string Zip, string Addr)[] addresses =
        [
            ("06236", "서울 강남구 테헤란로 152 강남파이낸스센터 20층"),
            ("13529", "경기 성남시 분당구 판교역로 235 에이치스퀘어 N동 7층"),
            ("48058", "부산 해운대구 센텀중앙로 97 센텀스카이비즈 1204호"),
            ("35242", "대전 서구 둔산중로 78번길 15 3층"),
        ];
        string[] messages = ["부재시 경비실에 맡겨주세요", "문 앞에 놓아주세요", "", "배송 전 연락 부탁드립니다"];

        foreach (var (itemId, info) in Registered)
        {
            var seed = int.Parse(itemId[^4..]);
            var random = new Random(seed);
            var orderCount = random.Next(0, 3);
            for (var i = 0; i < orderCount; i++)
            {
                var buyer = names[random.Next(names.Length)];
                var (zip, addr) = addresses[random.Next(addresses.Length)];
                var quantity = random.Next(1, 4);
                orders.Add(new MarketOrder
                {
                    MarketOrderId = $"MO{itemId}{i:00}",
                    MarketCode = Code,
                    MarketItemId = itemId,
                    ProductName = info.Name,
                    Quantity = quantity,
                    // 마켓이 주는 결제금액은 단가가 아니라 총액이다
                    PaidAmount = info.Price.Multiply(quantity),
                    OrdererName = buyer,
                    OrderedAt = DateTimeOffset.UtcNow.AddHours(-random.Next(1, 48)),
                    Status = "Paid",
                    ShipTo = new ShippingAddress
                    {
                        ReceiverName = buyer,
                        Phone = $"010-{random.Next(1000, 9999)}-{random.Next(1000, 9999)}",
                        Zipcode = zip,
                        Address1 = addr,
                        Message = messages[random.Next(messages.Length)],
                    },
                    // 실제 마켓처럼 배송 묶음·옵션 식별자를 함께 준다 (송장 반영 검증용)
                    ShipmentBoxId = $"SB{itemId}{i:00}",
                    VendorItemId = $"VI{itemId}",
                });
            }
        }
        return orders;
    }

    // ── 주문 이행 (송장·CS) ──────────────────────────────────────────────

    /// <summary>반영된 송장. 자동화가 같은 주문을 두 번 올리지 않는지 확인하는 데 쓴다.</summary>
    private static readonly ConcurrentDictionary<string, string> UploadedInvoices = new();

    public async Task<FulfillmentResult> UploadTrackingAsync(
        TrackingUpload upload, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(100, 300), ct);

        // 실마켓과 같은 실패 조건을 재현한다 — 자동화가 이걸 제대로 다루는지 봐야 한다
        if (string.IsNullOrWhiteSpace(upload.ShipmentBoxId))
            return FulfillmentResult.Fail("NO_SHIPMENT_BOX", "배송 묶음 번호가 없습니다.");
        if (string.IsNullOrWhiteSpace(upload.TrackingNo))
            return FulfillmentResult.Fail("NO_INVOICE", "송장번호가 비어 있습니다.");

        UploadedInvoices[upload.MarketOrderId] = upload.TrackingNo;
        return FulfillmentResult.Ok($"{{\"orderId\":\"{upload.MarketOrderId}\",\"invoice\":\"{upload.TrackingNo}\"}}");
    }

    /// <summary>
    /// 데모 CS 요청. 수집된 주문 중 일부에 취소·반품이 걸린 상황을 만든다.
    /// 주문번호 끝자리로 결정하므로 돌릴 때마다 같은 결과가 나온다.
    /// </summary>
    public async Task<IReadOnlyList<MarketCsTicket>> FetchCsTicketsAsync(
        DateRange range, MarketCredential cred, CancellationToken ct)
    {
        await Task.Delay(200, ct);

        var tickets = new List<MarketCsTicket>();
        foreach (var (itemId, info) in Registered)
        {
            var seed = int.Parse(itemId[^4..]);
            // 7개 중 1개 꼴로만 CS가 생기게 한다
            if (seed % 7 != 0) continue;

            var orderId = $"MO{itemId}00";
            var isReturn = seed % 14 == 0;
            tickets.Add(new MarketCsTicket
            {
                MarketCode = Code,
                MarketTicketId = $"CS{itemId}",
                MarketOrderId = orderId,
                Kind = isReturn ? "Return" : "Cancel",
                ProductName = info.Name,
                Reason = isReturn ? "단순 변심 (색상이 사진과 다름)" : "고객 변심으로 주문 취소",
                Quantity = 1,
                RequestedAt = DateTimeOffset.UtcNow.AddHours(-Random.Shared.Next(1, 24)),
            });
        }
        return tickets;
    }
}

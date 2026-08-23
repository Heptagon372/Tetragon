using Tetragon.Application.Ports;
using Tetragon.Domain.Ordering;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Services;

/// <summary>
/// 위탁판매 발주서.
///
/// 주문이 들어오면 판매자가 해야 하는 일은 하나다:
/// **공급처에서 그 상품을 사되, 배송지를 구매자 주소로 지정하는 것.**
/// 이 화면에 필요한 정보(무엇을·어디서·누구에게·얼마에)를 한 곳에 모은다.
///
/// 도매꾹은 외부 발주 API를 제공하지 않으므로 완전 자동 발주는 불가능하다.
/// 대신 사람이 바로 실행할 수 있게 상품 링크·복사용 배송지·마진을 준비한다.
/// </summary>
public sealed record PurchaseOrderSheet
{
    public required Guid OrderId { get; init; }
    public required string MarketCode { get; init; }
    public required string MarketOrderId { get; init; }
    public required string Status { get; init; }

    // 무엇을
    public string? ProductName { get; init; }
    public string? OptionName { get; init; }
    public int Quantity { get; init; }

    // 어디서 (공급처)
    public string? SupplierCode { get; init; }
    public string? SupplierName { get; init; }
    public string? SupplierPhone { get; init; }
    public string? SupplierUrl { get; init; }
    public decimal? SupplierUnitCost { get; init; }
    public int? MinOrderQty { get; init; }

    // 누구에게 (구매자)
    public string? ReceiverName { get; init; }
    public string? ReceiverPhone { get; init; }
    public string? ReceiverZipcode { get; init; }
    public string? ReceiverAddress { get; init; }
    public string? DeliveryMessage { get; init; }

    // 얼마에
    public decimal PaidAmount { get; init; }
    public decimal? EstimatedCost { get; init; }
    public decimal? EstimatedMargin { get; init; }
    public string? SupplierOrderNo { get; init; }

    /// <summary>공급처 주문 폼에 붙여넣기 좋은 한 줄 배송지.</summary>
    public string ShippingLine =>
        string.Join(" / ", new[]
        {
            ReceiverName,
            ReceiverPhone,
            string.IsNullOrWhiteSpace(ReceiverZipcode) ? ReceiverAddress : $"({ReceiverZipcode}) {ReceiverAddress}",
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>발주 전에 사람이 확인해야 하는 경고들.</summary>
    public List<string> Warnings { get; init; } = [];
}

public sealed class PurchaseOrderSheetBuilder(
    IOrderRepository orders,
    IProductRepository products,
    IListingRepository listings)
{
    public async Task<PurchaseOrderSheet?> BuildAsync(Guid orderId, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderId, ct);
        if (order is null) return null;

        // 아직 상품과 연결되지 않았다면 지금 연결한다 (마켓 상품번호 → Listing → Product)
        if (order.ProductId is null && order.MarketItemId is not null)
            await TryLinkSourceAsync(order, ct);

        var product = order.ProductId is { } pid ? await products.FindAsync(pid, ct) : null;
        var logistics = product is not null
            ? ConsignmentLogistics.FromAttributes(product.Attributes, product.BasePrice.Currency)
            : new ConsignmentLogistics();

        var warnings = new List<string>();
        if (product is null)
            warnings.Add("이 주문이 어느 상품에서 왔는지 찾지 못했습니다 — 공급처를 수동으로 확인하세요.");
        if (string.IsNullOrWhiteSpace(order.ReceiverAddress))
            warnings.Add("구매자 배송지가 없습니다 — 마켓에서 주문 정보를 다시 수집하세요.");
        if (logistics.MinOrderQty is { } moq && order.Quantity < moq)
            warnings.Add($"공급처 최소구매수량이 {moq}개입니다. 주문은 {order.Quantity}개라 " +
                         $"{moq}개를 구매해야 할 수 있습니다.");

        var unitCost = order.SupplierUnitCost?.Amount
                       ?? product?.Variants.FirstOrDefault()?.SourcePrice.Amount;
        var estimatedCost = unitCost * order.Quantity;
        if (estimatedCost is { } cost && cost >= order.PaidAmount.Amount)
            warnings.Add($"공급가({cost:N0}원)가 판매금액({order.PaidAmount.Amount:N0}원) 이상입니다 — 손실 주문입니다.");

        return new PurchaseOrderSheet
        {
            OrderId = order.Id,
            MarketCode = order.MarketCode,
            MarketOrderId = order.MarketOrderId,
            Status = order.Status.ToString(),
            ProductName = order.ProductName,
            OptionName = order.OptionName,
            Quantity = order.Quantity,
            SupplierCode = order.SupplierCode ?? product?.Source.SupplierCode,
            SupplierName = logistics.SupplierName,
            SupplierPhone = logistics.SupplierPhone,
            SupplierUrl = order.SupplierUrl ?? product?.Source.Url,
            SupplierUnitCost = unitCost,
            MinOrderQty = logistics.MinOrderQty,
            ReceiverName = order.ReceiverName,
            ReceiverPhone = order.ReceiverPhone,
            ReceiverZipcode = order.ReceiverZipcode,
            ReceiverAddress = order.ReceiverAddress,
            DeliveryMessage = order.DeliveryMessage,
            PaidAmount = order.PaidAmount.Amount,
            EstimatedCost = estimatedCost,
            EstimatedMargin = order.EstimatedMargin ?? (estimatedCost is { } c ? order.PaidAmount.Amount - c : null),
            SupplierOrderNo = order.SupplierOrderNo,
            Warnings = warnings,
        };
    }

    /// <summary>마켓 상품번호로 Listing을 찾아 원본 상품·공급처를 주문에 연결한다.</summary>
    private async Task TryLinkSourceAsync(Order order, CancellationToken ct)
    {
        var listing = await listings.FindByMarketItemAsync(order.MarketCode, order.MarketItemId!, ct);
        if (listing is null) return;

        var product = await products.FindAsync(listing.ProductId, ct);
        if (product is null) return;

        order.LinkSource(
            product.Id,
            product.Source.SupplierCode,
            product.Source.SourceProductId,
            product.Source.Url,
            product.Variants.FirstOrDefault()?.SourcePrice);
        await orders.SaveAsync(ct);
    }
}

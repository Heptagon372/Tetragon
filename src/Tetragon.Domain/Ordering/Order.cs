using Tetragon.SharedKernel;

namespace Tetragon.Domain.Ordering;

/// <summary>
/// 표준 주문 (설계서 5.8). 마켓 주문 수집 → 표준 Order 변환.
/// 상태 머신: Imported → SupplierOrdered → AtForwarder → Shipped → Delivered (+ Cancelled)
/// </summary>
public sealed class Order : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string MarketCode { get; private set; } = "";
    public string MarketOrderId { get; private set; } = "";
    public string? MarketItemId { get; private set; }
    public string? ProductName { get; private set; }
    public string? OptionName { get; private set; }
    public int Quantity { get; private set; }
    public Money PaidAmount { get; private set; }
    public string? OrdererName { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.Imported;
    public string? TrackingNo { get; private set; }
    public DateTimeOffset OrderedAt { get; private set; }
    public DateTimeOffset ImportedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    // ── 위탁판매 발주 정보 ────────────────────────────────────────────────
    // 주문이 들어오면 우리가 공급처에서 사서 구매자에게 직접 보내야 한다.
    // 그러려면 "어디서 사는지"와 "누구에게 보내는지"가 주문에 붙어 있어야 한다.

    /// <summary>우리 상품 ID. Listing을 통해 역추적해 붙인다.</summary>
    public Guid? ProductId { get; private set; }
    public string? SupplierCode { get; private set; }
    public string? SupplierProductId { get; private set; }
    /// <summary>공급처 상품 URL — 원클릭 발주 링크로 쓴다.</summary>
    public string? SupplierUrl { get; private set; }
    /// <summary>공급처 원가(1개 기준). 마진 계산용.</summary>
    public Money? SupplierUnitCost { get; private set; }

    /// <summary>구매자 배송지 — 공급처 발주 시 수령지로 그대로 넘긴다.</summary>
    public string? ReceiverName { get; private set; }
    public string? ReceiverPhone { get; private set; }
    public string? ReceiverZipcode { get; private set; }
    public string? ReceiverAddress { get; private set; }
    public string? DeliveryMessage { get; private set; }

    /// <summary>공급처에서 실제로 발주한 주문번호 (사용자가 입력).</summary>
    public string? SupplierOrderNo { get; private set; }
    /// <summary>공급처에 실제로 지불한 금액.</summary>
    public Money? SupplierPaidAmount { get; private set; }

    // ── 송장 반영 ────────────────────────────────────────────────────────
    // 공급처가 출고해 송장이 나와도 마켓에 올리지 않으면 '배송지연'으로 잡힌다.
    // 마켓이 요구하는 식별자를 주문 수집 시점에 받아 둬야 나중에 올릴 수 있다.

    /// <summary>마켓의 배송 묶음 식별자 (쿠팡 shipmentBoxId).</summary>
    public string? ShipmentBoxId { get; private set; }
    /// <summary>마켓 내부 옵션 식별자 (쿠팡 vendorItemId).</summary>
    public string? VendorItemId { get; private set; }
    /// <summary>택배사 코드 (송장과 함께 마켓에 올린다).</summary>
    public string? DeliveryCompanyCode { get; private set; }
    /// <summary>송장을 마켓에 반영한 시각. null이면 아직 안 올렸다.</summary>
    public DateTimeOffset? TrackingUploadedAt { get; private set; }
    /// <summary>송장 반영 실패 사유 — 자동화가 재시도할지 판단하는 근거.</summary>
    public string? TrackingUploadError { get; private set; }

    /// <summary>자동화가 이 주문에 손댄 마지막 결과 (사람이 볼 이력).</summary>
    public string? AutomationNote { get; private set; }

    private Order() { }

    public static Order Import(string tenantId, string marketCode, string marketOrderId,
        string? marketItemId, string? productName, string? optionName, int quantity,
        Money paidAmount, string? ordererName, DateTimeOffset orderedAt,
        string? receiverName = null, string? receiverPhone = null,
        string? receiverZipcode = null, string? receiverAddress = null, string? deliveryMessage = null,
        string? shipmentBoxId = null, string? vendorItemId = null)
        => new()
        {
            ShipmentBoxId = shipmentBoxId,
            VendorItemId = vendorItemId,
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MarketCode = marketCode,
            MarketOrderId = marketOrderId,
            MarketItemId = marketItemId,
            ProductName = productName,
            OptionName = optionName,
            Quantity = quantity,
            PaidAmount = paidAmount,
            OrdererName = ordererName,
            OrderedAt = orderedAt,
            ReceiverName = receiverName ?? ordererName,
            ReceiverPhone = receiverPhone,
            ReceiverZipcode = receiverZipcode,
            ReceiverAddress = receiverAddress,
            DeliveryMessage = deliveryMessage,
        };

    /// <summary>어느 상품·공급처에서 발주해야 하는지 연결한다 (Listing 역추적 결과).</summary>
    public void LinkSource(Guid productId, string supplierCode, string? supplierProductId,
        string? supplierUrl, Money? unitCost)
    {
        ProductId = productId;
        SupplierCode = supplierCode;
        SupplierProductId = supplierProductId;
        SupplierUrl = supplierUrl;
        SupplierUnitCost = unitCost;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>공급처 발주 완료 기록. 상태를 SupplierOrdered로 전이한다.</summary>
    public void MarkSupplierOrdered(string supplierOrderNo, Money? paidAmount)
    {
        SupplierOrderNo = supplierOrderNo;
        SupplierPaidAmount = paidAmount;
        if (Status == OrderStatus.Imported) TransitionTo(OrderStatus.SupplierOrdered);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>예상 마진 = 판매금액 − 공급처 원가(수량 반영). 원가를 모르면 null.</summary>
    public decimal? EstimatedMargin =>
        SupplierPaidAmount is { } paid ? PaidAmount.Amount - paid.Amount
        : SupplierUnitCost is { } unit ? PaidAmount.Amount - unit.Amount * Quantity
        : null;

    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.Imported] = [OrderStatus.SupplierOrdered, OrderStatus.Cancelled],
        [OrderStatus.SupplierOrdered] = [OrderStatus.AtForwarder, OrderStatus.Cancelled],
        [OrderStatus.AtForwarder] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
    };

    public void TransitionTo(OrderStatus next)
    {
        if (!Transitions.TryGetValue(Status, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"허용되지 않는 주문 상태 전이: {Status} → {next}");
        Status = next;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RegisterTracking(string trackingNo, string? deliveryCompanyCode = null)
    {
        TrackingNo = trackingNo;
        if (deliveryCompanyCode is { Length: > 0 }) DeliveryCompanyCode = deliveryCompanyCode;
        if (Status is OrderStatus.SupplierOrdered or OrderStatus.AtForwarder)
            TransitionTo(Status == OrderStatus.SupplierOrdered ? OrderStatus.AtForwarder : OrderStatus.Shipped);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>송장을 마켓에 올렸다. 이 시점부터 마켓이 배송중으로 인식한다.</summary>
    public void MarkTrackingUploaded()
    {
        TrackingUploadedAt = DateTimeOffset.UtcNow;
        TrackingUploadError = null;
        if (Status == OrderStatus.AtForwarder) TransitionTo(OrderStatus.Shipped);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>송장 반영 실패. 자동화가 다음 주기에 다시 시도한다.</summary>
    public void MarkTrackingUploadFailed(string error)
    {
        TrackingUploadError = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>자동화 처리 이력 한 줄. 사람이 "왜 이렇게 됐는지" 보는 용도.</summary>
    public void NoteAutomation(string note)
    {
        AutomationNote = note.Length > 500 ? note[..500] : note;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>구매자가 취소·반품해서 더 진행하면 안 되는 주문.</summary>
    public void Cancel(string reason)
    {
        if (Status is OrderStatus.Delivered or OrderStatus.Cancelled) return;
        Status = OrderStatus.Cancelled;
        NoteAutomation(reason);
    }

    /// <summary>송장을 마켓에 올려야 하는 상태인지.</summary>
    public bool NeedsTrackingUpload =>
        TrackingNo is { Length: > 0 }
        && TrackingUploadedAt is null
        && Status is not OrderStatus.Cancelled;
}

public enum OrderStatus
{
    Imported,        // 마켓에서 수집됨
    SupplierOrdered, // 공급처 발주 완료
    AtForwarder,     // 배대지 입고
    Shipped,         // 송장 등록
    Delivered,
    Cancelled,
}

using Tetragon.SharedKernel;

namespace Tetragon.Domain.Ordering;

/// <summary>
/// CS 요청 — 구매자가 건 취소·반품·교환.
///
/// 위탁판매에서 이게 특별한 이유: 물건이 우리 손을 거치지 않는다.
/// 구매자가 반품하면 물건은 공급처로 돌아가고, 우리는 그 사이에서
/// <b>마켓에 답을 주는 동시에 공급처에도 같은 조치를 취해야</b> 한다.
/// 한쪽만 처리하면 돈이나 재고가 새어 나간다:
///   - 마켓만 취소 → 공급처에는 발주가 살아 있어 물건이 그대로 나간다
///   - 공급처만 취소 → 마켓은 배송 대기로 남아 미출고 페널티를 받는다
///
/// 그래서 티켓마다 "공급처에도 조치가 필요한가"를 들고 다닌다.
/// </summary>
public sealed class CsTicket : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string MarketCode { get; private set; } = "";
    public string MarketOrderId { get; private set; } = "";
    /// <summary>마켓이 이 요청에 붙인 식별자 (중복 수집 방지 키).</summary>
    public string MarketTicketId { get; private set; } = "";

    /// <summary>우리 주문과 연결됐는지. 없으면 수집만 된 상태다.</summary>
    public Guid? OrderId { get; private set; }

    public CsKind Kind { get; private set; }
    public CsStatus Status { get; private set; } = CsStatus.Open;

    public string? ProductName { get; private set; }
    public string? Reason { get; private set; }
    public int Quantity { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 공급처에도 조치가 필요한가. 이미 발주가 나간 뒤의 취소·반품이면 true다.
    /// true인데 방치하면 우리 돈으로 산 물건이 구매자에게 그냥 간다.
    /// </summary>
    public bool SupplierActionRequired { get; private set; }

    /// <summary>공급처 쪽 처리를 끝냈는지 (사람이 확인 후 체크).</summary>
    public bool SupplierActionDone { get; private set; }

    public string? HandledNote { get; private set; }
    public DateTimeOffset? HandledAt { get; private set; }

    private CsTicket() { }

    public static CsTicket Import(
        string tenantId, string marketCode, string marketTicketId, string marketOrderId,
        CsKind kind, string? productName, string? reason, int quantity,
        DateTimeOffset requestedAt, Guid? orderId, bool supplierActionRequired)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            MarketCode = marketCode,
            MarketTicketId = marketTicketId,
            MarketOrderId = marketOrderId,
            Kind = kind,
            ProductName = productName,
            Reason = reason,
            Quantity = quantity < 1 ? 1 : quantity,
            RequestedAt = requestedAt,
            OrderId = orderId,
            SupplierActionRequired = supplierActionRequired,
        };

    /// <summary>나중에 주문과 연결됐을 때.</summary>
    public void LinkOrder(Guid orderId, bool supplierActionRequired)
    {
        OrderId = orderId;
        SupplierActionRequired = supplierActionRequired;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MarkSupplierActionDone(string note)
    {
        SupplierActionDone = true;
        HandledNote = note;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>처리 완료. 공급처 조치가 남아 있으면 완료로 볼 수 없다.</summary>
    public void Resolve(string note)
    {
        if (SupplierActionRequired && !SupplierActionDone)
            throw new InvalidOperationException(
                "공급처 조치가 아직 안 끝났습니다. 공급처 취소/반품을 먼저 처리하세요.");
        Status = CsStatus.Resolved;
        HandledNote = note;
        HandledAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>대응이 필요 없는 건 (오인 접수 등).</summary>
    public void Dismiss(string note)
    {
        Status = CsStatus.Dismissed;
        HandledNote = note;
        HandledAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>지금 사람이 봐야 하는 건인지.</summary>
    public bool NeedsAttention =>
        Status == CsStatus.Open || (SupplierActionRequired && !SupplierActionDone);
}

public enum CsKind
{
    /// <summary>출고 전 취소 요청.</summary>
    Cancel,
    /// <summary>수령 후 반품 요청.</summary>
    Return,
    /// <summary>교환 요청.</summary>
    Exchange,
}

public enum CsStatus
{
    Open,
    Resolved,
    Dismissed,
}

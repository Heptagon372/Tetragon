using Tetragon.SharedKernel;

namespace Tetragon.Domain.Listings;

/// <summary>마켓 등록 상태 (Product × Marketplace, 설계서 §3.2 Listing 애그리거트).</summary>
public sealed class Listing : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public Guid ProductId { get; private set; }
    public string MarketCode { get; private set; } = "";
    public ListingStatus Status { get; private set; } = ListingStatus.Pending;
    /// <summary>마켓이 발급한 상품 번호.</summary>
    public string? MarketItemId { get; private set; }
    public string? LastError { get; private set; }
    public Money? ListedPrice { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public List<ListingSyncLog> SyncLogs { get; private set; } = [];

    private Listing() { }

    public static Listing Create(string tenantId, Guid productId, string marketCode)
        => new() { Id = Guid.NewGuid(), TenantId = tenantId, ProductId = productId, MarketCode = marketCode };

    public void MarkRegistered(string marketItemId, Money price)
    {
        Status = ListingStatus.Registered;
        MarketItemId = marketItemId;
        ListedPrice = price;
        LastError = null;
        AppendLog("register", true, $"마켓 상품번호 {marketItemId}");
    }

    public void MarkFailed(string error)
    {
        Status = ListingStatus.Failed;
        LastError = error;
        AppendLog("register", false, error);
    }

    public void MarkPriceStockSynced(Money price, int stock)
    {
        ListedPrice = price;
        AppendLog("price-stock", true, $"가격 {price}, 재고 {stock}");
    }

    public void Suspend(string reason)
    {
        Status = ListingStatus.Suspended;
        AppendLog("suspend", true, reason);
    }

    private void AppendLog(string action, bool success, string? message)
    {
        SyncLogs.Add(new ListingSyncLog(action, success, message, DateTimeOffset.UtcNow));
        if (SyncLogs.Count > 50) SyncLogs.RemoveAt(0); // 로그 상한
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public enum ListingStatus { Pending, Registered, Failed, Suspended }

public sealed record ListingSyncLog(string Action, bool Success, string? Message, DateTimeOffset At);

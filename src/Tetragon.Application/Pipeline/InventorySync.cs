using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Events;
using Tetragon.Domain.Listings;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Pipeline;

/// <summary>
/// 재고 동기화 (설계서 5.7).
/// 공급처 재확인 → 변경 감지(diff) → 변경된 항목만 마켓 API 호출.
/// </summary>
public sealed class InventoryCheckHandler(
    ISupplierPluginRegistry suppliers,
    IProductRepository products,
    IEventBus bus,
    ILogger<InventoryCheckHandler> logger) : IIntegrationEventHandler<InventoryCheckRequested>
{
    public async Task HandleAsync(InventoryCheckRequested @event, CancellationToken ct)
    {
        var product = await products.FindAsync(@event.ProductId, ct);
        if (product is null) return;

        var plugin = suppliers.Resolve(product.Source.SupplierCode);
        if (plugin is null)
        {
            logger.LogWarning("공급처 플러그인 없음: {Code}", product.Source.SupplierCode);
            return;
        }

        try
        {
            var inventory = await plugin.CheckInventoryAsync(product.Source, ct);

            // diff 기반 변경 감지
            var changes = new Dictionary<string, int>();
            foreach (var variant in product.Variants)
            {
                var rawVariant = inventory.Variants.FirstOrDefault(v => v.SourceSkuId == variant.SourceSkuId);
                var newStock = rawVariant?.Stock ?? (inventory.IsAvailable ? variant.SourceStock : 0);
                if (newStock != variant.SourceStock)
                {
                    variant.SourceStock = newStock;
                    changes[variant.VariantId] = newStock;
                }
            }

            if (changes.Count > 0 || !inventory.IsAvailable)
            {
                await products.SaveAsync(ct);
                await bus.PublishAsync(new StockChanged
                {
                    ProductId = product.Id,
                    IsAvailable = inventory.IsAvailable,
                    StockByVariantId = changes,
                    TenantId = @event.TenantId,
                    CorrelationId = @event.CorrelationId,
                    CausationId = @event.EventId,
                }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "재고 확인 실패 (Product {ProductId})", @event.ProductId);
        }
    }
}

/// <summary>StockChanged → 등록된 마켓 리스팅 가격/재고 수정 or 품절 시 판매 중지 (설계서 5.7).</summary>
public sealed class StockChangedHandler(
    IMarketplaceAdapterRegistry markets,
    IListingRepository listings,
    IProductRepository products,
    ICredentialStore credentials,
    ILogger<StockChangedHandler> logger) : IIntegrationEventHandler<StockChanged>
{
    public async Task HandleAsync(StockChanged @event, CancellationToken ct)
    {
        var productListings = await listings.ByProductAsync(@event.ProductId, ct);
        var product = await products.FindAsync(@event.ProductId, ct);
        if (product is null) return;

        foreach (var listing in productListings.Where(l => l.Status == ListingStatus.Registered))
        {
            try
            {
                var adapter = markets.Resolve(listing.MarketCode);
                if (adapter is null || listing.MarketItemId is null) continue;
                var credential = await credentials.GetAsync($"market:{listing.MarketCode}", ct);

                if (!@event.IsAvailable)
                {
                    await adapter.UpdatePriceStockAsync(listing.MarketItemId,
                        listing.ListedPrice ?? product.BasePrice, 0, credential, ct);
                    listing.Suspend("공급처 품절 — 자동 판매 중지");
                }
                else
                {
                    var totalStock = product.Variants.Sum(v => v.SourceStock);
                    await adapter.UpdatePriceStockAsync(listing.MarketItemId,
                        listing.ListedPrice ?? product.BasePrice, totalStock, credential, ct);
                    listing.MarkPriceStockSynced(listing.ListedPrice ?? product.BasePrice, totalStock);
                }
                await listings.SaveAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "마켓 재고 동기화 실패 ({Market}, Listing {ListingId})", listing.MarketCode, listing.Id);
            }
        }
    }
}

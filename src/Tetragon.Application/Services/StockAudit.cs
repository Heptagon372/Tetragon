using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Catalog;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Services;

/// <summary>
/// 공급처 재고 점검 — 품절·재고부족 상품 찾기.
///
/// 위탁판매에서 가장 위험한 상황은 <b>공급처에 없는 물건이 마켓에서 팔리는 것</b>이다.
/// 주문은 들어왔는데 발주할 수 없으면 취소해야 하고, 취소율이 쌓이면 마켓 페널티를 받는다.
///
/// 도매꾹은 상품 조회 API로 현재 재고를 주므로, 등록된 상품을 훑어
/// 품절·재고부족을 미리 찾아낼 수 있다.
/// </summary>
public sealed class StockAudit(
    IProductRepository products,
    ISupplierPluginRegistry suppliers,
    ILogger<StockAudit> logger)
{
    /// <summary>이 수량 아래면 곧 품절될 수 있다고 본다.</summary>
    public const int DefaultLowStockThreshold = 10;

    /// <summary>공급처 부하를 줄이기 위한 호출 간격.</summary>
    private static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// 등록된 상품의 공급처 재고를 확인한다.
    /// 상품 수가 많으면 오래 걸리므로 limit로 나눠 돌린다.
    /// </summary>
    public async Task<StockAuditResult> RunAsync(
        int limit, int lowStockThreshold, bool listedOnly, CancellationToken ct)
    {
        var status = listedOnly ? nameof(ProductStatus.Listed) : null;
        var (items, _) = await products.SearchAsync(status, null, 1, Math.Clamp(limit, 1, 500), ct);

        var soldOut = new List<StockAuditEntry>();
        var lowStock = new List<StockAuditEntry>();
        var priceChanged = new List<StockAuditEntry>();
        var failed = new List<StockAuditEntry>();
        var checkedCount = 0;

        foreach (var product in items)
        {
            if (ct.IsCancellationRequested) break;

            var plugin = suppliers.Resolve(product.Source.SupplierCode);
            if (plugin is null) continue;

            try
            {
                var inventory = await plugin.CheckInventoryAsync(product.Source, ct);
                checkedCount++;

                var currentStock = inventory.Variants.Count > 0
                    ? inventory.Variants.Sum(v => v.Stock)
                    : (inventory.IsAvailable ? product.Variants.Sum(v => v.SourceStock) : 0);

                // 공급처 원가가 바뀌면 마진이 흔들리므로 함께 본다
                var oldCost = product.Variants.FirstOrDefault()?.SourcePrice.Amount ?? 0;
                var newCost = inventory.Variants.FirstOrDefault()?.Price ?? oldCost;

                var entry = new StockAuditEntry
                {
                    ProductId = product.Id,
                    ProductName = product.Name.Get("ko-KR") ?? product.Name.GetOrFirst("ko-KR"),
                    SupplierCode = product.Source.SupplierCode,
                    SupplierName = product.Attributes.GetValueOrDefault(LogisticsKeys.SupplierName),
                    SourceUrl = product.Source.Url,
                    Status = product.Status.ToString(),
                    PreviousStock = product.Variants.Sum(v => v.SourceStock),
                    CurrentStock = currentStock,
                    PreviousCost = oldCost,
                    CurrentCost = newCost,
                };

                if (!inventory.IsAvailable || currentStock <= 0) soldOut.Add(entry);
                else if (currentStock < lowStockThreshold) lowStock.Add(entry);

                // 원가가 5% 넘게 오르면 판매가를 다시 봐야 한다
                if (oldCost > 0 && newCost > 0 && Math.Abs(newCost - oldCost) / oldCost > 0.05m)
                    priceChanged.Add(entry);
            }
            catch (Exception ex)
            {
                // 조회 실패는 품절과 다르다 — 섞어서 판단하면 멀쩡한 상품을 내리게 된다
                failed.Add(new StockAuditEntry
                {
                    ProductId = product.Id,
                    ProductName = product.Name.Get("ko-KR") ?? product.Name.GetOrFirst("ko-KR"),
                    SupplierCode = product.Source.SupplierCode,
                    SourceUrl = product.Source.Url,
                    Status = product.Status.ToString(),
                    Error = ex.Message,
                });
                logger.LogDebug("재고 확인 실패 ({ProductId}): {Error}", product.Id, ex.Message);
            }

            await Task.Delay(CheckDelay, ct);
        }

        logger.LogInformation(
            "재고 점검 완료: {Checked}건 확인 — 품절 {SoldOut}, 재고부족 {Low}, 원가변동 {Price}, 실패 {Failed}",
            checkedCount, soldOut.Count, lowStock.Count, priceChanged.Count, failed.Count);

        return new StockAuditResult
        {
            CheckedCount = checkedCount,
            SoldOut = soldOut,
            LowStock = lowStock,
            PriceChanged = priceChanged,
            Failed = failed,
            LowStockThreshold = lowStockThreshold,
        };
    }
}

public sealed record StockAuditResult
{
    public int CheckedCount { get; init; }
    public int LowStockThreshold { get; init; }
    /// <summary>공급처에 물건이 없다 — 마켓에서 내려야 한다.</summary>
    public List<StockAuditEntry> SoldOut { get; init; } = [];
    /// <summary>곧 품절될 수 있다.</summary>
    public List<StockAuditEntry> LowStock { get; init; } = [];
    /// <summary>공급처 원가가 바뀌어 마진 재확인이 필요하다.</summary>
    public List<StockAuditEntry> PriceChanged { get; init; } = [];
    /// <summary>조회 자체가 실패 — 품절로 단정하면 안 된다.</summary>
    public List<StockAuditEntry> Failed { get; init; } = [];

    public int RiskCount => SoldOut.Count + LowStock.Count;
}

public sealed record StockAuditEntry
{
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = "";
    public string SupplierCode { get; init; } = "";
    public string? SupplierName { get; init; }
    public string SourceUrl { get; init; } = "";
    public string Status { get; init; } = "";
    public int PreviousStock { get; init; }
    public int CurrentStock { get; init; }
    public decimal PreviousCost { get; init; }
    public decimal CurrentCost { get; init; }
    public string? Error { get; init; }

    /// <summary>원가 변동률 (%). 양수면 올랐다.</summary>
    public decimal CostChangePct =>
        PreviousCost > 0 ? (CurrentCost - PreviousCost) / PreviousCost * 100m : 0m;
}

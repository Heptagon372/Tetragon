using Tetragon.Application.Ports;
using Tetragon.Domain.Catalog;
using Tetragon.Domain.Events;
using Tetragon.Domain.Sourcing;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Application.UseCases;

/// <summary>
/// 링크 하나 → 마켓 등록까지.
///
/// 기존 흐름은 "URL 수집" 화면에서 긁고 → "상품" 화면에서 확인하고 → 등록 버튼을 누르는
/// 세 단계였다. 위탁판매에서 실제로 하는 일은 "도매꾹에서 괜찮은 물건을 보면 쿠팡에 올린다"
/// 한 줄이므로, 그 한 줄을 그대로 실행한다.
///
/// 수집 자체는 기존 파이프라인이 하고, 이 유스케이스는 두 가지만 더한다:
///   1. Job에 자동 등록 마켓을 붙인다 (컴플라이언스 통과 즉시 등록으로 넘어간다)
///   2. 긁기 전에 이미 가진 상품인지 확인한다 — 같은 링크를 두 번 넣어도 상품이 두 개가 되지 않는다
/// </summary>
public sealed class QuickListUseCase(
    ISupplierPluginRegistry suppliers,
    IScrapeJobRepository jobs,
    IRawProductRepository rawProducts,
    IProductRepository products,
    IEventBus bus)
{
    public async Task<IReadOnlyList<QuickListEntry>> ExecuteAsync(
        IReadOnlyList<string> urls,
        IReadOnlyList<string> marketCodes,
        Guid? pricingPolicyId,
        bool reuseExisting,
        CancellationToken ct)
    {
        var results = new List<QuickListEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in urls.Select(u => u.Trim()).Where(u => u.Length > 0))
        {
            if (!seen.Add(raw)) continue;   // 같은 줄을 두 번 넣은 경우

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
            {
                results.Add(QuickListEntry.Rejected(raw, "주소 형식이 아닙니다."));
                continue;
            }

            var plugin = suppliers.Resolve(url);
            if (plugin is null)
            {
                results.Add(QuickListEntry.Rejected(raw,
                    $"'{url.Host}'을(를) 처리할 공급처가 없습니다. 도매꾹/도매매 상품 링크를 넣어주세요."));
                continue;
            }

            // 이미 수집한 상품이면 다시 긁지 않고 그 상품을 그대로 등록한다.
            var existing = await FindExistingAsync(plugin, url, ct);
            if (existing is not null && reuseExisting)
            {
                var entry = await ListExistingAsync(existing, raw, plugin.Code, marketCodes, ct);
                results.Add(entry);
                continue;
            }

            var job = ScrapeJob.Create(Tenant.Default, raw, pricingPolicyId, marketCodes);
            await jobs.AddAsync(job, ct);
            await jobs.SaveAsync(ct);

            await bus.PublishAsync(new ScrapeRequested
            {
                JobId = job.Id,
                Url = raw,
                PricingPolicyId = pricingPolicyId,
                CorrelationId = job.Id,
            }, ct);

            results.Add(new QuickListEntry
            {
                Url = raw,
                SupplierCode = plugin.Code,
                JobId = job.Id,
                Outcome = QuickListOutcome.Queued,
                Message = existing is null
                    ? "수집 후 자동 등록합니다."
                    : "이미 수집한 상품이지만 다시 수집합니다 (중복 상품이 생깁니다).",
            });
        }

        return results;
    }

    /// <summary>URL만으로 이미 가진 상품인지 확인 — 공급처 API를 부르기 전에 끝낸다.</summary>
    private async Task<Product?> FindExistingAsync(ISupplierPlugin plugin, Uri url, CancellationToken ct)
    {
        if (plugin.TryGetSourceProductId(url) is not { Length: > 0 } sourceId) return null;

        var record = await rawProducts.FindBySourceAsync(plugin.Code, sourceId, ct);
        if (record?.ProductId is not Guid productId) return null;

        return await products.FindAsync(productId, ct);
    }

    /// <summary>
    /// 이미 있는 상품을 마켓으로 보낸다.
    /// 파이프라인이 끝나지 않은 상품(Draft·Blocked 등)은 등록할 수 없으므로 그대로 알린다.
    /// </summary>
    private async Task<QuickListEntry> ListExistingAsync(
        Product product, string url, string supplierCode, IReadOnlyList<string> marketCodes,
        CancellationToken ct)
    {
        if (product.Status is not (ProductStatus.Ready or ProductStatus.Listed))
        {
            return new QuickListEntry
            {
                Url = url,
                SupplierCode = supplierCode,
                ProductId = product.Id,
                Outcome = QuickListOutcome.NeedsAttention,
                Message = product.Status == ProductStatus.Blocked
                    ? "이미 수집했지만 금지어로 차단된 상품입니다. 상품 화면에서 확인 후 해제하세요."
                    : $"이미 수집했지만 아직 등록할 수 없는 상태입니다 ({product.Status}).",
            };
        }

        await bus.PublishAsync(new ListingRequested
        {
            ProductId = product.Id,
            MarketCodes = marketCodes.ToList(),
            CorrelationId = Guid.NewGuid(),
        }, ct);

        return new QuickListEntry
        {
            Url = url,
            SupplierCode = supplierCode,
            ProductId = product.Id,
            Outcome = QuickListOutcome.ExistingProduct,
            Message = "이미 수집한 상품입니다. 다시 긁지 않고 그대로 등록합니다.",
        };
    }
}

public enum QuickListOutcome
{
    /// <summary>수집 Job을 만들었다 — 파이프라인이 끝나면 자동 등록된다.</summary>
    Queued,
    /// <summary>이미 있는 상품을 재사용해 바로 등록 요청했다.</summary>
    ExistingProduct,
    /// <summary>사람이 봐야 한다 (차단됨·수집 미완료 등).</summary>
    NeedsAttention,
    /// <summary>처리할 수 없는 링크.</summary>
    Rejected,
}

public sealed record QuickListEntry
{
    public required string Url { get; init; }
    public string? SupplierCode { get; init; }
    public Guid? JobId { get; init; }
    public Guid? ProductId { get; init; }
    public required QuickListOutcome Outcome { get; init; }
    public required string Message { get; init; }

    public static QuickListEntry Rejected(string url, string message) =>
        new() { Url = url, Outcome = QuickListOutcome.Rejected, Message = message };
}

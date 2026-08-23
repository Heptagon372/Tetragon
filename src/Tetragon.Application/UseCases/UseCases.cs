using Tetragon.Application.Ports;
using Tetragon.Domain.Events;
using Tetragon.Domain.Sourcing;
using Tetragon.SharedKernel;

namespace Tetragon.Application.UseCases;

/// <summary>URL 목록 → 수집 Job 대량 생성 (대량등록의 진입점, 설계서 §8 collect API).</summary>
public sealed class CollectProductsUseCase(IScrapeJobRepository jobs, IEventBus bus)
{
    public async Task<IReadOnlyList<Guid>> ExecuteAsync(
        IReadOnlyList<string> urls, Guid? pricingPolicyId, CancellationToken ct)
    {
        var jobIds = new List<Guid>();
        foreach (var url in urls.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct())
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                continue; // 잘못된 URL은 건너뜀 (API에서 검증 결과 반환)

            var job = ScrapeJob.Create(Tenant.Default, url, pricingPolicyId);
            await jobs.AddAsync(job, ct);
            await jobs.SaveAsync(ct);
            jobIds.Add(job.Id);

            await bus.PublishAsync(new ScrapeRequested
            {
                JobId = job.Id,
                Url = url,
                PricingPolicyId = pricingPolicyId,
                CorrelationId = job.Id,
            }, ct);
        }
        return jobIds;
    }
}

/// <summary>카테고리 단위 대량 수집 요청 (설계 확장 — 카테고리로 상품 수집).</summary>
public sealed class CollectCategoryUseCase(ICategoryCollectJobRepository jobs, IEventBus bus)
{
    public async Task<CategoryCollectJob> ExecuteAsync(
        string supplierCode, string? categoryCode, string? categoryName, string? keyword,
        int maxProducts, decimal? minPrice, decimal? maxPrice, Guid? pricingPolicyId,
        CancellationToken ct)
    {
        var job = CategoryCollectJob.Create(
            Tenant.Default, supplierCode, categoryCode, categoryName, keyword,
            maxProducts, minPrice, maxPrice, pricingPolicyId);

        await jobs.AddAsync(job, ct);
        await jobs.SaveAsync(ct);

        await bus.PublishAsync(new CategoryCrawlRequested
        {
            CategoryJobId = job.Id,
            CorrelationId = job.Id,
        }, ct);

        return job;
    }
}

/// <summary>선택 상품들을 선택 마켓들에 대량 등록 요청.</summary>
public sealed class RequestListingUseCase(IEventBus bus)
{
    public async Task ExecuteAsync(IReadOnlyList<Guid> productIds, IReadOnlyList<string> marketCodes, CancellationToken ct)
    {
        foreach (var productId in productIds)
        {
            await bus.PublishAsync(new ListingRequested
            {
                ProductId = productId,
                MarketCodes = marketCodes.ToList(),
                CorrelationId = Guid.NewGuid(),
            }, ct);
        }
    }
}

/// <summary>차단(Blocked)된 상품을 사용자 확인 후 재개 (설계서 6.1 사용자 확인 큐).</summary>
public sealed class ResumeBlockedProductUseCase(
    IProductRepository products, IScrapeJobRepository jobs, IPipelineNotifier notifier)
{
    public async Task ExecuteAsync(Guid productId, CancellationToken ct)
    {
        var product = await products.FindAsync(productId, ct)
            ?? throw new KeyNotFoundException("상품을 찾을 수 없습니다.");
        product.TransitionTo(Domain.Catalog.ProductStatus.Ready);
        await products.SaveAsync(ct);

        // 연결된 Job도 완료 처리
        var recent = await jobs.RecentAsync(500, ct);
        var job = recent.FirstOrDefault(j => j.ProductId == productId);
        if (job is not null && job.State == JobState.Blocked)
        {
            job.Complete();
            await jobs.SaveAsync(ct);
            notifier.Notify(new PipelineNotification(job.Id, job.Stage.ToString(), job.State.ToString(),
                productId, "사용자 확인 — 차단 해제", DateTimeOffset.UtcNow));
        }
    }
}

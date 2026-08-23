using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.Domain.Events;
using Tetragon.Domain.Sourcing;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Application.Pipeline;

/// <summary>
/// 카테고리를 페이지 단위로 훑어 개별 ScrapeJob을 생성한다.
/// 훑기와 수집을 분리해, 수집 자체는 기존 파이프라인이 그대로 처리하도록 한다.
///
/// 안전장치:
///  - MaxProducts 상한 (기본 100, 최대 1000)
///  - 이미 수집한 상품(공급처+원본ID)은 건너뜀
///  - 페이지 간 지연으로 공급처 레이트리밋 회피
/// </summary>
public sealed class CategoryCrawlHandler(
    ICategoryCrawlerRegistry crawlers,
    ICategoryCollectJobRepository categoryJobs,
    IScrapeJobRepository scrapeJobs,
    IPipelineNotifier notifier,
    IEventBus bus,
    ILogger<CategoryCrawlHandler> logger) : IIntegrationEventHandler<CategoryCrawlRequested>
{
    /// <summary>공급처 부하를 줄이기 위한 페이지 간 지연.</summary>
    private static readonly TimeSpan PageDelay = TimeSpan.FromMilliseconds(600);

    public async Task HandleAsync(CategoryCrawlRequested @event, CancellationToken ct)
    {
        var job = await categoryJobs.FindAsync(@event.CategoryJobId, ct);
        if (job is null) return;

        var crawler = crawlers.Resolve(job.SupplierCode);
        if (crawler is null)
        {
            job.Fail($"'{job.SupplierCode}' 공급처는 카테고리 수집을 지원하지 않습니다.");
            await categoryJobs.SaveAsync(ct);
            Notify(job, $"카테고리 수집 미지원: {job.SupplierCode}");
            return;
        }

        job.Start();
        await categoryJobs.SaveAsync(ct);
        Notify(job, $"{job.CategoryName ?? job.CategoryCode ?? job.Keyword} 카테고리 훑는 중…");

        try
        {
            var page = 1;
            while (!job.ReachedLimit && ct.IsCancellationRequested == false)
            {
                var remaining = job.MaxProducts - (job.QueuedCount + job.SkippedCount);
                var result = await crawler.CrawlAsync(new CategoryCrawlRequest
                {
                    CategoryCode = job.CategoryCode,
                    Keyword = job.Keyword,
                    Page = page,
                    PageSize = Math.Min(remaining, 100),
                    MinPrice = job.MinPrice,
                    MaxPrice = job.MaxPrice,
                    TenantId = job.TenantId,
                }, ct);

                if (result.Items.Count == 0) break;

                // 이미 수집한 상품 제외
                var sourceIds = result.Items.Select(i => i.SourceProductId).ToList();
                var existing = await categoryJobs.ExistingSourceIdsAsync(job.SupplierCode, sourceIds, ct);

                var queued = 0;
                var skipped = 0;
                foreach (var item in result.Items)
                {
                    if (job.QueuedCount + job.SkippedCount + queued + skipped >= job.MaxProducts) break;
                    if (existing.Contains(item.SourceProductId)) { skipped++; continue; }

                    var scrapeJob = ScrapeJob.Create(job.TenantId, item.Url, job.PricingPolicyId);
                    await scrapeJobs.AddAsync(scrapeJob, ct);
                    await scrapeJobs.SaveAsync(ct);

                    await bus.PublishAsync(new ScrapeRequested
                    {
                        JobId = scrapeJob.Id,
                        Url = item.Url,
                        PricingPolicyId = job.PricingPolicyId,
                        TenantId = job.TenantId,
                        CorrelationId = scrapeJob.Id,
                        CausationId = @event.EventId,
                    }, ct);
                    queued++;
                }

                job.RecordPage(result.Items.Count, queued, skipped);
                await categoryJobs.SaveAsync(ct);
                Notify(job, $"{page}페이지: {queued}건 수집 요청, {skipped}건 중복 제외 (누적 {job.QueuedCount}건)");

                if (!result.HasMore) break;
                page++;
                await Task.Delay(PageDelay, ct);
            }

            job.Complete();
            await categoryJobs.SaveAsync(ct);
            Notify(job, $"카테고리 수집 완료 — {job.QueuedCount}건 요청, {job.SkippedCount}건 중복 제외");

            await bus.PublishAsync(new CategoryCrawlCompleted
            {
                CategoryJobId = job.Id,
                QueuedCount = job.QueuedCount,
                SkippedCount = job.SkippedCount,
                TenantId = job.TenantId,
                CorrelationId = job.Id,
                CausationId = @event.EventId,
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "카테고리 수집 실패 (Job {JobId})", job.Id);
            job.Fail(ex.Message);
            await categoryJobs.SaveAsync(ct);
            Notify(job, $"카테고리 수집 실패: {ex.Message}");
        }
    }

    private void Notify(CategoryCollectJob job, string message) =>
        notifier.Notify(new PipelineNotification(
            job.Id, "CategoryCrawl", job.State.ToString(), null, message, DateTimeOffset.UtcNow));
}

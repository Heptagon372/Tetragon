using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Domain.Events;
using Tetragon.Domain.Sourcing;

namespace Tetragon.Api.Endpoints;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/jobs").WithTags("Jobs");

        group.MapGet("/", async (int? limit, IScrapeJobRepository jobs, CancellationToken ct) =>
        {
            var items = await jobs.RecentAsync(Math.Clamp(limit ?? 100, 1, 500), ct);
            return Results.Ok(new { items = items.Select(JobDto.Of), stages = JobDto.Stages });
        })
        .WithSummary("최근 수집 Job 목록 (파이프라인 진행 상태)");

        group.MapGet("/{id:guid}", async (Guid id, IScrapeJobRepository jobs, CancellationToken ct) =>
        {
            var job = await jobs.FindAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(JobDto.Of(job));
        })
        .WithSummary("Job 단건 조회");

        // DLQ 재처리 (설계서 6.2 — 운영 대시보드 재처리 UI)
        group.MapPost("/{id:guid}/retry", async (
            Guid id, IScrapeJobRepository jobs, IEventBus bus, CancellationToken ct) =>
        {
            var job = await jobs.FindAsync(id, ct);
            if (job is null) return Results.NotFound();
            if (job.State is not (JobState.Failed or JobState.DeadLettered))
                return Results.BadRequest(new { error = $"재처리 가능한 상태가 아닙니다: {job.State}" });

            // 등록 단계에서 실패한 Job은 수집을 다시 하면 안 된다.
            // 상품은 이미 있으므로 다시 긁으면 같은 상품이 하나 더 생긴다.
            if (job.Stage == JobStage.Listing && job.ProductId is Guid productId && job.HasAutoList)
            {
                job.MarkStage(JobStage.Listing, JobState.Running);
                await jobs.SaveAsync(ct);
                await bus.PublishAsync(new ListingRequested
                {
                    JobId = job.Id,
                    ProductId = productId,
                    MarketCodes = job.AutoListMarkets.ToList(),
                    CorrelationId = job.Id,
                }, ct);
                return Results.Accepted($"/api/v1/jobs/{job.Id}", JobDto.Of(job));
            }

            job.MarkStage(JobStage.Queued, JobState.Pending);
            await jobs.SaveAsync(ct);
            await bus.PublishAsync(new ScrapeRequested
            {
                JobId = job.Id,
                Url = job.Url,
                PricingPolicyId = job.PricingPolicyId,
                CorrelationId = job.Id,
            }, ct);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", JobDto.Of(job));
        })
        .WithSummary("실패/DLQ Job 재처리");
    }
}

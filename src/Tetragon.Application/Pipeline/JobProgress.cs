using Tetragon.Application.Ports;
using Tetragon.Domain.Sourcing;

namespace Tetragon.Application.Pipeline;

/// <summary>Job 단계 갱신 + SSE 알림을 한 번에 처리하는 헬퍼.</summary>
public sealed class JobProgress(IScrapeJobRepository jobs, IPipelineNotifier notifier)
{
    public async Task<ScrapeJob?> AdvanceAsync(Guid jobId, JobStage stage, JobState state, string? message, CancellationToken ct)
    {
        var job = await jobs.FindAsync(jobId, ct);
        if (job is null) return null;
        job.MarkStage(stage, state);
        await jobs.SaveAsync(ct);
        notifier.Notify(new PipelineNotification(jobId, stage.ToString(), state.ToString(), job.ProductId, message, DateTimeOffset.UtcNow));
        return job;
    }

    public async Task FailAsync(Guid jobId, string error, CancellationToken ct, bool permanent = false)
    {
        var job = await jobs.FindAsync(jobId, ct);
        if (job is null) return;
        if (permanent) job.RecordPermanentFailure(error);
        else job.RecordFailure(error);
        await jobs.SaveAsync(ct);
        notifier.Notify(new PipelineNotification(jobId, job.Stage.ToString(), job.State.ToString(), job.ProductId, error, DateTimeOffset.UtcNow));
    }

    /// <summary>마켓 등록 실패 — 수집 재시도가 아니라 등록 재시도로 이어지도록 단계를 남긴다.</summary>
    public async Task ListingFailedAsync(Guid jobId, string error, CancellationToken ct)
    {
        var job = await jobs.FindAsync(jobId, ct);
        if (job is null) return;
        job.RecordListingFailure(error);
        await jobs.SaveAsync(ct);
        notifier.Notify(new PipelineNotification(
            jobId, job.Stage.ToString(), job.State.ToString(), job.ProductId, error, DateTimeOffset.UtcNow));
    }

    public async Task<ScrapeJob?> CompleteAsync(Guid jobId, string? message, CancellationToken ct)
    {
        var job = await jobs.FindAsync(jobId, ct);
        if (job is null) return null;
        job.Complete();
        await jobs.SaveAsync(ct);
        notifier.Notify(new PipelineNotification(jobId, job.Stage.ToString(), job.State.ToString(), job.ProductId, message, DateTimeOffset.UtcNow));
        return job;
    }
}

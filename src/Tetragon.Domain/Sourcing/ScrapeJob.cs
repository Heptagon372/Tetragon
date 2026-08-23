using Tetragon.SharedKernel;

namespace Tetragon.Domain.Sourcing;

/// <summary>
/// 수집~등록 파이프라인 Job (설계서 5.1 작업 모델 + §8 Job 리소스).
/// CorrelationId로 전체 파이프라인 단계를 추적한다.
/// </summary>
public sealed class ScrapeJob : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string Url { get; private set; } = "";
    public string? SupplierCode { get; private set; }
    public Guid? ProductId { get; private set; }
    public Guid? PricingPolicyId { get; private set; }

    /// <summary>
    /// 컴플라이언스 통과 즉시 등록할 마켓 코드들.
    /// 비어 있으면 기존 동작 그대로 — 사용자가 상품 화면에서 직접 등록을 눌러야 한다.
    /// 링크 하나로 쿠팡까지 보내는 흐름(빠른 등록)이 이 값을 채운다.
    /// </summary>
    public IReadOnlyList<string> AutoListMarkets { get; private set; } = [];

    public JobStage Stage { get; private set; } = JobStage.Queued;
    public JobState State { get; private set; } = JobState.Pending;
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private ScrapeJob() { }

    public static ScrapeJob Create(
        string tenantId, string url, Guid? pricingPolicyId,
        IReadOnlyList<string>? autoListMarkets = null)
    {
        return new ScrapeJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Url = url,
            PricingPolicyId = pricingPolicyId,
            AutoListMarkets = autoListMarkets?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? [],
        };
    }

    /// <summary>자동 등록이 걸린 Job인지 — 컴플라이언스 통과 후 바로 마켓으로 넘길지 판단한다.</summary>
    public bool HasAutoList => AutoListMarkets.Count > 0;

    public void MarkStage(JobStage stage, JobState state = JobState.Running)
    {
        Stage = stage;
        State = state;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void AttachProduct(Guid productId, string supplierCode)
    {
        ProductId = productId;
        SupplierCode = supplierCode;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordFailure(string error)
    {
        Attempts++;
        LastError = error;
        State = Attempts >= MaxAttempts ? JobState.DeadLettered : JobState.Failed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>재시도해도 소용없는 실패 (삭제된 상품 등) — 즉시 DLQ로 보낸다.</summary>
    public void RecordPermanentFailure(string error)
    {
        Attempts++;
        LastError = error;
        State = JobState.DeadLettered;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// 마켓 등록 단계에서의 실패. 수집은 이미 끝났으므로 재시도는 등록만 다시 하면 된다.
    /// 수집 실패와 구분하지 않으면 재시도가 상품을 한 번 더 수집해 중복이 생긴다.
    /// </summary>
    public void RecordListingFailure(string error)
    {
        Stage = JobStage.Listing;
        State = JobState.Failed;
        LastError = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Complete()
    {
        Stage = JobStage.Completed;
        State = JobState.Succeeded;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public bool CanRetry => State == JobState.Failed && Attempts < MaxAttempts;
    public const int MaxAttempts = 3; // 지수 백오프 재시도 3회 후 DLQ (설계서 5.1)
}

/// <summary>파이프라인 단계 (설계서 6.1 메인 파이프라인과 1:1).</summary>
public enum JobStage
{
    Queued,
    Collecting,   // 스크래핑
    Normalizing,  // 표준화
    Enriching,    // 번역/정제
    Pricing,      // 가격 계산
    Compliance,   // 금지어/제한 검사
    ReadyToList,  // 등록 대기 (사용자 트리거 or 자동)
    Listing,      // 마켓 등록 중
    Completed,
}

public enum JobState
{
    Pending,
    Running,
    Succeeded,
    Failed,       // 재시도 가능
    DeadLettered, // DLQ — 대시보드 노출, 수동 재처리 (설계서 6.2)
    Blocked,      // 컴플라이언스 차단 — 사용자 확인 대기
}

using Tetragon.SharedKernel;

namespace Tetragon.Domain.Events;

// ─── 메인 파이프라인 통합 이벤트 (설계서 6.1) ───────────────────────────
// scrape.requested → ProductCollected → ProductNormalized → ProductEnriched
//   → PriceCalculated → ComplianceChecked → listing.requested → MarketplaceRegistered

/// <summary>수집 요청 커맨드 (scrape.requested 큐).</summary>
public sealed record ScrapeRequested : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public required string Url { get; init; }
    public Guid? PricingPolicyId { get; init; }
}

/// <summary>카테고리 대량 수집 요청 커맨드 (category.crawl.requested 큐).</summary>
public sealed record CategoryCrawlRequested : IntegrationEvent
{
    public required Guid CategoryJobId { get; init; }
}

/// <summary>카테고리 훑기 완료 — 개별 ScrapeJob들은 이미 큐에 적재됨.</summary>
public sealed record CategoryCrawlCompleted : IntegrationEvent
{
    public required Guid CategoryJobId { get; init; }
    public required int QueuedCount { get; init; }
    public required int SkippedCount { get; init; }
}

public sealed record ProductCollected : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProductId { get; init; }
    public required string SupplierCode { get; init; }
}

public sealed record ProductNormalized : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProductId { get; init; }
}

public sealed record ProductEnriched : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProductId { get; init; }
}

public sealed record PriceCalculated : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProductId { get; init; }
    public required Guid PolicyId { get; init; }
}

public sealed record ComplianceChecked : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public required Guid ProductId { get; init; }
    public required string Verdict { get; init; } // Pass / Warn / Block
}

/// <summary>등록 요청 커맨드 (listing.requested 큐). 사용자 트리거 또는 자동.</summary>
public sealed record ListingRequested : IntegrationEvent
{
    public required Guid ProductId { get; init; }
    public required List<string> MarketCodes { get; init; }
    public Guid? JobId { get; init; }
}

public sealed record MarketplaceRegistered : IntegrationEvent
{
    public required Guid ProductId { get; init; }
    public required Guid ListingId { get; init; }
    public required string MarketCode { get; init; }
    public required bool Success { get; init; }
    public string? MarketItemId { get; init; }
    public string? Error { get; init; }
}

/// <summary>파이프라인 실패 (재시도 소진 → DLQ, 설계서 6.2).</summary>
public sealed record PipelineFailed : IntegrationEvent
{
    public required Guid JobId { get; init; }
    public Guid? ProductId { get; init; }
    public required string Stage { get; init; }
    public required string Error { get; init; }
    public required bool DeadLettered { get; init; }
}

// ─── 재고 동기화 (설계서 5.7) ───────────────────────────────────────────

public sealed record InventoryCheckRequested : IntegrationEvent
{
    public required Guid ProductId { get; init; }
}

public sealed record StockChanged : IntegrationEvent
{
    public required Guid ProductId { get; init; }
    public required bool IsAvailable { get; init; }
    public required Dictionary<string, int> StockByVariantId { get; init; }
}

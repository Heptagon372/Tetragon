using Tetragon.SharedKernel;

namespace Tetragon.Domain.Sourcing;

/// <summary>
/// 카테고리 단위 대량 수집 작업.
/// 카테고리를 훑어 상품 URL을 모으고, 각 URL마다 개별 ScrapeJob을 생성한다.
/// (수집 자체는 기존 파이프라인이 그대로 처리 — 이 애그리거트는 "훑기"만 책임진다)
/// </summary>
public sealed class CategoryCollectJob : AggregateRoot<Guid>
{
    public string TenantId { get; private set; } = Tenant.Default;
    public string SupplierCode { get; private set; } = "";
    public string? CategoryCode { get; private set; }
    public string? CategoryName { get; private set; }
    public string? Keyword { get; private set; }
    /// <summary>수집할 최대 상품 수 (무한 크롤링 방지).</summary>
    public int MaxProducts { get; private set; }
    public decimal? MinPrice { get; private set; }
    public decimal? MaxPrice { get; private set; }
    public Guid? PricingPolicyId { get; private set; }

    public CategoryJobState State { get; private set; } = CategoryJobState.Pending;
    /// <summary>현재까지 훑은 페이지 수.</summary>
    public int PagesCrawled { get; private set; }
    /// <summary>목록에서 발견한 상품 수.</summary>
    public int FoundCount { get; private set; }
    /// <summary>실제로 ScrapeJob을 만든 수 (중복 제외).</summary>
    public int QueuedCount { get; private set; }
    /// <summary>이미 수집한 적 있어 건너뛴 수.</summary>
    public int SkippedCount { get; private set; }
    public string? LastError { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private CategoryCollectJob() { }

    public static CategoryCollectJob Create(
        string tenantId, string supplierCode, string? categoryCode, string? categoryName,
        string? keyword, int maxProducts, decimal? minPrice, decimal? maxPrice, Guid? pricingPolicyId)
    {
        if (string.IsNullOrWhiteSpace(categoryCode) && string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("카테고리 코드 또는 검색어 중 하나는 반드시 필요합니다.");

        return new CategoryCollectJob
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SupplierCode = supplierCode,
            CategoryCode = categoryCode,
            CategoryName = categoryName,
            Keyword = keyword,
            MaxProducts = Math.Clamp(maxProducts, 1, MaxAllowedProducts),
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            PricingPolicyId = pricingPolicyId,
        };
    }

    /// <summary>한 번의 카테고리 수집으로 만들 수 있는 상품 수 상한.</summary>
    public const int MaxAllowedProducts = 1000;

    public void Start()
    {
        State = CategoryJobState.Crawling;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void RecordPage(int found, int queued, int skipped)
    {
        PagesCrawled++;
        FoundCount += found;
        QueuedCount += queued;
        SkippedCount += skipped;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Complete()
    {
        State = CategoryJobState.Completed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string error)
    {
        State = CategoryJobState.Failed;
        LastError = error;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>상한에 도달했는지 (더 훑지 말아야 하는지).</summary>
    public bool ReachedLimit => QueuedCount + SkippedCount >= MaxProducts;
}

public enum CategoryJobState
{
    Pending,
    Crawling,
    Completed,
    Failed,
}

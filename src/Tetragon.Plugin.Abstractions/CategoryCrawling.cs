namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 카테고리/검색 단위로 상품 목록을 훑는 능력.
/// 모든 공급처가 지원하지는 않으므로 ISupplierPlugin과 분리한다
/// (예: 11번가는 상세 페이지는 서버렌더지만 카테고리 목록은 CSR이라 미지원).
/// 지원하는 플러그인만 추가로 구현하고, 레지스트리가 이 인터페이스로 필터링한다.
/// </summary>
public interface ICategoryCrawler
{
    /// <summary>이 크롤러가 속한 공급처 코드 (ISupplierPlugin.Code와 동일).</summary>
    string SupplierCode { get; }

    /// <summary>카테고리 트리 조회 (UI에서 고르게 하거나 엑셀로 내보내기 위함).</summary>
    Task<IReadOnlyList<SupplierCategory>> GetCategoriesAsync(string? parentCode, CancellationToken ct);

    /// <summary>
    /// 카테고리/키워드로 상품 참조 목록을 조회한다.
    /// 실제 상세 수집은 ISupplierPlugin.CollectAsync가 담당한다 (책임 분리).
    /// </summary>
    Task<CategoryCrawlPage> CrawlAsync(CategoryCrawlRequest request, CancellationToken ct);
}

/// <summary>공급처 카테고리 노드.</summary>
/// <param name="IsSelectable">
/// 이 카테고리로 바로 상품을 조회할 수 있는지.
/// 공급처에 따라 상위 분류는 조회 조건으로 쓸 수 없다
/// (예: 도매꾹은 대분류 <c>XX_00_00_00_00</c>를 거부하고 중분류 이하만 받는다).
/// false면 UI는 선택 대신 하위 탐색만 허용한다.
/// </param>
public sealed record SupplierCategory(
    string Code,
    string Name,
    string? ParentCode = null,
    bool HasChildren = false,
    string? FullPath = null,
    bool IsSelectable = true);

public sealed record CategoryCrawlRequest
{
    /// <summary>공급처 카테고리 코드. Keyword와 함께 쓰거나 둘 중 하나만 써도 된다.</summary>
    public string? CategoryCode { get; init; }
    public string? Keyword { get; init; }
    /// <summary>1부터 시작.</summary>
    public int Page { get; init; } = 1;
    /// <summary>페이지당 개수 (공급처 상한에 맞춰 클램프됨).</summary>
    public int PageSize { get; init; } = 50;
    /// <summary>최저/최고가 필터 (공급처 통화 기준). null이면 미적용.</summary>
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public string TenantId { get; init; } = "default";
}

/// <summary>카테고리 조회 1페이지 결과.</summary>
public sealed record CategoryCrawlPage
{
    public required IReadOnlyList<CrawledProductRef> Items { get; init; }
    public int Page { get; init; }
    public int TotalCount { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>
/// 목록에서 얻은 상품 참조. 목록 API가 주는 만큼만 채워지며,
/// 전체 데이터는 이후 CollectAsync로 수집한다.
/// </summary>
public sealed record CrawledProductRef
{
    public required string SourceProductId { get; init; }
    public required string Url { get; init; }
    public string? Title { get; init; }
    public decimal? Price { get; init; }
    public string? Currency { get; init; }
    public string? ThumbnailUrl { get; init; }
}

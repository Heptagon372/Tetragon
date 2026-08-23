namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 카테고리/검색 목록을 훑어 **품절·저재고 상품만 골라내는** 능력.
///
/// 상세 페이지를 상품마다 여는 대신 목록 페이지 하나(1 fetch)에서 78개가량의 재고 상태를
/// 한 번에 읽는다 — 요청 수를 최소화해 IP 차단(Akamai) 위험을 낮추기 위함이다.
/// 쿠팡처럼 목록 카드에 재고 신호가 있는 공급처만 구현한다.
/// </summary>
public interface IStockScanner
{
    /// <summary>이 스캐너가 속한 공급처 코드 (ISupplierPlugin.Code와 동일).</summary>
    string SupplierCode { get; }

    /// <summary>목록 1페이지를 훑어 각 상품의 재고 상태를 판정한다.</summary>
    Task<StockScanPage> ScanStockAsync(StockScanRequest request, CancellationToken ct);
}

/// <summary>재고 스캔 요청 — 카테고리 코드 또는 검색어 중 하나(또는 둘 다).</summary>
public sealed record StockScanRequest
{
    public string? CategoryCode { get; init; }
    public string? Keyword { get; init; }
    /// <summary>1부터 시작.</summary>
    public int Page { get; init; } = 1;
    public string TenantId { get; init; } = "default";
}

/// <summary>재고 스캔 1페이지 결과.</summary>
public sealed record StockScanPage
{
    public required IReadOnlyList<StockScanItem> Items { get; init; }
    public int Page { get; init; }
    /// <summary>다음 페이지가 있을 가능성 (목록에 상품이 있었으면 true).</summary>
    public bool HasMore { get; init; }
}

/// <summary>목록에서 판정한 상품 한 건의 재고 상태.</summary>
public sealed record StockScanItem
{
    public required string SourceProductId { get; init; }
    public required string Url { get; init; }
    public string? Name { get; init; }
    public decimal? Price { get; init; }
    public string Currency { get; init; } = "KRW";
    public required StockStatus Status { get; init; }
    /// <summary>저재고일 때 남은 수량 ("단 N개 남음"). 그 외에는 null.</summary>
    public int? Remaining { get; init; }
}

/// <summary>목록에서 읽어낸 재고 상태.</summary>
public enum StockStatus
{
    /// <summary>정상 판매 중.</summary>
    InStock,
    /// <summary>"단 N개 남음" — 곧 소진될 저재고.</summary>
    LowStock,
    /// <summary>품절/일시품절.</summary>
    SoldOut,
}

/// <summary>DI에 등록된 재고 스캐너를 공급처 코드로 해석.</summary>
public interface IStockScannerRegistry
{
    IStockScanner? Resolve(string supplierCode);
    IReadOnlyList<IStockScanner> All { get; }
}

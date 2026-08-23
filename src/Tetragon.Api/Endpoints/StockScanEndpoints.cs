using Tetragon.Plugin.Abstractions;

namespace Tetragon.Api.Endpoints;

/// <summary>
/// 쿠팡 품절·저재고 스캐너 (설계 외 확장).
///
/// 카테고리 코드나 검색어로 목록을 여러 페이지 훑어 **품절/저재고 상품만** 골라낸다.
/// 상품마다 상세를 열지 않고 목록 페이지에서 재고 신호를 읽으므로 요청은 페이지 수만큼이며,
/// 페이지 사이에 지연을 두어 IP 차단(Akamai) 위험을 낮춘다.
/// </summary>
public static class StockScanEndpoints
{
    // 목록 한 페이지에 상품이 78개가량이라 몇 페이지면 충분하다. 밴 방지를 위해 상한을 둔다.
    private const int MaxPages = 10;

    public static void MapStockScanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/stock-scan").WithTags("StockScan");

        // POST /api/v1/stock-scan/{supplier}
        group.MapPost("/{supplier}", async (
            string supplier,
            StockScanRequestDto request,
            IStockScannerRegistry registry,
            ILoggerFactory logs,
            CancellationToken ct) =>
        {
            var scanner = registry.Resolve(supplier);
            if (scanner is null)
                return Results.BadRequest(new { error = $"'{supplier}' 공급처는 재고 스캔을 지원하지 않습니다." });

            if (string.IsNullOrWhiteSpace(request.Keyword) && string.IsNullOrWhiteSpace(request.CategoryCode))
                return Results.BadRequest(new { error = "keyword 또는 categoryCode가 필요합니다." });

            var pages = Math.Clamp(request.MaxPages ?? 3, 1, MaxPages);
            var log = logs.CreateLogger("StockScan");

            var all = new List<StockScanItem>();
            var seen = new HashSet<string>();
            var scannedPages = 0;
            string? error = null;
            try
            {
                for (var page = 1; page <= pages; page++)
                {
                    // 페이지 사이 정중한 지연 (사이드카 예열과 별개로 목록 요청 간격 확보)
                    if (page > 1) await Task.Delay(1500, ct);

                    var result = await scanner.ScanStockAsync(new StockScanRequest
                    {
                        Keyword = request.Keyword,
                        CategoryCode = request.CategoryCode,
                        Page = page,
                    }, ct);
                    scannedPages++;

                    foreach (var item in result.Items)
                        if (seen.Add(item.SourceProductId))
                            all.Add(item);

                    if (!result.HasMore) break;
                }
            }
            catch (Exception ex)
            {
                // 일부 페이지에서 차단/오류가 나도 그때까지 모은 결과는 돌려준다.
                error = ex.Message;
                log.LogWarning(ex, "재고 스캔 중 중단 — 부분 결과 반환");
            }

            var flagged = all
                .Where(i => request.IncludeInStock || i.Status != StockStatus.InStock)
                .OrderBy(i => i.Status == StockStatus.SoldOut ? 0 : i.Status == StockStatus.LowStock ? 1 : 2)
                .ThenBy(i => i.Remaining ?? int.MaxValue)
                .Select(i => new StockScanItemDto(
                    i.SourceProductId, i.Url, i.Name, i.Price, i.Currency,
                    i.Status.ToString(), i.Remaining))
                .ToList();

            return Results.Ok(new StockScanResponseDto(
                supplier,
                request.Keyword,
                request.CategoryCode,
                scannedPages,
                all.Count,
                all.Count(i => i.Status == StockStatus.SoldOut),
                all.Count(i => i.Status == StockStatus.LowStock),
                flagged,
                error));
        })
        .WithSummary("쿠팡 품절·저재고 스캔")
        .WithDescription("카테고리/검색어로 목록을 훑어 품절·저재고 상품을 골라낸다. maxPages로 훑을 페이지 수를 정한다.");
    }
}

public sealed record StockScanRequestDto
{
    public string? Keyword { get; init; }
    public string? CategoryCode { get; init; }
    /// <summary>훑을 페이지 수 (기본 3, 최대 10).</summary>
    public int? MaxPages { get; init; }
    /// <summary>정상 재고 상품도 포함할지 (기본 false — 품절·저재고만).</summary>
    public bool IncludeInStock { get; init; }
}

public sealed record StockScanResponseDto(
    string Supplier,
    string? Keyword,
    string? CategoryCode,
    int ScannedPages,
    int TotalScanned,
    int SoldOutCount,
    int LowStockCount,
    IReadOnlyList<StockScanItemDto> Items,
    string? Error);

public sealed record StockScanItemDto(
    string SourceProductId,
    string Url,
    string? Name,
    decimal? Price,
    string Currency,
    string Status,
    int? Remaining);

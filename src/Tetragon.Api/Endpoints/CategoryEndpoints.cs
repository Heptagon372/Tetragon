using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.UseCases;
using Tetragon.Domain.Sourcing;
using Tetragon.Infrastructure.Excel;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Api.Endpoints;

public static class CategoryEndpoints
{
    public static void MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/categories").WithTags("Categories");

        // 카테고리 수집을 지원하는 공급처 목록
        group.MapGet("/suppliers", (ICategoryCrawlerRegistry crawlers, ISupplierPluginRegistry suppliers) =>
            Results.Ok(crawlers.All.Select(c =>
            {
                var plugin = suppliers.Resolve(c.SupplierCode);
                return new
                {
                    supplierCode = c.SupplierCode,
                    displayName = plugin?.DisplayName ?? c.SupplierCode,
                    isLive = plugin?.IsLive ?? false,
                    isAvailable = plugin?.IsAvailable ?? false,
                };
            })))
        .WithSummary("카테고리 수집을 지원하는 공급처");

        // 카테고리 트리 조회
        group.MapGet("/{supplierCode}", async (
            string supplierCode, string? parent,
            ICategoryCrawlerRegistry crawlers, CancellationToken ct) =>
        {
            var crawler = crawlers.Resolve(supplierCode);
            if (crawler is null)
                return Results.NotFound(new { error = $"'{supplierCode}'는 카테고리 수집을 지원하지 않습니다." });
            try
            {
                var categories = await crawler.GetCategoriesAsync(parent, ct);
                return Results.Ok(categories.Select(c => new
                {
                    c.Code, c.Name, c.ParentCode, c.HasChildren, c.FullPath, c.IsSelectable,
                }));
            }
            catch (PermanentScrapeException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        })
        .WithSummary("공급처 카테고리 트리 조회");

        // 카테고리 미리보기 (수집 전에 어떤 상품이 나오는지 확인)
        group.MapPost("/{supplierCode}/preview", async (
            string supplierCode, CategoryPreviewRequest request,
            ICategoryCrawlerRegistry crawlers, CancellationToken ct) =>
        {
            var crawler = crawlers.Resolve(supplierCode);
            if (crawler is null)
                return Results.NotFound(new { error = $"'{supplierCode}'는 카테고리 수집을 지원하지 않습니다." });
            try
            {
                var page = await crawler.CrawlAsync(new CategoryCrawlRequest
                {
                    CategoryCode = request.CategoryCode,
                    Keyword = request.Keyword,
                    Page = 1,
                    PageSize = Math.Clamp(request.PageSize ?? 20, 1, 100),
                    MinPrice = request.MinPrice,
                    MaxPrice = request.MaxPrice,
                }, ct);
                return Results.Ok(new
                {
                    items = page.Items.Select(i => new
                    {
                        i.SourceProductId, i.Url, i.Title, i.Price, i.Currency, i.ThumbnailUrl,
                    }),
                    page.TotalCount,
                    page.HasMore,
                });
            }
            catch (PermanentScrapeException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.Problem(ex.Message); }
        })
        .WithSummary("카테고리 상품 미리보기 (수집하지 않음)");

        // 카테고리 대량 수집 시작
        group.MapPost("/{supplierCode}/collect", async (
            string supplierCode, CategoryCollectRequest request,
            CollectCategoryUseCase useCase, CancellationToken ct) =>
        {
            try
            {
                var job = await useCase.ExecuteAsync(
                    supplierCode, request.CategoryCode, request.CategoryName, request.Keyword,
                    request.MaxProducts ?? 100, request.MinPrice, request.MaxPrice,
                    request.PricingPolicyId, ct);
                return Results.Accepted($"/api/v1/categories/jobs/{job.Id}", CategoryJobDto.Of(job));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("카테고리 단위 대량 수집 시작");

        // 카테고리 수집 작업 현황
        group.MapGet("/jobs", async (int? limit, ICategoryCollectJobRepository jobs, CancellationToken ct) =>
        {
            var items = await jobs.RecentAsync(Math.Clamp(limit ?? 50, 1, 200), ct);
            return Results.Ok(new { items = items.Select(CategoryJobDto.Of) });
        })
        .WithSummary("카테고리 수집 작업 목록");

        group.MapGet("/jobs/{id:guid}", async (
            Guid id, ICategoryCollectJobRepository jobs, CancellationToken ct) =>
        {
            var job = await jobs.FindAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(CategoryJobDto.Of(job));
        })
        .WithSummary("카테고리 수집 작업 조회");
    }
}

public static class CategoryJobDto
{
    public static object Of(CategoryCollectJob j) => new
    {
        id = j.Id,
        supplierCode = j.SupplierCode,
        categoryCode = j.CategoryCode,
        categoryName = j.CategoryName,
        keyword = j.Keyword,
        state = j.State.ToString(),
        maxProducts = j.MaxProducts,
        pagesCrawled = j.PagesCrawled,
        foundCount = j.FoundCount,
        queuedCount = j.QueuedCount,
        skippedCount = j.SkippedCount,
        lastError = j.LastError,
        createdAt = j.CreatedAt,
        updatedAt = j.UpdatedAt,
    };
}

using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Application.UseCases;
using Tetragon.Domain.Catalog;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Api.Endpoints;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/products").WithTags("Products");

        // POST /api/v1/products/collect — URL 대량 수집 (설계서 §8, 202 Accepted + Job)
        group.MapPost("/collect", async (
            CollectRequest request,
            CollectProductsUseCase useCase,
            CancellationToken ct) =>
        {
            var urls = request.Urls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            if (request.Url is not null) urls.Add(request.Url);
            if (urls.Count == 0)
                return Results.BadRequest(new { error = "url 또는 urls가 필요합니다." });

            var invalid = urls.Where(u => !Uri.TryCreate(u.Trim(), UriKind.Absolute, out _)).ToList();
            var jobIds = await useCase.ExecuteAsync(urls, request.PricingPolicyId, ct);

            return Results.Accepted($"/api/v1/jobs", new
            {
                jobIds,
                accepted = jobIds.Count,
                rejected = invalid,
            });
        })
        .WithSummary("상품 URL 대량 수집 요청")
        .WithDescription("URL 목록을 받아 수집 Job을 생성하고 파이프라인을 시작합니다. 202 + jobIds 반환.");

        // POST /api/v1/products/preview — 링크 하나를 긁어서 등록될 내용을 미리 본다 (저장 안 함)
        group.MapPost("/preview", async (
            LinkPreviewRequest request,
            LinkPreviewService preview,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Url))
                return Results.BadRequest(new { error = "url이 필요합니다." });
            try
            {
                return Results.Ok(await preview.InspectAsync(request.Url, request.PricingPolicyId, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or PermanentScrapeException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithSummary("링크 미리보기 — 쿠팡에 등록될 내용을 저장 없이 확인");

        // POST /api/v1/products/quick-list — 링크 → 수집 → 마켓 등록까지 한 번에
        group.MapPost("/quick-list", async (
            QuickListRequest request,
            QuickListUseCase useCase,
            CancellationToken ct) =>
        {
            var urls = request.Urls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            if (request.Url is not null) urls.Add(request.Url);
            if (urls.Count == 0)
                return Results.BadRequest(new { error = "url 또는 urls가 필요합니다." });

            var markets = request.MarketCodes?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? [];
            if (markets.Count == 0)
                return Results.BadRequest(new { error = "보낼 마켓(marketCodes)이 필요합니다." });

            var entries = await useCase.ExecuteAsync(
                urls, markets, request.PricingPolicyId, request.ReuseExisting ?? true, ct);

            return Results.Accepted("/api/v1/jobs", new
            {
                markets,
                queued = entries.Count(e => e.Outcome == QuickListOutcome.Queued),
                reused = entries.Count(e => e.Outcome == QuickListOutcome.ExistingProduct),
                entries,
            });
        })
        .WithSummary("링크 → 수집 → 마켓 자동 등록")
        .WithDescription("도매꾹/도매매 상품 링크를 받아 수집하고, 컴플라이언스를 통과하면 지정한 마켓에 바로 등록합니다.");

        // GET /api/v1/products
        group.MapGet("/", async (
            string? status, string? keyword, int? page, int? pageSize,
            IProductRepository products, CancellationToken ct) =>
        {
            var currentPage = page is null or <= 0 ? 1 : page.Value;
            var size = pageSize is null or <= 0 or > 200 ? 50 : pageSize.Value;
            var (items, total) = await products.SearchAsync(status, keyword, currentPage, size, ct);
            return Results.Ok(new
            {
                items = items.Select(ProductDto.Summary),
                total,
                page = currentPage,
                pageSize = size,
            });
        })
        .WithSummary("상품 목록 조회");

        // GET /api/v1/products/{id}
        group.MapGet("/{id:guid}", async (
            Guid id,
            IProductRepository products,
            IPricingPolicyRepository policies,
            IComplianceRepository compliance,
            IListingRepository listings,
            CancellationToken ct) =>
        {
            var product = await products.FindAsync(id, ct);
            if (product is null) return Results.NotFound();

            var calculations = await policies.CalculationsForProductAsync(id, ct);
            var complianceResult = await compliance.LatestResultAsync(id, ct);
            var productListings = await listings.ByProductAsync(id, ct);

            return Results.Ok(ProductDto.Detail(product, calculations, complianceResult, productListings));
        })
        .WithSummary("상품 상세 조회 (가격 계산 내역 + 컴플라이언스 + 등록 현황 포함)");

        // 공급처 상세설명 HTML — 용량이 커서 상세 응답에 넣지 않고 별도로 준다
        group.MapGet("/{id:guid}/detail-html", async (
            Guid id, IProductRepository products, CancellationToken ct) =>
        {
            var product = await products.FindAsync(id, ct);
            if (product is null) return Results.NotFound();

            var logistics = ConsignmentLogistics.FromAttributes(product.Attributes, product.BasePrice.Currency);
            if (!logistics.DetailImagesAllowed)
                return Results.Ok(new
                {
                    allowed = false,
                    html = (string?)null,
                    images = Array.Empty<string>(),
                    reason = "공급처가 상세설명 이미지 재사용을 허용하지 않았습니다.",
                });

            var html = logistics.DetailHtml ?? "";
            var images = System.Text.RegularExpressions.Regex
                .Matches(html, """<img[^>]+src=["']([^"']+)["']""",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                // HTML 속성값이라 &amp; 등이 인코딩돼 있다. 디코딩하지 않으면 URL이 깨진다.
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
                .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .ToArray();

            return Results.Ok(new { allowed = true, html, images, reason = (string?)null });
        })
        .WithSummary("공급처 상세설명 HTML/이미지 (라이선스 허용 시에만)");

        // PATCH /api/v1/products/{id} — 사용자 수동 편집
        group.MapPatch("/{id:guid}", async (
            Guid id, ProductEditRequest request,
            IProductRepository products, CancellationToken ct) =>
        {
            var product = await products.FindAsync(id, ct);
            if (product is null) return Results.NotFound();
            product.EditManually(request.Name, request.Description);
            await products.SaveAsync(ct);
            return Results.Ok(ProductDto.Summary(product));
        })
        .WithSummary("상품 수동 편집 (상품명/상세)");

        // POST /api/v1/products/{id}/resume — 컴플라이언스 차단 해제
        group.MapPost("/{id:guid}/resume", async (
            Guid id, ResumeBlockedProductUseCase useCase, CancellationToken ct) =>
        {
            try
            {
                await useCase.ExecuteAsync(id, ct);
                return Results.Ok(new { resumed = true });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("차단된 상품 확인 후 등록 재개");

        // GET /api/v1/products/{id}/raw — 원본 스크래핑 JSON (디버깅/감사)
        group.MapGet("/{id:guid}/raw", async (
            Guid id, IRawProductRepository raws, CancellationToken ct) =>
        {
            var record = await raws.FindByProductAsync(id, ct);
            return record is null
                ? Results.NotFound()
                : Results.Text(record.RawJson, "application/json");
        })
        .WithSummary("스크래핑 원본 JSON 조회");
    }
}

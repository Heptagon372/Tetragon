using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Application.UseCases;
using Tetragon.Infrastructure.Excel;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Api.Endpoints;

public static class ExcelEndpoints
{
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapExcelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/excel").WithTags("Excel");

        // 사용 가능한 마켓 양식 목록
        group.MapGet("/templates", (ExcelWorkbookService excel) =>
            Results.Ok(excel.Templates.Select(t => new
            {
                t.MarketCode,
                t.DisplayName,
                t.UploadGuide,
                columnCount = t.Columns.Count,
                requiredColumns = t.Columns.Where(c => c.IsRequired).Select(c => c.Header),
            })))
        .WithSummary("마켓별 대량등록 엑셀 양식 목록");

        // 상품 → 마켓 대량등록 엑셀 생성
        group.MapPost("/listings/{marketCode}", async (
            string marketCode, ExcelExportRequest request,
            ExcelWorkbookService excel,
            IProductRepository products,
            ListingPayloadBuilder payloadBuilder,
            ICredentialStore credentials,
            CancellationToken ct) =>
        {
            var template = excel.FindTemplate(marketCode);
            if (template is null)
                return Results.NotFound(new { error = $"'{marketCode}' 마켓 엑셀 양식이 없습니다." });

            // 대상 상품 결정 — ID 지정이 없으면 등록 가능 상태 전체
            List<Domain.Catalog.Product> targets = [];
            if (request.ProductIds is { Count: > 0 })
            {
                foreach (var id in request.ProductIds)
                    if (await products.FindAsync(id, ct) is { } product) targets.Add(product);
            }
            else
            {
                var (items, _) = await products.SearchAsync(
                    request.Status ?? nameof(Domain.Catalog.ProductStatus.Ready), null, 1, 1000, ct);
                targets.AddRange(items);
            }

            if (targets.Count == 0)
                return Results.BadRequest(new { error = "내보낼 상품이 없습니다. 가격 계산이 끝난 상품을 선택하세요." });

            // 마켓 설정값 (카테고리 코드, 배송비 등)은 자격증명 스코프에서 가져온다
            var credential = await credentials.GetAsync($"market:{marketCode}", ct);
            var settings = new Dictionary<string, string>(credential.Secrets);
            foreach (var (key, value) in request.Settings ?? []) settings[key] = value;

            var payloads = new List<ListingPayload>();
            var skipped = new List<object>();
            foreach (var product in targets)
            {
                try { payloads.Add(payloadBuilder.Build(product)); }
                catch (InvalidOperationException ex)
                {
                    skipped.Add(new { id = product.Id, reason = ex.Message });
                }
            }

            if (payloads.Count == 0)
                return Results.BadRequest(new
                {
                    error = "엑셀로 만들 수 있는 상품이 없습니다 (가격 계산 미완료).",
                    skipped,
                });

            var bytes = excel.BuildListingWorkbook(template, payloads, settings);
            var fileName = $"{template.FileNameHint}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            return Results.File(bytes, XlsxMime, fileName);
        })
        .WithSummary("선택 상품을 마켓 대량등록 엑셀로 내보내기");

        // 카테고리 목록 엑셀 내보내기 (수집할 카테고리를 골라 표시하는 용도)
        group.MapGet("/categories/{supplierCode}", async (
            string supplierCode, string? parent,
            ExcelWorkbookService excel,
            ICategoryCrawlerRegistry crawlers,
            CancellationToken ct) =>
        {
            var crawler = crawlers.Resolve(supplierCode);
            if (crawler is null)
                return Results.NotFound(new { error = $"'{supplierCode}'는 카테고리 수집을 지원하지 않습니다." });

            try
            {
                // 부모가 지정되지 않으면 최상위 + 각 하위까지 한 번에 담아 준다
                var categories = new List<SupplierCategory>();
                var roots = await crawler.GetCategoriesAsync(parent, ct);
                categories.AddRange(roots);
                if (parent is null)
                {
                    foreach (var root in roots.Where(r => r.HasChildren).Take(20))
                        categories.AddRange(await crawler.GetCategoriesAsync(root.Code, ct));
                }

                var bytes = excel.BuildCategoryWorkbook(supplierCode, categories);
                return Results.File(bytes, XlsxMime,
                    $"{supplierCode}_카테고리_{DateTime.Now:yyyyMMdd}.xlsx");
            }
            catch (PermanentScrapeException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("공급처 카테고리 목록 엑셀 내보내기");

        // 카테고리 엑셀 가져오기 → 표시된 카테고리들 대량 수집
        group.MapPost("/categories/import", async (
            IFormFile file,
            ExcelWorkbookService excel,
            CollectCategoryUseCase useCase,
            ICategoryCrawlerRegistry crawlers,
            Guid? pricingPolicyId,
            CancellationToken ct) =>
        {
            if (file.Length == 0)
                return Results.BadRequest(new { error = "빈 파일입니다." });

            List<CategoryImportRow> rows;
            try
            {
                await using var stream = file.OpenReadStream();
                rows = excel.ParseCategoryWorkbook(stream).ToList();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"엑셀을 읽을 수 없습니다: {ex.Message}" });
            }

            if (rows.Count == 0)
                return Results.BadRequest(new
                {
                    error = "'수집' 열에 Y로 표시된 행이 없습니다. 수집할 카테고리에 Y를 입력한 뒤 다시 올려주세요.",
                });

            var started = new List<object>();
            var failed = new List<object>();
            foreach (var row in rows)
            {
                if (crawlers.Resolve(row.SupplierCode) is null)
                {
                    failed.Add(new { row.RowNumber, row.SupplierCode, reason = "카테고리 수집 미지원 공급처" });
                    continue;
                }
                try
                {
                    var job = await useCase.ExecuteAsync(
                        row.SupplierCode, row.CategoryCode, row.CategoryName, row.Keyword,
                        row.MaxProducts, null, null, pricingPolicyId, ct);
                    started.Add(new { jobId = job.Id, row.CategoryName, row.CategoryCode, row.MaxProducts });
                }
                catch (ArgumentException ex)
                {
                    failed.Add(new { row.RowNumber, reason = ex.Message });
                }
            }

            return Results.Accepted("/api/v1/categories/jobs", new
            {
                started = started.Count,
                jobs = started,
                failed,
            });
        })
        .DisableAntiforgery()
        .WithSummary("카테고리 엑셀 업로드 → 표시된 카테고리 대량 수집");
    }
}

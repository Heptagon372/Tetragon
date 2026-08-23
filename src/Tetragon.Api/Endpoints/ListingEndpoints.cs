using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Application.UseCases;
using Tetragon.Domain.Catalog;

namespace Tetragon.Api.Endpoints;

public static class ListingEndpoints
{
    public static void MapListingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/listings").WithTags("Listings");

        // 대량 등록 요청 (설계서 §8)
        group.MapPost("/", async (
            ListingRequest request,
            IProductRepository products,
            RequestListingUseCase useCase,
            CancellationToken ct) =>
        {
            if (request.ProductIds.Count == 0 || request.MarketCodes.Count == 0)
                return Results.BadRequest(new { error = "productIds와 marketCodes가 필요합니다." });

            // 등록 가능 상태 확인 (Ready/Listed만)
            var notReady = new List<object>();
            var ready = new List<Guid>();
            foreach (var id in request.ProductIds)
            {
                var product = await products.FindAsync(id, ct);
                if (product is null) { notReady.Add(new { id, reason = "상품 없음" }); continue; }
                if (product.Status is ProductStatus.Ready or ProductStatus.Listed) ready.Add(id);
                else notReady.Add(new { id, reason = $"등록 불가 상태: {product.Status}" });
            }

            if (ready.Count > 0)
                await useCase.ExecuteAsync(ready, request.MarketCodes, ct);

            return Results.Accepted("/api/v1/listings", new
            {
                requested = ready.Count,
                markets = request.MarketCodes,
                skipped = notReady,
            });
        })
        .WithSummary("선택 상품을 선택 마켓에 대량 등록 요청");

        group.MapGet("/", async (
            string? market, string? status, IListingRepository listings, CancellationToken ct) =>
        {
            var items = await listings.SearchAsync(market, status, ct);
            return Results.Ok(new { items = items.Select(ListingDto.Of) });
        })
        .WithSummary("등록 현황 조회");

        // 재고 동기화 수동 트리거 (설계서 5.7)
        app.MapPost("/api/v1/inventory/check", async (
            List<Guid> productIds, IEventBus bus, CancellationToken ct) =>
        {
            foreach (var id in productIds)
                await bus.PublishAsync(new Domain.Events.InventoryCheckRequested
                {
                    ProductId = id,
                    CorrelationId = Guid.NewGuid(),
                }, ct);
            return Results.Accepted("/api/v1/listings", new { requested = productIds.Count });
        })
        .WithTags("Listings")
        .WithSummary("재고/가격 동기화 수동 실행");

        // 등록 페이로드 미리보기 (실제 등록 전 확인용)
        app.MapGet("/api/v1/products/{id:guid}/listing-payload", async (
            Guid id, IProductRepository products, ListingPayloadBuilder builder, CancellationToken ct) =>
        {
            var product = await products.FindAsync(id, ct);
            if (product is null) return Results.NotFound();
            try
            {
                var payload = builder.Build(product);
                return Results.Ok(payload);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithTags("Listings")
        .WithSummary("마켓 등록 페이로드 미리보기");
    }
}

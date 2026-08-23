using System.Text.Json;
using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Domain.Compliance;
using Tetragon.Domain.Listings;
using Tetragon.Domain.Ordering;
using Tetragon.Domain.Sourcing;
using Tetragon.Infrastructure.Messaging;
using Tetragon.Infrastructure.Persistence;
using Tetragon.Plugin.Abstractions;
using Tetragon.SharedKernel;

namespace Tetragon.Api.Endpoints;

public static class ComplianceEndpoints
{
    public static void MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/compliance").WithTags("Compliance");

        group.MapGet("/rules", async (IComplianceRepository repo, CancellationToken ct) =>
        {
            var rules = await repo.RulesAsync(ct);
            return Results.Ok(rules.Select(r => new
            {
                r.Id, r.Keyword,
                severity = r.Severity.ToString(),
                r.Reason, r.MarketCode,
            }));
        })
        .WithSummary("금지어 Rule 목록");

        group.MapPost("/rules", async (
            ComplianceRuleRequest request, IComplianceRepository repo, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Keyword))
                return Results.BadRequest(new { error = "keyword가 필요합니다." });
            var severity = Enum.TryParse<ComplianceSeverity>(request.Severity, true, out var s)
                ? s : ComplianceSeverity.Block;
            var rule = ComplianceRule.Create(request.Keyword.Trim(), severity, request.Reason, request.MarketCode);
            await repo.AddRuleAsync(rule, ct);
            await repo.SaveAsync(ct);
            return Results.Created($"/api/v1/compliance/rules/{rule.Id}", new { rule.Id, rule.Keyword });
        })
        .WithSummary("금지어 추가");

        group.MapDelete("/rules/{id:guid}", async (
            Guid id, IComplianceRepository repo, CancellationToken ct) =>
        {
            await repo.RemoveRuleAsync(id, ct);
            await repo.SaveAsync(ct);
            return Results.NoContent();
        })
        .WithSummary("금지어 삭제");
    }
}

public static class ShippingPlaceEndpoints
{
    public static void MapShippingPlaceEndpoints(this IEndpointRouteBuilder app)
    {
        // 공급처별 배송지 등록 현황 — 반품지는 WING에 사람이 한 번 등록해야 하므로
        // 무엇이 빠졌는지 한 번에 보여준다
        app.MapGet("/api/v1/settings/shipping-places/{marketCode}", async (
            string marketCode, ShippingPlaceAudit audit, CancellationToken ct) =>
        {
            var result = await audit.RunAsync(marketCode, ct);
            return Results.Ok(new
            {
                result.MarketCode,
                result.Supported,
                result.Message,
                result.MissingReturnCount,
                suppliers = result.Suppliers.Select(s => new
                {
                    s.SupplierName,
                    s.OutboundAddress,
                    s.ReturnAddress,
                    s.ReturnZipcode,
                    s.ReturnPhone,
                    s.OutboundCode,
                    s.ReturnCenterCode,
                    s.OutboundReady,
                    s.ReturnReady,
                    s.RegisterLine,
                    // WING 반품지 등록 폼에 그대로 넣을 값
                    s.FormPlaceName,
                    s.FormAddress,
                    s.FormAddressDetail,
                    s.FormPhone,
                }),
            });
        })
        .WithTags("Settings")
        .WithSummary("공급처별 출고지/반품지 등록 현황");
    }
}

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/audit").WithTags("Audit");

        // 공급처 재고 점검 — 품절·재고부족·원가변동
        group.MapPost("/stock", async (
            int? limit, int? threshold, bool? listedOnly,
            StockAudit audit, CancellationToken ct) =>
        {
            var result = await audit.RunAsync(
                limit ?? 50,
                threshold ?? StockAudit.DefaultLowStockThreshold,
                listedOnly ?? false,
                ct);
            return Results.Ok(result);
        })
        .WithSummary("공급처 재고 점검 (품절·재고부족·원가변동)");

        // 중복 등록·아이템위너 위험
        group.MapGet("/duplicates", async (
            int? limit, DuplicateAudit audit, CancellationToken ct) =>
        {
            var result = await audit.RunAsync(limit ?? 1000, ct);
            return Results.Ok(result);
        })
        .WithSummary("중복 상품·아이템위너 위험 점검");
    }
}

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders").WithTags("Orders");

        group.MapGet("/", async (string? status, IOrderRepository orders, CancellationToken ct) =>
        {
            var items = await orders.SearchAsync(status, ct);
            return Results.Ok(new { items = items.Select(OrderDto) });
        })
        .WithSummary("주문 목록");

        // 마켓 주문 수집 (설계서 5.8 — 폴링 방식)
        group.MapPost("/fetch", async (
            string? market,
            IMarketplaceAdapterRegistry markets,
            ICredentialStore credentials,
            IOrderRepository orders,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("OrderFetch");
            var targets = market is null
                ? markets.All
                : markets.All.Where(a => a.Code == market).ToList();

            var imported = 0;
            var errors = new List<object>();
            var range = new DateRange(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow);

            foreach (var adapter in targets)
            {
                try
                {
                    var credential = await credentials.GetAsync($"market:{adapter.Code}", ct);
                    var marketOrders = await adapter.FetchOrdersAsync(range, credential, ct);
                    foreach (var mo in marketOrders)
                    {
                        var existing = await orders.FindByMarketOrderAsync(mo.MarketCode, mo.MarketOrderId, ct);
                        if (existing is not null) continue;
                        // 위탁판매: 구매자 배송지를 함께 저장한다 (공급처 발주의 수령지가 된다)
                        await orders.AddAsync(Order.Import(
                            Tenant.Default, mo.MarketCode, mo.MarketOrderId, mo.MarketItemId,
                            mo.ProductName, mo.OptionName, mo.Quantity, mo.PaidAmount,
                            mo.OrdererName, mo.OrderedAt,
                            mo.ShipTo?.ReceiverName, mo.ShipTo?.Phone, mo.ShipTo?.Zipcode,
                            mo.ShipTo?.FullAddress, mo.ShipTo?.Message), ct);
                        imported++;
                    }
                    await orders.SaveAsync(ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "{Market} 주문 수집 실패", adapter.Code);
                    errors.Add(new { market = adapter.Code, error = ex.Message });
                }
            }

            return Results.Ok(new { imported, errors });
        })
        .WithSummary("마켓 주문 수집");

        // 위탁판매 발주서 — 무엇을·어디서·누구에게·얼마에 사야 하는지 한 곳에 모아 준다
        group.MapGet("/{id:guid}/purchase-sheet", async (
            Guid id, PurchaseOrderSheetBuilder builder, CancellationToken ct) =>
        {
            var sheet = await builder.BuildAsync(id, ct);
            return sheet is null ? Results.NotFound() : Results.Ok(sheet);
        })
        .WithSummary("위탁판매 발주서 (공급처 발주에 필요한 정보 일괄)");

        // 발주 전 점검 — 돈을 쓰기 전에 무엇이 막고 있는지 보여준다
        group.MapGet("/{id:guid}/purchase-preflight", async (
            Guid id, SupplierPurchaseService purchases, CancellationToken ct) =>
        {
            var preflight = await purchases.PreflightAsync(id, ct);
            return Results.Ok(new
            {
                orderId = preflight.OrderId,
                canAutoOrder = preflight.CanAutoOrder,
                blockers = preflight.Blockers,
                warnings = preflight.Warnings,
                estimatedAmount = preflight.EstimatedAmount,
                walletAvailable = preflight.WalletAvailable,
                walletShortfall = preflight.WalletShortfall,
                capabilityReason = preflight.CapabilityReason,
                capabilityHowToEnable = preflight.CapabilityHowToEnable,
                supplierUrl = preflight.SupplierUrl,
            });
        })
        .WithSummary("발주 전 점검 (잔액·권한·배송지)");

        // 자동 발주 — 실제로 돈이 나간다
        group.MapPost("/{id:guid}/auto-purchase", async (
            Guid id, SupplierPurchaseService purchases, CancellationToken ct) =>
        {
            var outcome = await purchases.PurchaseAsync(id, ct);
            if (outcome.Success)
                return Results.Ok(new
                {
                    success = true,
                    supplierOrderNo = outcome.SupplierOrderNo,
                    paidAmount = outcome.PaidAmount,
                    walletAvailableAfter = outcome.WalletAvailableAfter,
                });

            return Results.BadRequest(new
            {
                success = false,
                error = outcome.Message,
                blockers = outcome.Preflight?.Blockers ?? [],
                warnings = outcome.Preflight?.Warnings ?? [],
                walletShortfall = outcome.Preflight?.WalletShortfall,
                supplierUrl = outcome.Preflight?.SupplierUrl,
                howToEnable = outcome.Preflight?.CapabilityHowToEnable,
            });
        })
        .WithSummary("공급처 자동 발주 (금고에서 차감)");

        group.MapPost("/{id:guid}/purchase", async (
            Guid id, ManualPurchaseRequest? request, IOrderRepository orders, CancellationToken ct) =>
        {
            var order = await orders.FindAsync(id, ct);
            if (order is null) return Results.NotFound();
            try
            {
                // 공급처 주문번호와 실제 지불액을 함께 기록해 마진을 정확히 계산한다
                if (request?.SupplierOrderNo is { Length: > 0 } supplierOrderNo)
                    order.MarkSupplierOrdered(supplierOrderNo,
                        request.SupplierPaidAmount is { } paid ? Money.Krw(paid) : null);
                else
                    order.TransitionTo(OrderStatus.SupplierOrdered);

                await orders.SaveAsync(ct);
                return Results.Ok(OrderDto(order));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("공급처 발주 완료 기록");

        group.MapPost("/{id:guid}/tracking", async (
            Guid id, TrackingRequest request, IOrderRepository orders, CancellationToken ct) =>
        {
            var order = await orders.FindAsync(id, ct);
            if (order is null) return Results.NotFound();
            try
            {
                order.RegisterTracking(request.TrackingNo, request.DeliveryCompanyCode);
                await orders.SaveAsync(ct);
                return Results.Ok(OrderDto(order));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("송장번호 등록");
    }

    private static object OrderDto(Order o) => new
    {
        id = o.Id,
        marketCode = o.MarketCode,
        marketOrderId = o.MarketOrderId,
        productName = o.ProductName,
        optionName = o.OptionName,
        quantity = o.Quantity,
        paidAmount = o.PaidAmount.Amount,
        ordererName = o.OrdererName,
        status = o.Status.ToString(),
        trackingNo = o.TrackingNo,
        orderedAt = o.OrderedAt,
        // 위탁판매 발주 정보
        supplierCode = o.SupplierCode,
        supplierUrl = o.SupplierUrl,
        supplierOrderNo = o.SupplierOrderNo,
        receiverName = o.ReceiverName,
        receiverAddress = o.ReceiverAddress,
        estimatedMargin = o.EstimatedMargin,
    };
}

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/dashboard/summary", async (
            IProductRepository products,
            IScrapeJobRepository jobs,
            IListingRepository listings,
            IOrderRepository orders,
            CancellationToken ct) =>
        {
            var byStatus = await products.CountByStatusAsync(ct);
            var recentJobs = await jobs.RecentAsync(200, ct);
            var allListings = await listings.SearchAsync(null, null, ct);
            var allOrders = await orders.SearchAsync(null, ct);
            var today = DateTimeOffset.UtcNow.Date;

            return Results.Ok(new
            {
                products = new
                {
                    total = byStatus.Values.Sum(),
                    byStatus,
                },
                pipeline = new
                {
                    running = recentJobs.Count(j => j.State == JobState.Running),
                    succeeded = recentJobs.Count(j => j.State == JobState.Succeeded),
                    failed = recentJobs.Count(j => j.State is JobState.Failed or JobState.DeadLettered),
                    blocked = recentJobs.Count(j => j.State == JobState.Blocked),
                    byStage = recentJobs.GroupBy(j => j.Stage.ToString())
                        .ToDictionary(g => g.Key, g => g.Count()),
                    todayCollected = recentJobs.Count(j => j.CreatedAt.UtcDateTime.Date == today),
                },
                listings = new
                {
                    total = allListings.Count,
                    registered = allListings.Count(l => l.Status == ListingStatus.Registered),
                    failed = allListings.Count(l => l.Status == ListingStatus.Failed),
                    suspended = allListings.Count(l => l.Status == ListingStatus.Suspended),
                    byMarket = allListings.GroupBy(l => l.MarketCode)
                        .ToDictionary(g => g.Key, g => new
                        {
                            total = g.Count(),
                            registered = g.Count(l => l.Status == ListingStatus.Registered),
                        }),
                },
                orders = new
                {
                    total = allOrders.Count,
                    revenue = allOrders.Sum(o => o.PaidAmount.Amount),
                    byStatus = allOrders.GroupBy(o => o.Status.ToString())
                        .ToDictionary(g => g.Key, g => g.Count()),
                },
            });
        })
        .WithTags("Dashboard")
        .WithSummary("대시보드 집계");
    }
}

public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/plugins", (
            ISupplierPluginRegistry suppliers,
            IMarketplaceAdapterRegistry markets,
            IEnumerable<IAiProviderPlugin> aiProviders) =>
            Results.Ok(new
            {
                suppliers = suppliers.All.Select(p => new { p.Code, p.DisplayName, p.Version, p.IsLive, p.IsAvailable }),
                marketplaces = markets.All.Select(p => new { p.Code, p.DisplayName, p.Version, p.IsLive, p.IsAvailable }),
                aiProviders = aiProviders.Select(p => new
                {
                    p.Code, p.DisplayName, p.Version, p.IsLive, p.IsAvailable,
                    capabilities = p.Capabilities.Select(c => c.ToString()),
                }),
            }))
        .WithTags("Plugins")
        .WithSummary("등록된 플러그인 목록");
    }
}

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings").WithTags("Settings");

        group.MapGet("/credentials", async (ICredentialStore store, CancellationToken ct) =>
        {
            var scopes = await store.ListScopesAsync(ct);
            // 값은 반환하지 않고 키 이름만 노출
            return Results.Ok(scopes);
        })
        .WithSummary("등록된 자격증명 스코프/키 목록 (값 미노출)");

        group.MapPut("/credentials", async (
            CredentialRequest request,
            ICredentialStore store,
            CachedCredentialProvider cache,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Scope))
                return Results.BadRequest(new { error = "scope가 필요합니다. 예: market:coupang, ai:claude, supplier:taobao" });
            await store.SetAsync(request.Scope, request.Secrets, ct);
            cache.Invalidate(request.Scope);
            return Results.Ok(new { saved = true, scope = request.Scope, keys = request.Secrets.Keys });
        })
        .WithSummary("자격증명 저장 (빈 값은 기존 유지)");
    }
}

public static class StreamEndpoints
{
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        // SSE — 실시간 파이프라인 이벤트 (설계서 5.9의 SignalR 대응)
        app.MapGet("/api/v1/stream", async (
            HttpContext context, PipelineNotifier notifier, CancellationToken ct) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers["X-Accel-Buffering"] = "no";

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            // 최근 이벤트 먼저 전송 (재연결 시 공백 보정)
            foreach (var recent in notifier.Recent.TakeLast(20))
                await WriteAsync(context, recent, options, ct);

            try
            {
                await foreach (var notification in notifier.SubscribeAsync(ct))
                    await WriteAsync(context, notification, options, ct);
            }
            catch (OperationCanceledException) { /* 클라이언트 연결 종료 */ }
        })
        .WithTags("Stream")
        .WithSummary("파이프라인 실시간 이벤트 스트림 (SSE)");
    }

    private static async Task WriteAsync(
        HttpContext context, PipelineNotification notification, JsonSerializerOptions options, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(notification, options);
        await context.Response.WriteAsync($"data: {json}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
}

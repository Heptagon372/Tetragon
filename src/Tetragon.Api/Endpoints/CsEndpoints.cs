using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Domain.Ordering;

namespace Tetragon.Api.Endpoints;

/// <summary>CS 관리 — 자동 이행 정책, 실행, 취소·반품 티켓 처리.</summary>
public static class CsEndpoints
{
    public static void MapCsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cs").WithTags("CS");

        // ── 자동화 정책 ──────────────────────────────────────────────────

        group.MapGet("/automation", async (
            IAutomationPolicyRepository policies, CancellationToken ct) =>
            Results.Ok(PolicyDto(await policies.GetOrCreateAsync(ct))))
        .WithSummary("자동 이행 정책 조회");

        group.MapPut("/automation", async (
            AutomationSettingsRequest request,
            IAutomationPolicyRepository policies,
            CancellationToken ct) =>
        {
            var policy = await policies.GetOrCreateAsync(ct);
            policy.Configure(
                request.Enabled, request.DryRun,
                request.AutoCollectOrders, request.AutoPurchase,
                request.AutoUploadTracking, request.AutoCollectCs,
                request.MaxOrderAmount, request.DailyLimit,
                request.ConsecutiveFailureLimit, request.IntervalMinutes);
            await policies.SaveAsync(ct);
            return Results.Ok(PolicyDto(policy));
        })
        .WithSummary("자동 이행 정책 변경");

        // 자동으로 멈춘 상태를 사람이 확인하고 푼다
        group.MapPost("/automation/resume", async (
            IAutomationPolicyRepository policies, CancellationToken ct) =>
        {
            var policy = await policies.GetOrCreateAsync(ct);
            policy.Resume();
            await policies.SaveAsync(ct);
            return Results.Ok(PolicyDto(policy));
        })
        .WithSummary("멈춘 자동화 재개");

        // 지금 한 바퀴 돌린다. 스위치가 꺼져 있어도 시험 실행은 가능하다.
        group.MapPost("/automation/run", async (
            FulfillmentAutomation automation, CancellationToken ct) =>
        {
            var run = await automation.RunAsync(manualTrigger: true, ct);
            return Results.Ok(RunDto(run));
        })
        .WithSummary("자동 이행 즉시 실행");

        // ── CS 티켓 ──────────────────────────────────────────────────────

        group.MapGet("/tickets", async (
            string? status, string? kind,
            ICsTicketRepository tickets, IOrderRepository orders, CancellationToken ct) =>
        {
            var items = await tickets.SearchAsync(status, kind, ct);

            // 티켓마다 연결된 주문의 처리 상태를 함께 준다 —
            // "발주가 나갔는지"를 봐야 공급처에 무엇을 해야 할지 판단할 수 있다
            var result = new List<object>();
            foreach (var t in items)
            {
                var order = t.OrderId is { } id ? await orders.FindAsync(id, ct) : null;
                result.Add(TicketDto(t, order));
            }

            return Results.Ok(new
            {
                items = result,
                needsAttention = items.Count(t => t.NeedsAttention),
            });
        })
        .WithSummary("CS 요청 목록");

        group.MapPost("/tickets/{id:guid}/supplier-done", async (
            Guid id, CsNoteRequest? request, ICsTicketRepository tickets, CancellationToken ct) =>
        {
            var ticket = await tickets.FindAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            ticket.MarkSupplierActionDone(request?.Note ?? "공급처 취소/반품 처리 완료");
            await tickets.SaveAsync(ct);
            return Results.Ok(TicketDto(ticket, null));
        })
        .WithSummary("공급처 조치 완료 표시");

        group.MapPost("/tickets/{id:guid}/resolve", async (
            Guid id, CsNoteRequest? request, ICsTicketRepository tickets, CancellationToken ct) =>
        {
            var ticket = await tickets.FindAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            try
            {
                ticket.Resolve(request?.Note ?? "처리 완료");
                await tickets.SaveAsync(ct);
                return Results.Ok(TicketDto(ticket, null));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithSummary("CS 요청 처리 완료");

        group.MapPost("/tickets/{id:guid}/dismiss", async (
            Guid id, CsNoteRequest? request, ICsTicketRepository tickets, CancellationToken ct) =>
        {
            var ticket = await tickets.FindAsync(id, ct);
            if (ticket is null) return Results.NotFound();
            ticket.Dismiss(request?.Note ?? "대응 불필요");
            await tickets.SaveAsync(ct);
            return Results.Ok(TicketDto(ticket, null));
        })
        .WithSummary("CS 요청 무시 처리");
    }

    private static object PolicyDto(AutomationPolicy p) => new
    {
        p.Enabled,
        p.DryRun,
        p.AutoCollectOrders,
        p.AutoPurchase,
        p.AutoUploadTracking,
        p.AutoCollectCs,
        p.MaxOrderAmount,
        p.DailyLimit,
        p.ConsecutiveFailureLimit,
        p.IntervalMinutes,
        p.ConsecutiveFailures,
        p.LastRunAt,
        p.LastRunSummary,
        p.HaltedReason,
        p.SpentToday,
        spentDate = p.SpentDate.ToString("yyyy-MM-dd"),
        p.CanRun,
    };

    private static object RunDto(AutomationRun r) => new
    {
        r.DryRun,
        r.StartedAt,
        r.FinishedAt,
        r.Summary,
        r.OrdersCollected,
        r.OrdersLinked,
        r.Purchased,
        r.PurchasedAmount,
        r.TrackingUploaded,
        r.CsCollected,
        r.CsAutoCancelled,
        r.WouldPurchase,
        r.PurchaseSkipped,
        r.TrackingSkipped,
        r.Unsupported,
        r.Errors,
        r.HaltedReason,
        r.SkipReason,
    };

    private static object TicketDto(CsTicket t, Order? order) => new
    {
        t.Id,
        t.MarketCode,
        t.MarketOrderId,
        t.MarketTicketId,
        kind = t.Kind.ToString(),
        status = t.Status.ToString(),
        t.ProductName,
        t.Reason,
        t.Quantity,
        t.RequestedAt,
        t.SupplierActionRequired,
        t.SupplierActionDone,
        t.HandledNote,
        t.HandledAt,
        t.NeedsAttention,
        t.OrderId,
        order = order is null ? null : new
        {
            status = order.Status.ToString(),
            order.SupplierOrderNo,
            order.SupplierUrl,
            supplierPaid = order.SupplierPaidAmount?.Amount,
            order.TrackingNo,
        },
    };
}

public sealed record AutomationSettingsRequest(
    bool? Enabled, bool? DryRun,
    bool? AutoCollectOrders, bool? AutoPurchase,
    bool? AutoUploadTracking, bool? AutoCollectCs,
    decimal? MaxOrderAmount, decimal? DailyLimit,
    int? ConsecutiveFailureLimit, int? IntervalMinutes);

public sealed record CsNoteRequest(string? Note);

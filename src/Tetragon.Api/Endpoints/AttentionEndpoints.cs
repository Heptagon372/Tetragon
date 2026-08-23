using Tetragon.Api.Contracts;
using Tetragon.Application.Ports;
using Tetragon.Application.Services;
using Tetragon.Domain.Attention;

namespace Tetragon.Api.Endpoints;

/// <summary>
/// 주의 원장 API (확장 08 §1.6).
///
/// 목록은 언제나 <c>impact_krw</c> 내림차순이다 — v2 README가 "진짜 목적지"라 부른
/// <b>"이번 주에 손댈 상품 10개"</b> 화면이 바로 이것이다.
/// </summary>
public static class AttentionEndpoints
{
    public static void MapAttentionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/attention").WithTags("Attention");

        group.MapGet("/", async (
            string? state, string? kind, int? limit,
            IAttentionRepository repo, CancellationToken ct) =>
        {
            // state를 안 주면 "지금 봐야 하는 것" — 열린 항목 + 연기 기한이 지난 항목
            AttentionState? parsed = null;
            var dueOnly = string.IsNullOrWhiteSpace(state) || state.Equals("due", StringComparison.OrdinalIgnoreCase);
            if (!dueOnly)
            {
                if (!Enum.TryParse<AttentionState>(state, true, out var s))
                    return Results.BadRequest(new { error = $"알 수 없는 상태: {state}" });
                parsed = s;
            }

            var items = await repo.QueryAsync(parsed, kind, dueOnly, Math.Clamp(limit ?? 50, 1, 500), ct);
            return Results.Ok(new { items = items.Select(AttentionDto.Of) });
        })
        .WithSummary("주의 목록 (영향 금액 내림차순)");

        group.MapGet("/summary", async (IAttentionRepository repo, CancellationToken ct) =>
        {
            var rows = await repo.SummaryAsync(ct);
            return Results.Ok(new
            {
                open = rows.Sum(r => r.Count),
                impactKrw = rows.Sum(r => r.ImpactKrw),
                kinds = rows.Select(r => new
                {
                    kind = r.Kind,
                    label = AttentionDto.KindLabel(r.Kind),
                    count = r.Count,
                    impactKrw = r.ImpactKrw,
                }),
            });
        })
        .WithSummary("kind별 건수·영향 금액 합계");

        group.MapPost("/{id:guid}/snooze", async (
            Guid id, AttentionSnoozeRequest request, AttentionLedger ledger, CancellationToken ct) =>
        {
            try
            {
                return await ledger.SnoozeAsync(id, request.UntilDays ?? 7, ct)
                    ? Results.Ok(new { snoozed = true })
                    : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("나중에 보기 — 기한이 지나면 목록에 다시 올라온다");

        group.MapPost("/{id:guid}/done", async (
            Guid id, AttentionNoteRequest request, AttentionLedger ledger, CancellationToken ct) =>
            await ledger.DoneAsync(id, request.Note, ct)
                ? Results.Ok(new { done = true })
                : Results.NotFound())
        .WithSummary("처리 완료");

        group.MapPost("/{id:guid}/dismiss", async (
            Guid id, AttentionNoteRequest request, AttentionLedger ledger, CancellationToken ct) =>
            await ledger.DismissAsync(id, request.Note, ct)
                ? Results.Ok(new { dismissed = true })
                : Results.NotFound())
        .WithSummary("볼 필요 없음");

        group.MapPost("/bulk", async (
            AttentionBulkRequest request, AttentionLedger ledger, CancellationToken ct) =>
        {
            if (request.Ids.Count == 0)
                return Results.BadRequest(new { error = "처리할 항목을 선택하세요." });
            if (!Enum.TryParse<AttentionBulkAction>(request.Action, true, out var action))
                return Results.BadRequest(new { error = $"알 수 없는 동작: {request.Action}" });

            var applied = await ledger.BulkAsync(request.Ids, action, request.UntilDays ?? 7, request.Note, ct);
            return Results.Ok(new { applied });
        })
        .WithSummary("일괄 처리 — 집계 항목이 가리키는 묶음을 한 번에");

        group.MapPost("/scan", async (AttentionScan scan, CancellationToken ct) =>
        {
            var result = await scan.RunAsync(ct);
            return Results.Ok(new
            {
                opened = result.Opened,
                touched = result.Touched,
                folded = result.Folded,
                autoClosed = result.AutoClosed,
                notes = result.Notes,
            });
        })
        .WithSummary("지금 코드가 아는 사실을 원장에 싣는다 (시간당 자동 실행되는 것을 수동으로)");
    }
}

public static class AttentionDto
{
    public static object Of(AttentionItem a) => new
    {
        id = a.Id,
        kind = a.Kind,
        kindLabel = KindLabel(a.Kind),
        subjectType = a.SubjectType,
        subjectId = a.SubjectId,
        severity = a.Severity.ToString(),
        impactKrw = a.ImpactKrw,
        impactBasis = a.ImpactBasis,
        title = a.Title,
        detail = a.Detail,
        suggestedAction = a.SuggestedAction,
        state = a.State.ToString(),
        snoozeUntil = a.SnoozeUntil,
        firstSeenAt = a.FirstSeenAt,
        lastSeenAt = a.LastSeenAt,
        seenCount = a.SeenCount,
        resolvedAt = a.ResolvedAt,
        resolvedBy = a.ResolvedBy,
        resolutionNote = a.ResolutionNote,
    };

    public static string KindLabel(string kind) => kind switch
    {
        AttentionKinds.MarginLoss => "역마진",
        AttentionKinds.MarginThin => "마진 미달",
        AttentionKinds.WinnerLost => "위너 상실",
        AttentionKinds.PerfDead => "성과 없음",
        AttentionKinds.SettleUnmatched => "정산 미매칭",
        AttentionKinds.SettleGap => "이익 차이",
        AttentionKinds.SagaDisputed => "발주 분쟁",
        AttentionKinds.WorkDead => "수집 실패",
        AttentionKinds.SourceCandidate => "소싱 후보",
        AttentionKinds.ContentRejected => "콘텐츠 반려",
        AttentionKinds.RegulationBlocked => "등록 차단",
        AttentionKinds.WalletLow => "금고 부족",
        AttentionKinds.CsSupplierAction => "공급처 조치",
        AttentionKinds.Folded => "묶음",
        _ => kind,
    };
}

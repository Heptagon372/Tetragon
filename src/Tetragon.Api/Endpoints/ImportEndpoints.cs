using Tetragon.Api.Contracts;
using Tetragon.Application.Services.Import;
using Tetragon.Domain.Imports;

namespace Tetragon.Api.Endpoints;

/// <summary>
/// CSV 임포트 프레임 API (확장 08 §3) — 09(성과)·10(정산)이 공유한다.
///
/// 이 프레임이 하는 일은 <b>"이 파일을 읽어도 되는가"</b>까지다.
/// 실제 적재(<c>listing_perf</c>·<c>settlement_lines</c>)는 09·10이 붙인다.
///
/// 순서가 중요하다:
///   1. <c>POST /imports/inspect</c> — 지문을 만들고 프로파일이 있는지 본다
///   2. 없으면 화면에서 매핑을 확정하고 <c>PUT /imports/profiles</c>
///   3. <c>POST /imports/validate</c> — 전량을 검증한다. 한 줄이라도 실패하면 파일 전체 거부
/// </summary>
public static class ImportEndpoints
{
    /// <summary>미리보기로 돌려주는 행 수. 전량을 돌려주면 20만 줄이 브라우저로 간다.</summary>
    private const int PreviewRows = 20;

    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/imports").WithTags("Imports");

        group.MapGet("/fields/{purpose}", (string purpose) =>
        {
            if (!ImportPurposes.IsKnown(purpose))
                return Results.BadRequest(new { error = $"알 수 없는 임포트 목적: {purpose}" });

            return Results.Ok(new
            {
                purpose,
                fields = ImportFieldCatalog.For(purpose).Select(f => new
                {
                    name = f.Name,
                    label = f.Label,
                    type = f.Type.ToString(),
                    required = f.Required,
                }),
            });
        })
        .WithSummary("이 목적이 요구하는 표준 필드 목록");

        group.MapPost("/inspect", async (
            IFormFile file, string channel, string purpose,
            CsvImporter importer, CancellationToken ct) =>
        {
            var (content, error) = await ReadAsync(file, purpose, ct);
            if (error is not null) return error;

            try
            {
                var inspection = await importer.InspectAsync(channel, purpose, content!, ct);
                return Results.Ok(new
                {
                    channel = inspection.Channel,
                    purpose = inspection.Purpose,
                    fingerprint = inspection.Fingerprint,
                    encoding = inspection.EncodingName,
                    rowCount = inspection.RowCount,
                    profileExists = inspection.ProfileExists,
                    header = inspection.Header,
                    normalizedHeader = inspection.NormalizedHeader,
                    fields = inspection.Fields.Select(f => new
                    {
                        name = f.Name,
                        label = f.Label,
                        type = f.Type.ToString(),
                        required = f.Required,
                    }),
                    // 프로파일이 있으면 확정 매핑, 없으면 제안이다. 화면이 이 차이를 말로 보여준다.
                    columnMap = inspection.SuggestedMap,
                    missingRequired = inspection.MissingRequired,
                });
            }
            catch (CsvImportException ex)
            {
                return Results.BadRequest(new { error = ex.Message, details = ex.Details });
            }
        })
        .DisableAntiforgery()
        .WithSummary("헤더 지문 확인 + 매핑 제안 (적재하지 않음)");

        group.MapPost("/validate", async (
            IFormFile file, string channel, string purpose,
            CsvImporter importer, CancellationToken ct) =>
        {
            var (content, error) = await ReadAsync(file, purpose, ct);
            if (error is not null) return error;

            try
            {
                var parsed = await importer.ParseAsync(channel, purpose, content!, ct);
                return Results.Ok(new
                {
                    fingerprint = parsed.Fingerprint,
                    encoding = parsed.EncodingName,
                    checksum = parsed.Checksum,
                    rowCount = parsed.Rows.Count,
                    preview = parsed.Rows.Take(PreviewRows).Select(r => new
                    {
                        line = r.LineNumber,
                        values = r.Values,
                    }),
                });
            }
            catch (UnknownHeaderException ex)
            {
                // 새 양식이다. 화면은 이 응답을 받아 매핑 확정 단계로 넘어간다.
                return Results.BadRequest(new
                {
                    error = ex.Message,
                    fingerprint = ex.Fingerprint,
                    header = ex.Header,
                    needsProfile = true,
                });
            }
            catch (CsvImportException ex)
            {
                return Results.BadRequest(new { error = ex.Message, details = ex.Details });
            }
        })
        .DisableAntiforgery()
        .WithSummary("전량 검증 — 한 줄이라도 실패하면 파일 전체 거부");

        group.MapGet("/profiles", async (
            string? channel, string? purpose, ImportProfileStore store, CancellationToken ct) =>
        {
            var profiles = await store.ListAsync(channel, purpose, ct);
            return Results.Ok(new { items = profiles.Select(ProfileDto) });
        })
        .WithSummary("저장된 컬럼 매핑 목록");

        group.MapPut("/profiles", async (
            ImportProfileRequest request, ImportProfileStore store, CancellationToken ct) =>
        {
            try
            {
                var profile = await store.SaveAsync(
                    request.Channel, request.Purpose, request.Fingerprint,
                    request.ColumnMap, request.SampleHeader ?? [], ct);
                return Results.Ok(ProfileDto(profile));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        })
        .WithSummary("컬럼 매핑 확정 — 필수 필드가 다 붙어야 저장된다");

        group.MapDelete("/profiles/{id:guid}", async (
            Guid id, ImportProfileStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
        .WithSummary("매핑 삭제");
    }

    /// <summary>업로드 파일 검증 + 바이트 읽기. 인코딩 판정은 <see cref="CsvImporter"/>가 한다.</summary>
    private static async Task<(byte[]? Content, IResult? Error)> ReadAsync(
        IFormFile file, string purpose, CancellationToken ct)
    {
        if (!ImportPurposes.IsKnown(purpose))
            return (null, Results.BadRequest(new { error = $"알 수 없는 임포트 목적: {purpose}" }));
        if (file.Length == 0)
            return (null, Results.BadRequest(new { error = "빈 파일입니다." }));
        if (file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return (null, Results.BadRequest(new
            {
                error = "엑셀(.xlsx)은 아직 지원하지 않습니다. 판매자센터에서 CSV로 내려받아 올리세요.",
            }));

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        return (buffer.ToArray(), null);
    }

    private static object ProfileDto(ImportProfile p) => new
    {
        id = p.Id,
        channel = p.Channel,
        purpose = p.Purpose,
        fingerprint = p.HeaderFingerprint,
        columnMap = p.ColumnMap,
        sampleHeader = p.SampleHeader,
        createdBy = p.CreatedBy,
        createdAt = p.CreatedAt,
    };
}

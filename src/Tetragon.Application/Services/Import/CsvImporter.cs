using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tetragon.Domain.Imports;

namespace Tetragon.Application.Services.Import;

/// <summary>
/// CSV 임포트 프레임 (확장 08 §3) — 09(성과)와 10(정산)이 공유한다.
///
/// 흐름은 §3.2 그대로다:
/// <code>
/// 파일 업로드
///   → 헤더 정규화(공백·괄호·단위 제거) → sha256 지문
///   → 지문 일치 프로파일 있음 → 그대로 파싱
///   → 없음 → 거부 + "새 양식입니다" + 헤더 목록 + 추정 매핑 반환
///            사람이 화면에서 매핑 확정 → 프로파일 저장 → 재시도
/// </code>
///
/// <b>추측으로 파싱하지 않는다.</b> 잘못 매핑된 정산 데이터는 틀린 이익을 확신 있게
/// 보여주는데, 그게 데이터가 없는 것보다 나쁘다.
/// </summary>
public sealed class CsvImporter(ImportProfileStore profiles)
{
    /// <summary>한 파일의 상한. 이걸 넘으면 CSV가 아니라 API 연동을 해야 할 때다.</summary>
    public const int MaxRows = 200_000;

    /// <summary>보여줄 오류 행의 상한. 어차피 파일 전체를 거부하므로 전량을 셀 이유가 없다.</summary>
    private const int ErrorCap = 20;

    /// <summary>
    /// 파일을 열어 보기만 한다 — 프로파일이 있는지, 없다면 어떤 매핑을 제안하는지.
    /// 적재는 하지 않는다.
    /// </summary>
    public async Task<CsvInspection> InspectAsync(
        string channel, string purpose, byte[] content, CancellationToken ct)
    {
        var table = ReadTable(content);
        var profile = await profiles.FindAsync(channel, purpose, table.Fingerprint, ct);
        var map = profile?.ColumnMap ?? ImportFieldCatalog.Suggest(purpose, table.NormalizedHeader);

        var missing = ImportFieldCatalog.For(purpose)
            .Where(f => f.Required && !map.ContainsValue(f.Name))
            .Select(f => f.Label)
            .ToList();

        return new CsvInspection(
            channel, purpose, table.Fingerprint, table.Header, table.NormalizedHeader,
            ImportFieldCatalog.For(purpose), map, profile is not null,
            table.Rows.Count, missing, table.EncodingName);
    }

    /// <summary>
    /// 프로파일에 따라 전량을 파싱한다.
    ///
    /// §3.3 규약 1 — <b>전량 검증 후 적재.</b> 한 줄이라도 실패하면
    /// <see cref="CsvImportException"/>을 던져 파일 전체를 거부한다.
    /// 부분 적재된 정산 데이터는 어디까지가 진실인지 아무도 모르게 만든다.
    /// </summary>
    public async Task<CsvParsed> ParseAsync(
        string channel, string purpose, byte[] content, CancellationToken ct)
    {
        var table = ReadTable(content);
        var profile = await profiles.FindAsync(channel, purpose, table.Fingerprint, ct)
            ?? throw new UnknownHeaderException(table.Fingerprint, table.Header);

        var fields = ImportFieldCatalog.For(purpose);
        var byColumn = new Dictionary<int, ImportFieldSpec>();
        foreach (var (normalized, fieldName) in profile.ColumnMap)
        {
            var index = table.NormalizedHeader.ToList().IndexOf(normalized);
            var spec = fields.FirstOrDefault(f => f.Name == fieldName);
            if (index >= 0 && spec is not null) byColumn[index] = spec;
        }

        var missing = fields.Where(f => f.Required && !byColumn.Values.Contains(f)).ToList();
        if (missing.Count > 0)
            throw new CsvImportException(
                $"필수 컬럼이 매핑되지 않았습니다: {string.Join(", ", missing.Select(m => m.Label))}");

        var rows = new List<ImportRow>(table.Rows.Count);
        var errors = new List<string>();

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var raw = table.Rows[r];
            var values = new Dictionary<string, string>();

            foreach (var (index, spec) in byColumn)
            {
                var cell = index < raw.Length ? raw[index].Trim() : "";
                if (string.IsNullOrEmpty(cell))
                {
                    if (spec.Required) errors.Add($"{r + 2}행: '{spec.Label}'이 비어 있습니다.");
                    continue;
                }

                if (!TryCoerce(cell, spec.Type, out var normalizedValue))
                {
                    errors.Add($"{r + 2}행: '{spec.Label}' 값을 {TypeLabel(spec.Type)}로 읽을 수 없습니다 — \"{cell}\"");
                    continue;
                }
                values[spec.Name] = normalizedValue;
            }

            // 오류가 쌓이면 어차피 파일 전체를 거부한다. 앞의 몇 건만 보여주고 멈춘다 —
            // 20만 줄짜리 오류 목록은 아무도 안 읽는다.
            if (errors.Count >= ErrorCap) break;
            rows.Add(new ImportRow(r + 2, values));
        }

        if (errors.Count > 0)
            throw new CsvImportException(
                errors.Count >= ErrorCap
                    ? $"{ErrorCap}건 넘게 값을 읽지 못해 파일 전체를 거부했습니다. 앞의 {ErrorCap}건만 표시합니다."
                    : $"{errors.Count}건의 행에서 값을 읽지 못해 파일 전체를 거부했습니다.",
                errors);

        return new CsvParsed(table.Fingerprint, rows, table.EncodingName, Checksum(content));
    }

    // ── 헤더 정규화 · 지문 ────────────────────────────────────────────────

    /// <summary>
    /// 공백·괄호·단위를 떼고 소문자로 맞춘다.
    /// "결제금액 (원)"과 "결제금액(원)"과 "결제금액"이 같은 컬럼이 되게 하는 것이 목적이다.
    /// </summary>
    public static string NormalizeHeader(string header)
    {
        var text = header.Trim().Trim('"');
        var open = text.IndexOfAny(['(', '（', '[']);
        if (open > 0) text = text[..open];

        var buffer = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch is '_' or '-' or '.' or '/') continue;
            buffer.Append(char.ToLowerInvariant(ch));
        }
        return buffer.ToString();
    }

    /// <summary>
    /// 정규화된 헤더 배열의 sha256. 컬럼명이 하나라도 바뀌면 달라진다.
    /// 구분자로 US(0x1F)를 쓰는 이유는 컬럼명에 절대 안 들어가는 문자여야
    /// ["ab","c"]와 ["a","bc"]가 같은 지문이 되지 않기 때문이다.
    /// </summary>
    private const char HeaderSeparator = (char)0x1f;

    public static string Fingerprint(IEnumerable<string> normalizedHeader) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join(HeaderSeparator, normalizedHeader)))).ToLowerInvariant();

    public static string Checksum(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    // ── 파일 읽기 ────────────────────────────────────────────────────────

    private static CsvTable ReadTable(byte[] content)
    {
        var (text, encodingName) = Decode(content);
        var grid = ParseCsv(text);
        if (grid.Count == 0) throw new CsvImportException("빈 파일입니다.");

        var header = grid[0].Select(h => h.Trim().Trim('"')).ToList();
        if (header.Count(h => !string.IsNullOrWhiteSpace(h)) < 2)
            throw new CsvImportException("첫 줄에서 컬럼 이름을 찾지 못했습니다. CSV 파일이 맞는지 확인하세요.");

        var rows = grid.Skip(1)
            .Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)))
            .ToList();

        if (rows.Count > MaxRows)
            throw new CsvImportException($"행이 {rows.Count:N0}건입니다. 한 번에 {MaxRows:N0}건까지만 처리합니다.");

        var normalized = header.Select(NormalizeHeader).ToList();
        return new CsvTable(header, normalized, Fingerprint(normalized), rows, encodingName);
    }

    /// <summary>
    /// 인코딩 판정. 마켓 판매자센터의 CSV는 UTF-8(BOM)과 CP949가 섞여 나온다 —
    /// 잘못 읽으면 헤더가 통째로 깨져 지문이 매번 달라지고, 프로파일이 영원히 안 맞는다.
    /// </summary>
    private static (string Text, string Encoding) Decode(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return (Encoding.UTF8.GetString(content, 3, content.Length - 3), "utf-8-bom");

        // 엄격 모드로 UTF-8을 시도해 본다. 실패하면 한국어 마켓의 기본값인 CP949다.
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return (strict.GetString(content), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            return (Korean.GetString(content), Korean.WebName);
        }
    }

    private static readonly Encoding Korean = ResolveKorean();

    private static Encoding ResolveKorean()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949);
        }
        catch (Exception)
        {
            // CP949를 못 쓰면 최소한 깨진 문자로라도 읽는다 (예외로 죽는 것보다 낫다)
            return Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
    }

    /// <summary>
    /// RFC4180 CSV 파서. 따옴표 안의 쉼표·줄바꿈·이스케이프된 따옴표를 처리한다.
    /// 라이브러리를 넣지 않은 이유는 이 프로젝트가 의존성을 최소로 유지하기 때문이다.
    /// </summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch != '"') { cell.Append(ch); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; continue; }
                inQuotes = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    row.Add(cell.ToString()); cell.Clear();
                    break;
                case '\r':
                    break; // \r\n의 \r은 버린다
                case '\n':
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add([.. row]); row.Clear();
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }

    // ── 값 변환 ──────────────────────────────────────────────────────────

    /// <summary>
    /// "1,234원", "₩1,234", "(1,234)"(음수), "2026.08.01" 같은 실제 마켓 표기를 표준형으로 바꾼다.
    /// 읽지 못하면 false — 조용히 0으로 채우지 않는다.
    /// </summary>
    private static bool TryCoerce(string cell, ImportFieldType type, out string value)
    {
        value = cell;
        switch (type)
        {
            case ImportFieldType.Text:
                return true;

            case ImportFieldType.Integer:
            case ImportFieldType.Decimal:
            {
                var negative = cell.StartsWith('(') && cell.EndsWith(')');
                var digits = new string(cell.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
                if (digits.Length == 0 || digits == "-" || digits == ".") return false;
                if (!decimal.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    return false;
                if (negative) number = -Math.Abs(number);
                value = type == ImportFieldType.Integer
                    ? ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture)
                    : number.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            case ImportFieldType.Date:
            {
                var cleaned = cell.Replace('.', '-').Replace('/', '-').Trim();
                if (cleaned.Length > 10) cleaned = cleaned[..10];
                string[] formats = ["yyyy-MM-dd", "yyyy-M-d", "yyyyMMdd"];
                if (!DateOnly.TryParseExact(cleaned, formats, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)) return false;
                value = date.ToString("yyyy-MM-dd");
                return true;
            }

            default:
                return false;
        }
    }

    private static string TypeLabel(ImportFieldType type) => type switch
    {
        ImportFieldType.Integer => "정수",
        ImportFieldType.Decimal => "금액",
        ImportFieldType.Date => "날짜",
        _ => "문자",
    };
}

internal sealed record CsvTable(
    IReadOnlyList<string> Header,
    IReadOnlyList<string> NormalizedHeader,
    string Fingerprint,
    IReadOnlyList<string[]> Rows,
    string EncodingName);

/// <param name="Fields">이 목적이 요구하는 표준 필드 목록 — 화면이 드롭다운을 만든다.</param>
/// <param name="SuggestedMap">정규화된 컬럼 → 표준 필드. 프로파일이 있으면 확정 매핑, 없으면 <b>제안</b>이다.</param>
/// <param name="MissingRequired">아직 매핑되지 않은 필수 필드. 비어야 저장할 수 있다.</param>
public sealed record CsvInspection(
    string Channel,
    string Purpose,
    string Fingerprint,
    IReadOnlyList<string> Header,
    IReadOnlyList<string> NormalizedHeader,
    IReadOnlyList<ImportFieldSpec> Fields,
    Dictionary<string, string> SuggestedMap,
    bool ProfileExists,
    int RowCount,
    IReadOnlyList<string> MissingRequired,
    string EncodingName);

/// <param name="LineNumber">원본 파일의 줄 번호 (헤더가 1행). 오류 메시지가 파일을 가리켜야 한다.</param>
public sealed record ImportRow(int LineNumber, Dictionary<string, string> Values)
{
    public string? Text(string field) => Values.GetValueOrDefault(field);

    public long Integer(string field, long fallback = 0) =>
        long.TryParse(Values.GetValueOrDefault(field), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public decimal Number(string field, decimal fallback = 0m) =>
        decimal.TryParse(Values.GetValueOrDefault(field), NumberStyles.Any,
            CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>'YYYY-MM-DD' (KST 기준 날짜). 파싱 단계에서 이미 표준형으로 맞춰 뒀다.</summary>
    public DateOnly? Date(string field) =>
        DateOnly.TryParseExact(Values.GetValueOrDefault(field) ?? "", "yyyy-MM-dd", out var v) ? v : null;
}

public sealed record CsvParsed(
    string Fingerprint, IReadOnlyList<ImportRow> Rows, string EncodingName, string Checksum);

public class CsvImportException(string message, IReadOnlyList<string>? details = null) : Exception(message)
{
    /// <summary>거부 사유를 행 단위로. 화면에 그대로 보여준다.</summary>
    public IReadOnlyList<string> Details { get; } = details ?? [];
}

/// <summary>지문에 맞는 프로파일이 없다 — 사람이 매핑을 확정해야 한다 (08 §3.2).</summary>
public sealed class UnknownHeaderException(string fingerprint, IReadOnlyList<string> header)
    : CsvImportException("새 양식입니다. 컬럼 매핑을 먼저 확정하세요.")
{
    public string Fingerprint { get; } = fingerprint;
    public IReadOnlyList<string> Header { get; } = header;
}

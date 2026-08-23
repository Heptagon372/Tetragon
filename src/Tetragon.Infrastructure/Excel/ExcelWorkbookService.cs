using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Infrastructure.Excel;

/// <summary>
/// 마켓 대량등록 엑셀 생성 및 카테고리 목록 엑셀 왕복.
/// 양식 정의(IMarketExcelTemplate)와 파일 생성(여기)을 분리해,
/// 새 마켓 양식을 추가할 때 이 클래스는 수정하지 않는다.
/// </summary>
public sealed class ExcelWorkbookService(
    IEnumerable<IMarketExcelTemplate> templates,
    ILogger<ExcelWorkbookService> logger)
{
    private readonly List<IMarketExcelTemplate> _templates = templates.ToList();

    public IReadOnlyList<IMarketExcelTemplate> Templates => _templates;

    public IMarketExcelTemplate? FindTemplate(string marketCode) =>
        _templates.FirstOrDefault(t => t.MarketCode.Equals(marketCode, StringComparison.OrdinalIgnoreCase));

    // ── 상품 대량등록 엑셀 생성 ──────────────────────────────────────────

    public byte[] BuildListingWorkbook(
        IMarketExcelTemplate template,
        IReadOnlyList<ListingPayload> payloads,
        IReadOnlyDictionary<string, string> settings)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("상품");

        // 헤더
        for (var i = 0; i < template.Columns.Count; i++)
        {
            var column = template.Columns[i];
            var cell = sheet.Cell(1, i + 1);
            cell.Value = column.Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = column.IsRequired ? XLColor.LightPink : XLColor.LightGray;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            if (column.Note is not null) cell.CreateComment().AddText(column.Note);
            sheet.Column(i + 1).Width = column.Width;
        }

        // 본문
        var row = 2;
        var productCount = 0;
        foreach (var payload in payloads)
        {
            IEnumerable<IReadOnlyList<object?>> rows;
            try
            {
                rows = template.BuildRows(new ExcelRowContext { Payload = payload, Settings = settings }).ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "엑셀 행 생성 실패 (상품 {ProductId})", payload.ProductId);
                continue;
            }

            foreach (var values in rows)
            {
                for (var i = 0; i < template.Columns.Count && i < values.Count; i++)
                    SetCell(sheet.Cell(row, i + 1), values[i]);
                row++;
            }
            productCount++;
        }

        sheet.SheetView.FreezeRows(1);
        if (row > 2) sheet.Range(1, 1, row - 1, template.Columns.Count).SetAutoFilter();

        // 안내 시트 — 업로드 방법과 필수 컬럼을 함께 넘긴다
        var guide = workbook.AddWorksheet("업로드안내");
        guide.Cell(1, 1).Value = $"{template.DisplayName} 대량등록 안내";
        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Cell(1, 1).Style.Font.FontSize = 14;
        guide.Cell(3, 1).Value = template.UploadGuide;
        guide.Cell(3, 1).Style.Alignment.WrapText = true;
        guide.Range(3, 1, 3, 6).Merge();
        guide.Cell(6, 1).Value = "필수 컬럼 (분홍 배경)";
        guide.Cell(6, 1).Style.Font.Bold = true;
        var guideRow = 7;
        foreach (var column in template.Columns.Where(c => c.IsRequired))
        {
            guide.Cell(guideRow, 1).Value = column.Header;
            guide.Cell(guideRow, 2).Value = column.Note ?? "";
            guideRow++;
        }
        guide.Cell(guideRow + 1, 1).Value = $"생성 시각: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}";
        guide.Cell(guideRow + 2, 1).Value = $"상품 {productCount}건 / 행 {row - 2}개";
        guide.Column(1).Width = 24;
        guide.Column(2).Width = 60;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        logger.LogInformation("{Market} 대량등록 엑셀 생성: 상품 {Products}건, {Rows}행",
            template.MarketCode, productCount, row - 2);
        return stream.ToArray();
    }

    /// <summary>
    /// 값 타입에 맞게 셀에 쓴다.
    /// 문자열은 항상 텍스트로 강제한다 — 상품번호·카테고리코드가 숫자로 인식되면
    /// 앞자리 0이 사라지거나 지수 표기가 되어 마켓 업로드가 실패한다.
    /// </summary>
    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case int i:
                cell.Value = i;
                break;
            case long l:
                cell.Value = l;
                break;
            case decimal d:
                cell.Value = d;
                break;
            case double db:
                cell.Value = db;
                break;
            case bool b:
                cell.Value = b;
                break;
            default:
                cell.SetValue(value.ToString());
                cell.Style.NumberFormat.Format = "@";   // 텍스트 서식 고정
                break;
        }
    }

    // ── 카테고리 목록 엑셀 (내보내기) ────────────────────────────────────

    /// <summary>
    /// 공급처 카테고리를 엑셀로 내보낸다.
    /// 사용자는 '수집' 열에 Y를 표시하고 '최대수량'을 채워 다시 올리면 된다.
    /// </summary>
    public byte[] BuildCategoryWorkbook(string supplierCode, IReadOnlyList<SupplierCategory> categories)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("카테고리");

        string[] headers = ["수집", "공급처", "카테고리코드", "카테고리명", "전체경로", "최대수량", "검색어(선택)"];
        int[] widths = [8, 14, 18, 28, 40, 12, 22];
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = i == 0 ? XLColor.LightPink : XLColor.LightGray;
            sheet.Column(i + 1).Width = widths[i];
        }
        sheet.Cell(1, 1).CreateComment().AddText("수집할 카테고리에 Y를 입력하세요.");

        var row = 2;
        foreach (var category in categories)
        {
            sheet.Cell(row, 1).Value = "";
            sheet.Cell(row, 2).Value = supplierCode;
            SetCell(sheet.Cell(row, 3), category.Code);   // 코드는 텍스트로 고정
            sheet.Cell(row, 4).Value = category.Name;
            sheet.Cell(row, 5).Value = category.FullPath ?? category.Name;
            sheet.Cell(row, 6).Value = 100;

            // 조회 불가 분류(대분류 등)는 흐리게 표시하고 Y를 못 넣게 막는다
            if (!category.IsSelectable)
            {
                sheet.Range(row, 1, row, 7).Style.Font.FontColor = XLColor.Gray;
                sheet.Cell(row, 1).CreateComment().AddText("이 분류로는 조회할 수 없습니다. 하위 분류를 선택하세요.");
                sheet.Cell(row, 6).Value = "";
            }
            row++;
        }

        sheet.SheetView.FreezeRows(1);
        if (row > 2)
        {
            sheet.Range(1, 1, row - 1, headers.Length).SetAutoFilter();
            // '수집' 열은 Y/N 드롭다운
            sheet.Range(2, 1, row - 1, 1).CreateDataValidation().List("\"Y,N\"", true);
        }

        var guide = workbook.AddWorksheet("사용법");
        guide.Cell(1, 1).Value = "카테고리 대량 수집 사용법";
        guide.Cell(1, 1).Style.Font.Bold = true;
        guide.Cell(1, 1).Style.Font.FontSize = 14;
        string[] steps =
        [
            "1. '카테고리' 시트에서 수집할 행의 '수집' 열에 Y를 입력합니다.",
            "2. '최대수량'에 해당 카테고리에서 가져올 상품 개수를 적습니다 (1~1000).",
            "3. 특정 키워드로 좁히려면 '검색어(선택)'에 입력합니다.",
            "4. 저장 후 Tetragon의 [수집 → 카테고리 수집 → 엑셀 가져오기]에 업로드합니다.",
            "5. 표시한 카테고리들이 순서대로 대량 수집됩니다.",
            "",
            "※ 이미 수집한 상품은 자동으로 건너뜁니다.",
            "※ 카테고리코드와 공급처 열은 수정하지 마세요.",
            "※ 회색으로 표시된 행은 상위 분류라 조회할 수 없습니다 — 하위 분류를 고르세요.",
        ];
        for (var i = 0; i < steps.Length; i++) guide.Cell(3 + i, 1).Value = steps[i];
        guide.Column(1).Width = 70;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    // ── 카테고리 목록 엑셀 (가져오기) ────────────────────────────────────

    /// <summary>업로드된 카테고리 엑셀에서 '수집=Y'인 행을 읽는다.</summary>
    public IReadOnlyList<CategoryImportRow> ParseCategoryWorkbook(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault(w => w.Name == "카테고리")
                    ?? workbook.Worksheets.First();

        var rows = new List<CategoryImportRow>();
        var errors = new List<string>();

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var selected = row.Cell(1).GetString().Trim();
            if (!selected.Equals("Y", StringComparison.OrdinalIgnoreCase)) continue;

            var supplierCode = row.Cell(2).GetString().Trim();
            var categoryCode = row.Cell(3).GetString().Trim();
            var categoryName = row.Cell(4).GetString().Trim();
            var keyword = row.Cell(7).GetString().Trim();

            if (string.IsNullOrWhiteSpace(supplierCode))
            {
                errors.Add($"{row.RowNumber()}행: 공급처가 비어 있습니다.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(categoryCode) && string.IsNullOrWhiteSpace(keyword))
            {
                errors.Add($"{row.RowNumber()}행: 카테고리코드와 검색어가 모두 비어 있습니다.");
                continue;
            }

            var maxProducts = 100;
            if (row.Cell(6).TryGetValue<double>(out var max) && max >= 1)
                maxProducts = (int)Math.Min(max, 1000);

            rows.Add(new CategoryImportRow(
                supplierCode, categoryCode, categoryName,
                string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                maxProducts, row.RowNumber()));
        }

        if (errors.Count > 0)
            logger.LogWarning("카테고리 엑셀 경고 {Count}건: {Errors}", errors.Count, string.Join(" / ", errors));

        return rows;
    }
}

public sealed record CategoryImportRow(
    string SupplierCode,
    string CategoryCode,
    string CategoryName,
    string? Keyword,
    int MaxProducts,
    int RowNumber);

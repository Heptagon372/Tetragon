namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 마켓별 대량등록 엑셀 양식.
///
/// 국내 마켓 중 상당수(특히 옥션·G마켓)는 상품등록 API를 일반 판매자에게 열어주지 않고
/// 대량등록 엑셀 업로드가 사실상의 표준 경로다. 따라서 엑셀 생성은 부가 기능이 아니라
/// IMarketplaceAdapter와 동등한 등록 경로로 취급한다.
///
/// 새 마켓 양식 = 이 인터페이스 구현 + DI 등록. 기존 코드는 수정하지 않는다.
/// </summary>
public interface IMarketExcelTemplate
{
    /// <summary>마켓 코드 (IMarketplaceAdapter.Code와 동일하면 짝을 이룬다).</summary>
    string MarketCode { get; }
    string DisplayName { get; }
    /// <summary>내려받을 파일명 힌트 (확장자 제외).</summary>
    string FileNameHint { get; }
    /// <summary>업로드 방법 안내 — UI에 그대로 노출된다.</summary>
    string UploadGuide { get; }

    /// <summary>헤더 행. 마켓 양식의 컬럼명과 정확히 일치해야 한다.</summary>
    IReadOnlyList<ExcelColumn> Columns { get; }

    /// <summary>
    /// 상품 1건 → 엑셀 행(들). 옵션이 있으면 여러 행이 될 수 있어 목록으로 반환한다.
    /// 반환 길이는 Columns 길이와 같아야 한다.
    /// </summary>
    IEnumerable<IReadOnlyList<object?>> BuildRows(ExcelRowContext context);
}

/// <summary>엑셀 컬럼 정의.</summary>
public sealed record ExcelColumn(string Header, int Width = 18, bool IsRequired = false, string? Note = null);

/// <summary>엑셀 행 생성에 필요한 상품 정보.</summary>
public sealed record ExcelRowContext
{
    public required ListingPayload Payload { get; init; }
    /// <summary>마켓별 설정값 (카테고리 코드, 배송비, 반품지 등). 자격증명 스코프에서 온다.</summary>
    public required IReadOnlyDictionary<string, string> Settings { get; init; }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}

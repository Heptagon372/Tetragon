namespace Tetragon.Application.Services.Import;

public enum ImportFieldType { Text, Integer, Decimal, Date }

/// <param name="Name">표준 필드명. 프로파일의 column_map이 이 값을 가리킨다.</param>
/// <param name="Label">화면에서 사람이 고르는 이름.</param>
/// <param name="Required">없으면 파일 전체를 거부한다.</param>
/// <param name="Hints">자동 추정에 쓰는 한국어 컬럼명 후보. 추측은 <b>제안</b>일 뿐 파싱 근거가 아니다.</param>
public sealed record ImportFieldSpec(
    string Name,
    string Label,
    ImportFieldType Type,
    bool Required,
    IReadOnlyList<string> Hints);

/// <summary>
/// 임포트 목적별 표준 필드 (확장 08 §3).
///
/// 여기 이름들은 09 <c>listing_perf</c>·10 <c>settlement_lines</c> 스키마와 1:1이다.
/// 프레임은 "마켓 컬럼 → 이 이름"까지만 책임지고, 그 뒤의 적재는 09·10이 한다.
/// </summary>
public static class ImportFieldCatalog
{
    /// <summary>성과 CSV → 09 <c>listing_perf</c>.</summary>
    private static readonly ImportFieldSpec[] Performance =
    [
        new("listingKey", "마켓 상품번호", ImportFieldType.Text, true,
            ["상품번호", "상품코드", "노출상품번호", "옵션상품번호", "판매자상품코드", "itemid", "productno"]),
        new("date", "일자", ImportFieldType.Date, true,
            ["일자", "날짜", "기준일", "통계일", "date"]),
        new("impressions", "노출수", ImportFieldType.Integer, false,
            ["노출수", "노출", "노출횟수", "impression"]),
        new("clicks", "클릭수", ImportFieldType.Integer, false,
            ["클릭수", "클릭", "click"]),
        new("orders", "주문건수", ImportFieldType.Integer, false,
            ["주문수", "주문건수", "결제건수", "order"]),
        new("units", "판매수량", ImportFieldType.Integer, false,
            ["판매수량", "결제수량", "주문수량", "수량", "unit"]),
        new("grossSales", "결제금액", ImportFieldType.Decimal, false,
            ["결제금액", "매출액", "판매금액", "거래액", "sales"]),
        new("cancels", "취소건수", ImportFieldType.Integer, false,
            ["취소수", "취소건수", "환불건수", "cancel"]),
    ];

    /// <summary>정산 CSV → 10 <c>settlement_lines</c>.</summary>
    private static readonly ImportFieldSpec[] Settlement =
    [
        new("settleDate", "정산일", ImportFieldType.Date, true,
            ["정산일", "정산기준일", "지급일", "settledate"]),
        new("marketOrderId", "주문번호", ImportFieldType.Text, true,
            ["주문번호", "주문id", "결제번호", "orderid"]),
        new("marketItemId", "상품번호", ImportFieldType.Text, false,
            ["상품번호", "상품코드", "옵션상품번호", "itemid"]),
        new("optionId", "옵션번호", ImportFieldType.Text, false,
            ["옵션번호", "옵션id", "optionid"]),
        new("lineType", "항목구분", ImportFieldType.Text, true,
            ["항목", "구분", "정산구분", "항목구분", "유형", "type"]),
        new("quantity", "수량", ImportFieldType.Integer, false,
            ["수량", "판매수량", "quantity"]),
        new("gross", "판매금액", ImportFieldType.Decimal, false,
            ["판매금액", "결제금액", "거래액", "gross"]),
        new("fee", "수수료", ImportFieldType.Decimal, false,
            ["수수료", "판매수수료", "서비스이용료", "fee"]),
        new("vat", "부가세", ImportFieldType.Decimal, false,
            ["부가세", "부가가치세", "vat"]),
        new("net", "정산금액", ImportFieldType.Decimal, true,
            ["정산금액", "지급액", "정산액", "실지급액", "net"]),
    ];

    public static IReadOnlyList<ImportFieldSpec> For(string purpose) => purpose switch
    {
        Domain.Imports.ImportPurposes.Performance => Performance,
        Domain.Imports.ImportPurposes.Settlement => Settlement,
        _ => throw new ArgumentException($"알 수 없는 임포트 목적: {purpose}", nameof(purpose)),
    };

    /// <summary>
    /// 정규화된 헤더에 표준 필드를 추정해 붙인다.
    ///
    /// 이 결과는 <b>사람에게 보여줄 제안</b>이지 파싱 근거가 아니다 (08 §3.2).
    /// 추측으로 파싱한 정산 데이터는 틀린 이익을 확신 있게 보여준다.
    /// </summary>
    public static Dictionary<string, string> Suggest(string purpose, IReadOnlyList<string> normalizedHeader)
    {
        var map = new Dictionary<string, string>();
        var taken = new HashSet<string>();

        foreach (var field in For(purpose))
        {
            // 정확히 같은 이름이 먼저, 없으면 포함 관계로 — "결제금액(원)"처럼 단위가 붙는 경우가 흔하다
            var exact = normalizedHeader.FirstOrDefault(
                h => !taken.Contains(h) && field.Hints.Any(hint => h == hint));
            var loose = exact ?? normalizedHeader.FirstOrDefault(
                h => !taken.Contains(h) && field.Hints.Any(hint => h.Contains(hint, StringComparison.Ordinal)));
            if (loose is null) continue;

            map[loose] = field.Name;
            taken.Add(loose);
        }
        return map;
    }
}

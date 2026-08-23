namespace Tetragon.Plugin.Abstractions;

/// <summary>
/// 한국 주소 비교 유틸.
///
/// 같은 장소를 "인천 계양구…"와 "인천광역시 계양구…"로 다르게 적는 일이 흔해서,
/// 문자열을 그대로 비교하면 같은 곳을 다른 곳으로 판단한다.
/// 출고지/반품지 매칭이 어긋나면 반품이 엉뚱한 공급처로 가므로 한 곳에서 규칙을 관리한다.
/// </summary>
public static class KoreanAddress
{
    /// <summary>
    /// 같은 주소로 인정할 최소 일치 길이(정규화 후 글자 수).
    /// "경남양산시물금읍제방로325" 정도면 12자를 넘으므로 시·군·구와 도로명까지 같아야 통과한다.
    /// </summary>
    public const int MinPrefixMatch = 12;

    /// <summary>광역자치단체 표기를 짧은 형태로 통일하고 공백을 없앤다.</summary>
    public static string Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";

        var text = address.Trim();
        foreach (var (full, shortForm) in RegionAliases)
            if (text.StartsWith(full, StringComparison.Ordinal))
            {
                text = shortForm + text[full.Length..];
                break;
            }

        return new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>긴 표기 → 짧은 표기. 긴 것부터 검사해야 '경상남도'가 '경기'에 먹히지 않는다.</summary>
    private static readonly (string Full, string Short)[] RegionAliases =
    [
        ("서울특별시", "서울"), ("부산광역시", "부산"), ("대구광역시", "대구"),
        ("인천광역시", "인천"), ("광주광역시", "광주"), ("대전광역시", "대전"),
        ("울산광역시", "울산"), ("세종특별자치시", "세종"),
        ("경기도", "경기"), ("강원특별자치도", "강원"), ("강원도", "강원"),
        ("충청북도", "충북"), ("충청남도", "충남"),
        ("전라북도", "전북"), ("전북특별자치도", "전북"), ("전라남도", "전남"),
        ("경상북도", "경북"), ("경상남도", "경남"),
        ("제주특별자치도", "제주"), ("제주도", "제주"),
    ];

    public static int CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < length && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>
    /// 두 주소가 같은 장소인지.
    /// 상세주소(호수·층)까지 같을 필요는 없고 시·군·구와 도로명이 같으면 같은 건물로 본다.
    /// </summary>
    public static bool SamePlace(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return CommonPrefixLength(Normalize(a), Normalize(b)) >= MinPrefixMatch;
    }

    public static string OnlyDigits(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    /// <summary>
    /// 신 우편번호(5자리)인지. 구 우편번호(626-812)는 마켓이 받지 않는다.
    /// </summary>
    public static bool IsValidZipcode(string? zipcode) =>
        zipcode is { Length: > 0 } zip && zip.Count(char.IsDigit) == 5 && !zip.Contains('-');
}

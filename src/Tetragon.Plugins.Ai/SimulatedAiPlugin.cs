using System.Text.RegularExpressions;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Plugins.Ai;

/// <summary>
/// 시뮬레이션 AI Provider — API 키 없이 파이프라인 전체를 검증하기 위한 폴백.
/// 자주 쓰이는 중국어 상품 용어 사전 기반 치환 번역.
/// </summary>
public sealed partial class SimulatedAiPlugin : IAiProviderPlugin
{
    public string Code => "simulated-ai";
    public string DisplayName => "시뮬레이션 번역기";
    public string Version => "1.0.0";
    public bool IsLive => false;

    public IReadOnlySet<AiCapability> Capabilities { get; } =
        new HashSet<AiCapability> { AiCapability.Translate, AiCapability.OptionCleanup };

    /// <summary>중국어 → 한국어 상품 용어 사전 (긴 표현부터 치환).</summary>
    private static readonly (string Cn, string Ko)[] Dictionary =
    [
        ("2024新款", "2024 신상"), ("新款", "신상"), ("韩版", "한국st"), ("宽松", "루즈핏"),
        ("显瘦", "슬림"), ("气质", "우아한"), ("连衣裙", "원피스"), ("长裙", "롱스커트"),
        ("北欧风格", "북유럽풍"), ("简约", "심플"), ("陶瓷", "세라믹"), ("马克杯", "머그컵"),
        ("创意", "감성"), ("咖啡杯", "커피잔"), ("无线蓝牙耳机", "무선 블루투스 이어폰"),
        ("运动", "스포츠"), ("跑步", "러닝"), ("双耳", "양쪽"), ("入耳式", "커널형"),
        ("降噪", "노이즈캔슬링"), ("夏季", "여름"), ("男士", "남성"), ("休闲", "캐주얼"),
        ("短袖", "반팔"), ("纯棉", "순면"), ("圆领", "라운드넥"), ("便携式", "휴대용"),
        ("折叠", "폴딩"), ("收纳箱", "수납함"), ("大容量", "대용량"), ("整理箱", "정리함"),
        ("家用", "가정용"), ("儿童", "아동"), ("益智", "지능개발"), ("积木", "블록"),
        ("玩具", "장난감"), ("拼装", "조립"), ("大颗粒", "대형"), ("早教", "유아교육"),
        ("透明", "투명"), ("手机壳", "폰케이스"), ("硅胶", "실리콘"), ("防摔", "충격방지"),
        ("保护套", "보호케이스"), ("厨房", "주방"), ("多功能", "다기능"), ("切菜", "채칼"),
        ("神器", "아이템"), ("不锈钢", "스테인리스"), ("刨丝器", "채썰기"),
        ("高品质", "고품질"), ("材料", "소재"), ("舒适", "편안한"), ("耐用", "내구성"),
        ("工厂直销", "공장직송"), ("支持批发", "도매가능"),
        // 옵션값
        ("颜色", "색상"), ("尺码", "사이즈"), ("白色", "화이트"), ("黑色", "블랙"),
        ("粉色", "핑크"), ("蓝色", "블루"), ("红色", "레드"), ("灰色", "그레이"),
        ("均码", "프리사이즈"), ("加大", "빅사이즈"),
    ];

    public async Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken ct)
    {
        await Task.Delay(Random.Shared.Next(200, 600), ct); // API 지연 시뮬레이션

        var results = request.Texts.Select(text => request.Capability switch
        {
            AiCapability.Translate => Translate(text),
            AiCapability.OptionCleanup => Translate(text),
            _ => text,
        }).ToList();

        return AiResult.Ok(results);
    }

    private static string Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var result = text;
        foreach (var (cn, ko) in Dictionary.OrderByDescending(d => d.Cn.Length))
            result = result.Replace(cn, ko);

        // 남은 한자 제거 후 공백 정리 (번역 실패 부분 정돈)
        result = HanRegex().Replace(result, "");
        result = WhitespaceRegex().Replace(result, " ").Trim();

        return string.IsNullOrWhiteSpace(result) ? text : result;
    }

    [GeneratedRegex(@"[一-鿿]+")]
    private static partial Regex HanRegex();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();
}

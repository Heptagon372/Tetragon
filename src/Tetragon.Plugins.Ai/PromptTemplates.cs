using Tetragon.Plugin.Abstractions;

namespace Tetragon.Plugins.Ai;

/// <summary>
/// 프롬프트 템플릿 (설계서 5.3 — 코드 하드코딩 대신 버전 관리 대상).
/// 실제 운영에서는 PromptTemplate 테이블로 이관한다.
/// </summary>
internal static class PromptTemplates
{
    public const string Version = "1.0.0";

    public static (string System, string User) Build(AiRequest request) => request.Capability switch
    {
        AiCapability.Translate => (TranslateSystem, BuildNumbered(request)),
        AiCapability.ProductNameSeo => (SeoSystem, BuildNumbered(request)),
        AiCapability.OptionCleanup => (OptionSystem, BuildNumbered(request)),
        _ => (TranslateSystem, BuildNumbered(request)),
    };

    private const string TranslateSystem = """
        당신은 해외 상품을 한국 오픈마켓에 등록하기 위한 전문 번역가입니다.

        규칙:
        1. 입력의 각 항목을 한국어로 번역해 results 배열에 같은 순서로 반환합니다.
        2. 항목 수는 입력과 정확히 같아야 합니다. 빈 입력은 빈 문자열로 반환합니다.
        3. 상품명은 한국 쇼핑몰 검색에 자연스러운 표현으로 의역합니다. 기계번역투 금지.
        4. 브랜드명·모델명·규격(cm, ml, XL 등)은 그대로 유지합니다.
        5. 과장광고 표현(최고, 1위, 최저가), 의료 효능 표현은 사용하지 않습니다.
        6. 옵션값(색상/사이즈 등)은 짧은 한국어 단어로 번역합니다. 예: 白色→화이트, 均码→프리사이즈
        7. 번역 외의 설명이나 주석은 절대 추가하지 않습니다.
        """;

    private const string SeoSystem = """
        당신은 한국 오픈마켓 상품명 최적화 전문가입니다.

        입력된 상품명을 검색 노출에 유리하도록 다듬어 results 배열에 반환합니다.

        규칙:
        1. 50자 이내. 핵심 키워드를 앞쪽에 배치합니다.
        2. 구성: [카테고리/용도] [소재/특징] [상품유형] 순서를 권장합니다.
        3. 특수문자(★, ♥, [], ★★)와 중복 단어는 제거합니다.
        4. 과장광고(최저가, 정품, 1위, 명품), 상표권 침해 브랜드명은 절대 넣지 않습니다.
        5. 항목 수는 입력과 정확히 같아야 합니다. 설명은 추가하지 않습니다.
        """;

    private const string OptionSystem = """
        당신은 상품 옵션명 정규화 전문가입니다.
        입력된 옵션명을 한국 쇼핑몰에서 통용되는 짧고 명확한 표현으로 정리해
        results 배열에 같은 순서로 반환합니다. 설명은 추가하지 않습니다.
        """;

    private static string BuildNumbered(AiRequest request)
    {
        var lines = request.Texts.Select((t, i) => $"{i + 1}. {(string.IsNullOrWhiteSpace(t) ? "(빈 항목)" : t)}");
        var context = string.IsNullOrWhiteSpace(request.Context) ? "" : $"\n\n[상품 카테고리 참고] {request.Context}";
        return $"""
            아래 {request.Texts.Count}개 항목을 처리해 results 배열로 반환하세요.
            반드시 {request.Texts.Count}개를 순서대로 반환해야 합니다.{context}

            {string.Join("\n", lines)}
            """;
    }
}

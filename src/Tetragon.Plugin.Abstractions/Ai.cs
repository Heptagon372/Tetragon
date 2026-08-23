namespace Tetragon.Plugin.Abstractions;

/// <summary>AI Provider 플러그인 계약 (설계서 5.3).</summary>
public interface IAiProviderPlugin : IPlugin
{
    IReadOnlySet<AiCapability> Capabilities { get; }
    Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken ct);
}

public enum AiCapability
{
    Translate,
    ProductNameSeo,
    OptionCleanup,
}

/// <summary>능력 + 테넌트 정책 기반 Provider 선택 (Strategy, 설계서 5.3 IAiRouter).</summary>
public interface IAiRouter
{
    IAiProviderPlugin Route(AiCapability capability, string tenantId);
}

public sealed record AiRequest
{
    public required AiCapability Capability { get; init; }
    public required string SourceLocale { get; init; }
    public required string TargetLocale { get; init; }
    /// <summary>번역/정제할 텍스트 목록 (배치 처리로 호출 수 절감).</summary>
    public required List<string> Texts { get; init; }
    /// <summary>상품 맥락 (카테고리 등) — 프롬프트 품질용.</summary>
    public string? Context { get; init; }
}

public sealed record AiResult
{
    public bool Success { get; init; }
    public List<string> Texts { get; init; } = [];
    public string? Error { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }

    public static AiResult Ok(List<string> texts, int inTok = 0, int outTok = 0) =>
        new() { Success = true, Texts = texts, InputTokens = inTok, OutputTokens = outTok };
    public static AiResult Fail(string error) => new() { Success = false, Error = error };
}

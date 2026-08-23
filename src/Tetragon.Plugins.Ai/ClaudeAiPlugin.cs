using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;
using ICredentialProvider = Tetragon.Plugin.Abstractions.ICredentialProvider;

namespace Tetragon.Plugins.Ai;

/// <summary>
/// Anthropic Claude 기반 번역/SEO Provider (실연동, 설계서 5.3).
/// 필요 자격증명: api_key (설정 → AI → claude).
/// 프롬프트는 PromptTemplates에서 관리 (설계서 5.3 — 코드 하드코딩 금지 원칙의 최소 구현).
/// </summary>
public sealed class ClaudeAiPlugin(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<ClaudeAiPlugin> logger) : IAiProviderPlugin
{
    public string Code => "claude";
    public string DisplayName => "Claude (Anthropic)";
    public string Version => "1.0.0";
    public bool IsLive => true;
    /// <summary>API 키가 등록돼야 사용 가능. 없으면 라우터가 시뮬레이션으로 폴백한다.</summary>
    public bool IsAvailable => credentials.HasKey("ai:claude", "api_key");

    public IReadOnlySet<AiCapability> Capabilities { get; } =
        new HashSet<AiCapability> { AiCapability.Translate, AiCapability.ProductNameSeo, AiCapability.OptionCleanup };

    private const string Model = "claude-opus-5";
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";

    public async Task<AiResult> ExecuteAsync(AiRequest request, CancellationToken ct)
    {
        var apiKey = credentials.Get("ai:claude", "api_key");
        if (string.IsNullOrWhiteSpace(apiKey))
            return AiResult.Fail("Claude API 키가 없습니다. 설정 → AI → claude에 api_key를 등록하세요.");

        if (request.Texts.Count == 0) return AiResult.Ok([]);

        var (system, user) = PromptTemplates.Build(request);

        try
        {
            var client = httpClientFactory.CreateClient("claude");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            httpRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            httpRequest.Content = JsonContent.Create(new
            {
                model = Model,
                max_tokens = 8000,
                system,
                messages = new[] { new { role = "user", content = user } },
                // 구조화 출력: 입력 순서와 1:1 대응하는 문자열 배열 강제
                output_config = new
                {
                    format = new
                    {
                        type = "json_schema",
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                results = new { type = "array", items = new { type = "string" } },
                            },
                            required = new[] { "results" },
                            additionalProperties = false,
                        },
                    },
                },
            });

            using var response = await client.SendAsync(httpRequest, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Claude API 오류 {Status}: {Body}", (int)response.StatusCode, Truncate(raw, 300));
                return AiResult.Fail($"Claude API 오류 (HTTP {(int)response.StatusCode}): {ExtractError(raw)}");
            }

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // 안전장치: 거절(refusal) 처리
            if (root.TryGetProperty("stop_reason", out var stopReason) && stopReason.GetString() == "refusal")
                return AiResult.Fail("Claude가 요청을 거절했습니다 (안전 정책). 상품 내용을 확인하세요.");

            var text = root.GetProperty("content")
                .EnumerateArray()
                .Where(b => b.GetProperty("type").GetString() == "text")
                .Select(b => b.GetProperty("text").GetString())
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

            if (text is null) return AiResult.Fail("Claude 응답에 텍스트가 없습니다.");

            using var resultDoc = JsonDocument.Parse(text);
            var results = resultDoc.RootElement.GetProperty("results")
                .EnumerateArray().Select(e => e.GetString() ?? "").ToList();

            var usage = root.GetProperty("usage");
            var inTok = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0;
            var outTok = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0;

            // 입력 개수와 다르면 부족분을 원문으로 채운다 (파이프라인 중단 방지)
            while (results.Count < request.Texts.Count)
                results.Add(request.Texts[results.Count]);

            logger.LogInformation("Claude {Capability} 완료: {Count}건 (in {In} / out {Out} 토큰)",
                request.Capability, results.Count, inTok, outTok);
            return AiResult.Ok(results, inTok, outTok);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude 호출 실패");
            return AiResult.Fail(ex.Message);
        }
    }

    private static string ExtractError(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var message))
                return message.GetString() ?? raw;
        }
        catch (JsonException) { }
        return Truncate(raw, 200);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

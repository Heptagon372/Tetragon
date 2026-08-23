using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Tetragon.Plugin.Abstractions;

namespace Tetragon.Infrastructure.External;

/// <summary>
/// <see cref="IBrowserFetcher"/> 구현 — 외부 fetch 사이드카(tetragon-fetch, Playwright)를 호출한다.
///
/// 사이드카 REST 계약: POST {baseUrl}/fetch, 헤더 X-Api-Key, body/resp는 아래 DTO.
/// 설정은 기존 <see cref="ICredentialProvider"/>의 "fetch" 스코프에서 읽는다
/// (설정 화면 재사용). baseUrl 미설정이면 IsAvailable=false → 플러그인은 폴백하지 않는다.
///
/// 설계: docs/ANTIBOT-FETCH-DESIGN.md §2.2
/// </summary>
public sealed class HttpBrowserFetcher(
    IHttpClientFactory httpClientFactory,
    ICredentialProvider credentials,
    ILogger<HttpBrowserFetcher> logger) : IBrowserFetcher
{
    private const string Scope = "fetch";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool IsAvailable => credentials.HasKey(Scope, "baseUrl");

    public async Task<BrowserFetchResult> FetchAsync(BrowserFetchRequest request, CancellationToken ct)
    {
        var baseUrl = credentials.Get(Scope, "baseUrl")
            ?? throw new TransientScrapeException("fetch 사이드카 baseUrl이 설정되지 않았습니다 (설정 → 시스템 → fetch).");

        var client = httpClientFactory.CreateClient("fetch-sidecar");
        var apiKey = credentials.Get(Scope, "apiKey");

        // 공급처별 프록시 접속 문자열을 정책 키로 조회 (예: "proxy.residential-kr").
        // 없으면 사이드카의 기본 프록시(PROXY 환경변수)를 쓰게 null로 보낸다.
        var proxy = credentials.Get(Scope, $"proxy.{request.ProxyPolicy}");

        var payload = new FetchRequestDto
        {
            Url = request.Url.ToString(),
            WaitForSelector = request.WaitForSelector,
            Behavior = request.Behavior.ToString(),
            CookieHeader = request.CookieHeader,
            Proxy = proxy,
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/fetch")
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            httpReq.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpReq, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // 사이드카 다운/타임아웃 — 일시적으로 보고 파이프라인 재시도에 맡긴다.
            throw new TransientScrapeException($"fetch 사이드카 호출 실패: {ex.Message}", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if ((int)response.StatusCode >= 500)
                throw new TransientScrapeException($"fetch 사이드카 오류: HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                throw new TransientScrapeException($"fetch 사이드카 응답 이상: HTTP {(int)response.StatusCode} {body}");

            var dto = JsonSerializer.Deserialize<FetchResultDto>(body, JsonOpts)
                ?? throw new TransientScrapeException("fetch 사이드카 응답을 파싱할 수 없습니다.");

            if (dto.Blocked)
                logger.LogWarning(
                    "브라우저 fetch 차단됨 — {Url} (프록시 정책 {Policy}). 프록시 소진/센서 미통과 가능.",
                    request.Url, request.ProxyPolicy);

            return new BrowserFetchResult
            {
                StatusCode = dto.StatusCode,
                Html = dto.Html ?? "",
                FinalUrl = dto.FinalUrl ?? request.Url.ToString(),
                SetCookie = dto.SetCookie,
                ChallengeSolved = dto.ChallengeSolved,
                Blocked = dto.Blocked,
            };
        }
    }

    // ── 사이드카 REST DTO (server.js와 필드 일치) ──
    private sealed record FetchRequestDto
    {
        public required string Url { get; init; }
        public string? WaitForSelector { get; init; }
        public string Behavior { get; init; } = "Human";
        public string? CookieHeader { get; init; }
        public string? Proxy { get; init; }
    }

    private sealed record FetchResultDto
    {
        public int StatusCode { get; init; }
        public string? Html { get; init; }
        public string? FinalUrl { get; init; }
        public string? SetCookie { get; init; }
        public bool ChallengeSolved { get; init; }
        public bool Blocked { get; init; }
    }
}

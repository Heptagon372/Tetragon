using System.Net;

namespace Tetragon.Plugins.Suppliers;

/// <summary>스크래퍼 공용 HTTP 헬퍼 — 브라우저 유사 헤더.</summary>
internal static class ScraperHttp
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public static HttpRequestMessage BuildRequest(Uri url, string? cookie = null, string? referer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9,zh-CN;q=0.8,en;q=0.7");
        if (!string.IsNullOrWhiteSpace(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (referer is not null)
            request.Headers.TryAddWithoutValidation("Referer", referer);
        return request;
    }

    public static bool LooksBlocked(string html) =>
        html.Contains("captcha", StringComparison.OrdinalIgnoreCase)
        || html.Contains("punish", StringComparison.OrdinalIgnoreCase)
        || html.Contains("verify", StringComparison.OrdinalIgnoreCase) && html.Length < 20_000
        || html.Contains("login", StringComparison.OrdinalIgnoreCase) && html.Length < 15_000;
}

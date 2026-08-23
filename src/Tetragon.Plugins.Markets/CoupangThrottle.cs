using Microsoft.Extensions.Logging;

namespace Tetragon.Plugins.Markets;

/// <summary>
/// 쿠팡 API 호출 조절 + 일시적 실패 재시도.
///
/// 대량등록에서 상품 여러 건을 동시에 처리하면 상품 하나당
/// 카테고리 추천 → 카테고리 메타 → 출고지 조회/생성 → 반품지 조회/생성 → 상품 등록
/// 으로 5~6번을 호출한다. 4건만 동시에 돌려도 20여 요청이 순식간에 나가고,
/// 쿠팡은 이를 스로틀링해 <b>빈 응답</b>을 돌려준다.
///
/// 빈 응답은 JSON 파싱 단계에서 "The input does not contain any JSON tokens"로 터지는데,
/// 원인이 드러나지 않아 "서로 등록이 안 되는" 것처럼 보인다.
/// 실제로 대량등록 4건 중 2건이 이 이유로 실패했다.
///
/// 그래서 동시 호출 수를 묶고, 호출 사이에 최소 간격을 두고,
/// 빈 응답은 일시적 오류로 보고 재시도한다.
/// </summary>
public sealed class CoupangThrottle(ILogger<CoupangThrottle> logger)
{
    /// <summary>동시에 나갈 수 있는 쿠팡 요청 수.</summary>
    private const int MaxConcurrent = 2;
    /// <summary>연속 호출 사이 최소 간격.</summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(350);
    /// <summary>빈 응답·5xx일 때 재시도 횟수.</summary>
    private const int MaxRetries = 3;

    private readonly SemaphoreSlim _gate = new(MaxConcurrent, MaxConcurrent);
    private readonly SemaphoreSlim _spacing = new(1, 1);
    private DateTimeOffset _lastCallAt = DateTimeOffset.MinValue;

    /// <summary>
    /// 쿠팡 호출을 조절해 실행한다.
    /// 응답 본문이 비었거나 5xx면 잠시 쉬었다 다시 시도한다.
    /// </summary>
    public async Task<(HttpResponseMessage Response, string Body)> ExecuteAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        string operation,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                await SpaceOutAsync(ct);

                var response = await send(ct);
                var body = await response.Content.ReadAsStringAsync(ct);

                var transient = string.IsNullOrWhiteSpace(body) || (int)response.StatusCode >= 500;
                if (!transient || attempt > MaxRetries)
                {
                    if (transient)
                        logger.LogWarning(
                            "쿠팡 {Operation} — {Attempts}번 시도 후에도 응답이 비었습니다 (HTTP {Status}). " +
                            "요청이 몰려 스로틀링된 것으로 보입니다.",
                            operation, attempt, (int)response.StatusCode);
                    return (response, body);
                }

                // 재시도 간격을 점점 늘린다 (0.8s → 1.6s → 3.2s)
                var backoff = TimeSpan.FromMilliseconds(800 * Math.Pow(2, attempt - 1));
                logger.LogInformation(
                    "쿠팡 {Operation} 빈 응답 — {Backoff:N1}초 후 재시도 ({Attempt}/{Max})",
                    operation, backoff.TotalSeconds, attempt, MaxRetries);
                response.Dispose();
                await Task.Delay(backoff, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>직전 호출로부터 최소 간격이 지나도록 기다린다.</summary>
    private async Task SpaceOutAsync(CancellationToken ct)
    {
        await _spacing.WaitAsync(ct);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastCallAt;
            if (elapsed < MinInterval)
                await Task.Delay(MinInterval - elapsed, ct);
            _lastCallAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _spacing.Release();
        }
    }
}

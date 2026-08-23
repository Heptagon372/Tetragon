using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;

namespace Tetragon.Infrastructure.External;

/// <summary>
/// 실시간 환율 Provider (설계서 5.4 IExchangeRateProvider — 캐시 1시간).
/// open.er-api.com (무료, 키 불필요) 사용. 실패 시 정적 폴백 환율.
/// </summary>
public sealed class OpenErApiExchangeRateProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenErApiExchangeRateProvider> logger) : IExchangeRateProvider
{
    private static readonly Dictionary<string, (decimal Rate, DateTimeOffset FetchedAt)> Cache = [];
    private static readonly SemaphoreSlim CacheLock = new(1);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>API 장애 시 폴백 (보수적 근사치).</summary>
    private static readonly Dictionary<string, decimal> Fallback = new()
    {
        ["CNY-KRW"] = 195m,
        ["USD-KRW"] = 1400m,
        ["JPY-KRW"] = 9.3m,
        ["EUR-KRW"] = 1550m,
    };

    public async Task<decimal> GetRateAsync(string from, string to, CancellationToken ct)
    {
        if (from == to) return 1m;
        var key = $"{from}-{to}";

        await CacheLock.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt < Ttl)
                return cached.Rate;

            try
            {
                var client = httpClientFactory.CreateClient("exchange-rate");
                var response = await client.GetFromJsonAsync<ErApiResponse>(
                    $"https://open.er-api.com/v6/latest/{from}", ct);

                if (response?.Result == "success" && response.Rates.TryGetValue(to, out var rate) && rate > 0)
                {
                    Cache[key] = (rate, DateTimeOffset.UtcNow);
                    logger.LogInformation("환율 갱신: {From}→{To} = {Rate}", from, to, rate);
                    return rate;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "환율 API 실패 — 폴백 사용 ({Key})", key);
            }

            if (Cache.TryGetValue(key, out var stale)) return stale.Rate; // 만료됐어도 폴백보단 낫다
            return Fallback.GetValueOrDefault(key, 1m);
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private sealed record ErApiResponse(string Result, Dictionary<string, decimal> Rates)
    {
        public ErApiResponse() : this("", []) { }
    }
}

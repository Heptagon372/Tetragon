using Tetragon.Application.Services;

namespace Tetragon.Api;

/// <summary>
/// 주의 원장 스윕 (확장 08 §2 `attention.sweep`, 시간당 1회).
///
/// 하는 일은 둘이다.
///   ① 지금 코드가 아는 사실을 원장에 싣는다 (새 사실은 열고, 이미 있는 사실은 seen_count만 올린다)
///   ② <b>사라진 사실을 자동으로 닫는다</b> — 이쪽이 더 중요하다.
///      사람이 손대지 않아도 닫히는 항목이 있어야 목록이 신뢰를 얻는다.
///      직접 닫아야만 사라지는 목록은 며칠 만에 "안 보는 목록"이 된다.
///
/// v2의 `work_items` 상태머신이 들어오면 이 워커는 그 루프의 kind 하나로 흡수된다
/// (08 §2 — "새 워커를 만들지 않는다"). 그전까지의 임시 거처다.
/// </summary>
public sealed class AttentionSweepWorker(
    IServiceProvider services,
    ILogger<AttentionSweepWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 기동 직후에는 초기화(스키마·시드)가 끝나길 기다린다
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var scan = scope.ServiceProvider.GetRequiredService<AttentionScan>();
                var result = await scan.RunAsync(stoppingToken);

                if (result.Opened + result.AutoClosed > 0)
                    logger.LogInformation(
                        "주의 스윕 — 신규 {Opened}건 · 자동 닫힘 {Closed}건",
                        result.Opened, result.AutoClosed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 스윕이 죽으면 목록이 조용히 낡는다 — 무슨 일이 있어도 다음 주기로 넘어간다
                logger.LogError(ex, "주의 스윕 오류 — 다음 주기에 다시 시도합니다.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

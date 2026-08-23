using Tetragon.Application.Ports;
using Tetragon.Application.Services;

namespace Tetragon.Api;

/// <summary>
/// 주문 이행 자동화를 주기적으로 돌리는 워커.
///
/// 정책이 꺼져 있으면 아무것도 하지 않고 다음 주기를 기다린다.
/// 주기는 정책의 IntervalMinutes를 따르되, 꺼져 있을 때는 짧게 확인만 한다
/// (사용자가 화면에서 켜면 곧바로 돌기 시작해야 하기 때문).
/// </summary>
public sealed class FulfillmentWorker(
    IServiceProvider services,
    ILogger<FulfillmentWorker> logger) : BackgroundService
{
    /// <summary>자동화가 꺼져 있을 때 정책을 다시 확인하는 간격.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 기동 직후에는 초기화(스키마·시드)가 끝나길 기다린다
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = IdlePoll;
            try
            {
                using var scope = services.CreateScope();
                var policies = scope.ServiceProvider.GetRequiredService<IAutomationPolicyRepository>();
                var policy = await policies.GetOrCreateAsync(stoppingToken);

                if (policy.CanRun)
                {
                    var automation = scope.ServiceProvider.GetRequiredService<FulfillmentAutomation>();
                    var run = await automation.RunAsync(manualTrigger: false, stoppingToken);

                    logger.LogInformation("자동 이행 {Mode}: {Summary}",
                        run.DryRun ? "[시험]" : "[운영]", run.Summary);

                    wait = TimeSpan.FromMinutes(policy.IntervalMinutes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 워커가 죽으면 자동화가 조용히 멈춘다 — 무슨 일이 있어도 다음 주기로 넘어간다
                logger.LogError(ex, "자동 이행 워커 오류 — 다음 주기에 다시 시도합니다.");
            }

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tetragon.Application.Ports;
using Tetragon.SharedKernel;

namespace Tetragon.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ 대응 인메모리 이벤트버스 (설계서 ADR-002의 로컬 구현).
/// Topic Exchange 대신 타입 기반 라우팅. IEventBus 포트 뒤에 있어 RabbitMQ로 교체 가능.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly Channel<IntegrationEvent> _channel =
        Channel.CreateUnbounded<IntegrationEvent>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<IntegrationEvent> Reader => _channel.Reader;

    public Task PublishAsync(IntegrationEvent @event, CancellationToken ct = default)
    {
        if (!_channel.Writer.TryWrite(@event))
            throw new InvalidOperationException("이벤트버스 쓰기 실패");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 이벤트 디스패처 워커. DI에서 IIntegrationEventHandler&lt;T&gt;를 해석해 호출한다.
/// 소비자 멱등성: EventId 기반 중복 처리 방지 (설계서 6.2).
/// 병렬도 4 — 이벤트 단위로 스코프(DbContext) 분리.
/// </summary>
public sealed class EventDispatcherWorker(
    InMemoryEventBus bus,
    IServiceScopeFactory scopeFactory,
    ILogger<EventDispatcherWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly ConcurrentDictionary<Guid, byte> _processed = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var @event in bus.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_processed.TryAdd(@event.EventId, 0))
                continue; // 멱등성: 이미 처리된 이벤트

            await _concurrency.WaitAsync(stoppingToken);
            _ = ProcessAsync(@event, stoppingToken)
                .ContinueWith(_ => _concurrency.Release(), TaskScheduler.Default);
        }
    }

    private async Task ProcessAsync(IntegrationEvent @event, CancellationToken ct)
    {
        var eventType = @event.GetType();
        var handlerType = typeof(IIntegrationEventHandler<>).MakeGenericType(eventType);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var handlers = scope.ServiceProvider.GetServices(handlerType);
            foreach (var handler in handlers)
            {
                if (handler is null) continue;
                var method = handlerType.GetMethod("HandleAsync")!;
                await (Task)method.Invoke(handler, [@event, ct])!;
            }
        }
        catch (Exception ex)
        {
            // 핸들러 내부에서 실패 처리(재시도/DLQ)를 담당하므로 여기 도달은 인프라 오류
            logger.LogError(ex, "이벤트 디스패치 실패: {EventType} ({EventId})", eventType.Name, @event.EventId);
        }
    }
}

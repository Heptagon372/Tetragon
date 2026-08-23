using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Tetragon.Application.Ports;

namespace Tetragon.Infrastructure.Messaging;

/// <summary>SSE 구독자들에게 파이프라인 진행 상황을 브로드캐스트 (설계서 5.9 실시간 지표의 로컬 구현).</summary>
public sealed class PipelineNotifier : IPipelineNotifier
{
    private readonly ConcurrentDictionary<Guid, Channel<PipelineNotification>> _subscribers = new();
    private readonly ConcurrentQueue<PipelineNotification> _recent = new();

    public IReadOnlyList<PipelineNotification> Recent => [.. _recent];

    public void Notify(PipelineNotification notification)
    {
        _recent.Enqueue(notification);
        while (_recent.Count > 200) _recent.TryDequeue(out _);

        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(notification);
    }

    public async IAsyncEnumerable<PipelineNotification> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<PipelineNotification>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers[id] = channel;
        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(ct))
                yield return notification;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}

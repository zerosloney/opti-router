using System.Collections.Concurrent;

namespace OptiRouter.Mesh;

/// <summary>
/// 内存态分布式网格总线实现（基于 ConcurrentDictionary 与强类型委托）。
/// 支撑零外部依赖部署、本地单元测试、以及单宿主多网关虚拟节点状态同步。
/// </summary>
public sealed class InMemoryDistributedStateMesh : IDistributedStateMesh
{
    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private int _disposed;

        public Subscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _unsubscribe();
            }
        }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Delegate>> _channels = new(StringComparer.OrdinalIgnoreCase);
    private long _publishedCount;
    private long _receivedCount;

    public string NodeId { get; }

    public InMemoryDistributedStateMesh(string? nodeId = null)
    {
        NodeId = string.IsNullOrWhiteSpace(nodeId)
            ? $"node-{Guid.NewGuid().ToString("N")[..8]}"
            : nodeId;
    }

    public Task PublishAsync<TEvent>(string channel, TEvent payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channel) || payload == null)
            return Task.CompletedTask;

        Interlocked.Increment(ref _publishedCount);

        if (_channels.TryGetValue(channel, out var subscribers))
        {
            foreach (var kvp in subscribers)
            {
                if (kvp.Value is Action<TEvent> handler)
                {
                    try
                    {
                        Interlocked.Increment(ref _receivedCount);
                        handler(payload);
                    }
                    catch
                    {
                        // 隔离单订阅者异常，防止影响整个网格广播
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    public IDisposable Subscribe<TEvent>(string channel, Action<TEvent> onReceived)
    {
        if (string.IsNullOrWhiteSpace(channel))
            throw new ArgumentException("Channel cannot be null or whitespace.", nameof(channel));
        if (onReceived == null)
            throw new ArgumentNullException(nameof(onReceived));

        var subscribers = _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<Guid, Delegate>());
        var subId = Guid.NewGuid();
        subscribers[subId] = onReceived;

        return new Subscription(() =>
        {
            if (_channels.TryGetValue(channel, out var currentSubs))
            {
                currentSubs.TryRemove(subId, out _);
            }
        });
    }

    public MeshStats GetStats()
    {
        int totalSubs = _channels.Values.Sum(dict => dict.Count);
        return new MeshStats(
            NodeId: NodeId,
            PublishedEventsCount: Interlocked.Read(ref _publishedCount),
            ReceivedEventsCount: Interlocked.Read(ref _receivedCount),
            ActiveSubscriptionsCount: totalSubs);
    }
}

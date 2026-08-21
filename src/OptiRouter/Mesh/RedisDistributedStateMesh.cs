using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Mesh;

/// <summary>
/// 基于 Redis pub/sub 的分布式状态网格实现 (Redis-backed Distributed State Mesh)。
/// 多网关实例共享同一 Redis 时，事件经 JSON 序列化后广播到所有节点，
/// 由 <see cref="DistributedMeshSynchronizer"/> 按 SenderNodeId 过滤回环。
/// Redis 回调线程不执行用户 handler（会阻塞连接），投递到线程池异步执行；
/// handler 异常被捕获并记错误日志——静默吞掉会造成本地与远端状态静默分歧且无从排查。
/// </summary>
public sealed class RedisDistributedStateMesh : IDistributedStateMesh
{
    private readonly IRedisChannelBus _bus;
    private readonly ILogger<RedisDistributedStateMesh>? _logger;
    private readonly object _lock = new();
    private readonly List<(string Channel, IDisposable Subscription)> _subscriptions = new();
    private long _publishedCount;
    private long _receivedCount;

    /// <inheritdoc />
    public string NodeId { get; }

    public RedisDistributedStateMesh(IRedisChannelBus bus, string? nodeId = null,
        ILogger<RedisDistributedStateMesh>? logger = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger;
        NodeId = string.IsNullOrWhiteSpace(nodeId)
            ? $"node-{Guid.NewGuid().ToString("N")[..8]}"
            : nodeId;
    }

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(string channel, TEvent payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channel) || payload == null)
            return Task.CompletedTask;

        Interlocked.Increment(ref _publishedCount);
        string json = JsonSerializer.Serialize(payload);
        return _bus.PublishAsync(channel, json, ct);
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(string channel, Action<TEvent> onReceived)
    {
        ArgumentNullException.ThrowIfNull(onReceived);

        var sub = _bus.Subscribe(channel, json =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<TEvent>(json);
                if (evt is not null)
                {
                    Interlocked.Increment(ref _receivedCount);
                    // Redis 回调线程不能执行用户 handler（阻塞会拖垮连接），投递线程池。
                    // handler 异常必须可观测：记错误日志而非任其成为 UnobservedTaskException——
                    // 否则本地状态（KV 前缀/卡尔曼/账本/弹性）与远端静默分歧且无从排查。
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            onReceived(evt);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError(ex,
                                "Mesh handler failed on channel {Channel} (node {NodeId}); local state may diverge from remote",
                                channel, NodeId);
                        }
                    });
                }
            }
            catch
            {
                // 单条消息反序列化失败不影响其他消息
            }
        });

        lock (_lock)
        {
            _subscriptions.Add((channel, sub));
        }
        return new SubscriptionHandle(sub, () =>
        {
            lock (_lock)
            {
                _subscriptions.RemoveAll(s => ReferenceEquals(s.Subscription, sub));
            }
        });
    }

    /// <inheritdoc />
    public MeshStats GetStats()
    {
        int activeSubscriptions;
        lock (_lock)
        {
            activeSubscriptions = _subscriptions.Count;
        }
        return new MeshStats(
            NodeId,
            Interlocked.Read(ref _publishedCount),
            Interlocked.Read(ref _receivedCount),
            activeSubscriptions);
    }

    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly IDisposable _sub;
        private readonly Action _onDispose;
        private int _disposed;

        public SubscriptionHandle(IDisposable sub, Action onDispose)
        {
            _sub = sub;
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _sub.Dispose();
                _onDispose();
            }
        }
    }
}

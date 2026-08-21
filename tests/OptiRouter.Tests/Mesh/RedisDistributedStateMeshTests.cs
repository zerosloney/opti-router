using System.Collections.Concurrent;
using OptiRouter.Mesh;
using Xunit;

namespace OptiRouter.Tests.Mesh;

/// <summary>
/// 内存版 Redis pub/sub 总线，用于在无 Redis 环境下测试网格逻辑。
/// </summary>
internal sealed class FakeRedisChannelBus : IRedisChannelBus
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<string>>> _subscribers = new(StringComparer.Ordinal);

    public Task PublishAsync(string channel, string payload, CancellationToken ct = default)
    {
        if (_subscribers.TryGetValue(channel, out var handlers))
        {
            foreach (var handler in handlers.Values)
            {
                handler(payload);
            }
        }
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(string channel, Action<string> onMessage)
    {
        var handlers = _subscribers.GetOrAdd(channel, _ => new ConcurrentDictionary<Guid, Action<string>>());
        var id = Guid.NewGuid();
        handlers[id] = onMessage;
        return new FakeSubscription(() => handlers.TryRemove(id, out _));
    }

    private sealed class FakeSubscription(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}

public sealed class RedisDistributedStateMeshTests
{
    private const string ChannelKvCache = "optirouter:mesh:kvcache";

    [Fact]
    public async Task PublishAsync_SubscribedNode_ReceivesDeserializedEvent()
    {
        var bus = new FakeRedisChannelBus();
        var nodeA = new RedisDistributedStateMesh(bus, "node-a");
        var nodeB = new RedisDistributedStateMesh(bus, "node-b");

        var received = new TaskCompletionSource<KvCachePrefixSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        nodeB.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, evt => received.TrySetResult(evt));

        var sent = new KvCachePrefixSyncEvent(
            nodeA.NodeId,
            new[] { "token-1", "token-2", "token-3" },
            "gpt-4o",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await nodeA.PublishAsync(ChannelKvCache, sent);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(nodeA.NodeId, got.SenderNodeId);
        Assert.Equal(new[] { "token-1", "token-2", "token-3" }, got.Tokens);
        Assert.Equal("gpt-4o", got.ModelName);
        Assert.Equal(sent.Timestamp, got.Timestamp);
    }

    [Fact]
    public async Task PublishAsync_DifferentChannels_RouteByEventType()
    {
        var bus = new FakeRedisChannelBus();
        var nodeA = new RedisDistributedStateMesh(bus, "node-a");
        var nodeB = new RedisDistributedStateMesh(bus, "node-b");

        var kvReceived = new TaskCompletionSource<KvCachePrefixSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var kalmanReceived = new TaskCompletionSource<KalmanLatencySyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        nodeB.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, evt => kvReceived.TrySetResult(evt));
        nodeB.Subscribe<KalmanLatencySyncEvent>("optirouter:mesh:kalman", evt => kalmanReceived.TrySetResult(evt));

        await nodeA.PublishAsync(ChannelKvCache,
            new KvCachePrefixSyncEvent("node-a", new[] { "a", "b", "c" }, "m", DateTimeOffset.UtcNow));
        await nodeA.PublishAsync("optirouter:mesh:kalman",
            new KalmanLatencySyncEvent("node-a", "m", 123.5, DateTimeOffset.UtcNow));

        var kv = await kvReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var kalman = await kalmanReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, kv.Tokens.Count);
        Assert.Equal(123.5, kalman.ObservedLatencyMs);
    }

    [Fact]
    public async Task Unsubscribe_StopsReceivingEvents()
    {
        var bus = new FakeRedisChannelBus();
        var nodeA = new RedisDistributedStateMesh(bus, "node-a");
        var nodeB = new RedisDistributedStateMesh(bus, "node-b");

        var received = new TaskCompletionSource<KvCachePrefixSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sub = nodeB.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, evt => received.TrySetResult(evt));
        sub.Dispose();

        await nodeA.PublishAsync(ChannelKvCache,
            new KvCachePrefixSyncEvent("node-a", new[] { "a", "b", "c" }, "m", DateTimeOffset.UtcNow));

        await Assert.ThrowsAnyAsync<TimeoutException>(() => received.Task.WaitAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(0, nodeB.GetStats().ActiveSubscriptionsCount);
    }

    [Fact]
    public async Task MalformedJson_DoesNotBreakSubscriber()
    {
        var bus = new FakeRedisChannelBus();
        var nodeA = new RedisDistributedStateMesh(bus, "node-a");
        var nodeB = new RedisDistributedStateMesh(bus, "node-b");

        var received = new TaskCompletionSource<KvCachePrefixSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        nodeB.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, evt => received.TrySetResult(evt));

        // 直接向总线注入坏 JSON（模拟异类生产者）
        await bus.PublishAsync(ChannelKvCache, "{not-json");

        // 后续合法消息仍能正常送达
        await nodeA.PublishAsync(ChannelKvCache,
            new KvCachePrefixSyncEvent("node-a", new[] { "x", "y", "z" }, "m", DateTimeOffset.UtcNow));

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("x", got.Tokens[0]);
    }

    [Fact]
    public async Task HandlerThrows_DoesNotBreakMesh_SubsequentEventsStillDelivered()
    {
        // 网格 handler 异常不能任其成为 UnobservedTaskException 静默吞掉——
        // 须被捕获（记日志），且不得影响后续事件投递。
        var bus = new FakeRedisChannelBus();
        var nodeA = new RedisDistributedStateMesh(bus, "node-a");
        var nodeB = new RedisDistributedStateMesh(bus, "node-b");

        var deliverAfterFailure = new TaskCompletionSource<KvCachePrefixSyncEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        nodeB.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, evt =>
        {
            if (evt.Tokens[0] == "boom")
                throw new InvalidOperationException("handler explosion");
            deliverAfterFailure.TrySetResult(evt);
        });

        // 第一条触发 handler 异常
        await nodeA.PublishAsync(ChannelKvCache,
            new KvCachePrefixSyncEvent("node-a", new[] { "boom", "b", "c" }, "m", DateTimeOffset.UtcNow));
        // 第二条仍须正常送达（给异步异常留一点调度时间）
        await Task.Delay(100);
        await nodeA.PublishAsync(ChannelKvCache,
            new KvCachePrefixSyncEvent("node-a", new[] { "ok", "y", "z" }, "m", DateTimeOffset.UtcNow));

        var got = await deliverAfterFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ok", got.Tokens[0]);
        Assert.Equal(2, nodeB.GetStats().ReceivedEventsCount);
    }

    [Fact]
    public void GetStats_ReportsLocalCounters()
    {
        var bus = new FakeRedisChannelBus();
        var node = new RedisDistributedStateMesh(bus, "node-x");
        node.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, _ => { });

        var stats = node.GetStats();
        Assert.Equal("node-x", stats.NodeId);
        Assert.Equal(1, stats.ActiveSubscriptionsCount);
    }
}

using System.Collections.Concurrent;
using StackExchange.Redis;

namespace OptiRouter.Mesh;

/// <summary>
/// Redis pub/sub 传输抽象：隔离 ConnectionMultiplexer，使网格逻辑可在无 Redis 环境下单元测试。
/// </summary>
public interface IRedisChannelBus
{
    /// <summary>
    /// 向指定频道发布字符串负载。
    /// </summary>
    Task PublishAsync(string channel, string payload, CancellationToken ct = default);

    /// <summary>
    /// 订阅指定频道；返回 IDisposable 用于取消订阅。
    /// </summary>
    IDisposable Subscribe(string channel, Action<string> onMessage);
}

/// <summary>
/// 基于 StackExchange.Redis pub/sub 的频道总线实现。
/// </summary>
public sealed class RedisChannelBus : IRedisChannelBus
{
    private readonly ISubscriber _subscriber;

    /// <summary>
    /// 通过连接串初始化（连接失败会立即抛出，由调用方决定降级策略）。
    /// </summary>
    public RedisChannelBus(string connectionString)
    {
        var redis = ConnectionMultiplexer.Connect(connectionString);
        _subscriber = redis.GetSubscriber();
    }

    /// <summary>
    /// 使用既有 ConnectionMultiplexer 初始化（复用连接池）。
    /// </summary>
    public RedisChannelBus(ConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _subscriber = redis.GetSubscriber();
    }

    /// <inheritdoc />
    public Task PublishAsync(string channel, string payload, CancellationToken ct = default)
    {
        return _subscriber.PublishAsync(RedisChannel.Literal(channel), payload);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string channel, Action<string> onMessage)
    {
        var queue = _subscriber.Subscribe(RedisChannel.Literal(channel));
        queue.OnMessage(msg => onMessage(msg.Message.ToString() ?? string.Empty));
        return new SubscriptionHandle(() => queue.UnsubscribeAsync());
    }

    private sealed class SubscriptionHandle(Func<Task> unsubscribe) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                unsubscribe().GetAwaiter().GetResult();
            }
        }
    }
}

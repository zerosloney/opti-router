using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 <see cref="IMemoryCache"/> 的进程内响应缓存。单实例，重启丢失。
/// </summary>
public sealed class MemoryResponseCache : IResponseCache
{
    private readonly IMemoryCache _cache;
    private readonly int _maxEntries;
    private readonly bool _useSize;
    private int _count;

    public MemoryResponseCache(IMemoryCache cache, int maxEntries, bool useSize = false)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _maxEntries = maxEntries > 0 ? maxEntries : 1000;
        // 底层 MemoryCache 若设了 SizeLimit（本项目 Program.cs 设 100_000），entry 必须申报 Size 才能写入，
        // 否则 _cache.Set 抛 InvalidOperationException。由调用方告知底层是否启用 SizeLimit。
        _useSize = useSize;
    }

    /// <inheritdoc />
    public bool TryGet(string key, out RawChatResponse? response)
        => _cache.TryGetValue(key, out response);

    /// <inheritdoc />
    public void Set(string key, RawChatResponse response, TimeSpan ttl)
    {
        // intentional-simple: 软容量保护，非精确 LRU；TTL 过期由 IMemoryCache 自动淘汰并经回调减计数。
        if (System.Threading.Interlocked.Increment(ref _count) > _maxEntries)
        {
            System.Threading.Interlocked.Decrement(ref _count);
            return;
        }

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
        if (_useSize)
            options.Size = 1;
        options.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
        {
            EvictionCallback = (_, _, _, _) => System.Threading.Interlocked.Decrement(ref _count)
        });

        try
        {
            _cache.Set(key, response, options);
        }
        catch
        {
            System.Threading.Interlocked.Decrement(ref _count);
        }
    }
}

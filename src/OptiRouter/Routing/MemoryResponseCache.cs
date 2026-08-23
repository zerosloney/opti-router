using System.Text;
using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 <see cref="IMemoryCache"/> 的进程内响应缓存。单实例，重启丢失。
/// 双重软容量保护：条目数上限（计数）+ 字节预算（UTF-8 体量估算），防大响应体撑爆内存。
/// </summary>
public sealed class MemoryResponseCache : IResponseCache
{
    // 单条目的对象开销估算（引用/头/Usage/Metadata），叠加在 Body 字节之上。
    private const int PerEntryOverheadBytes = 512;

    private readonly IMemoryCache _cache;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly bool _useSize;
    private int _count;
    private long _currentBytes;
    private long _hits;
    private long _misses;
    private long _sets;

    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    public MemoryResponseCache(IMemoryCache cache, int maxEntries, long maxBytes = 0, bool useSize = false,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger;
        _maxEntries = maxEntries > 0 ? maxEntries : 1000;
        // 0 = 不限字节（仅条目数保护）。默认值见 RouterOptions.ResponseCacheMaxBytes。
        _maxBytes = maxBytes > 0 ? maxBytes : 0;
        // 底层 MemoryCache 若设了 SizeLimit（本项目 Program.cs 设 100_000），entry 必须申报 Size 才能写入，
        // 否则 _cache.Set 抛 InvalidOperationException。由调用方告知底层是否启用 SizeLimit。
        _useSize = useSize;
    }

    /// <summary>命中/未命中/写入累计计数与当前条目数/字节数（dashboard 状态端点用；进程内统计，重启归零）。</summary>
    public (long Hits, long Misses, long Sets, int CurrentEntries, int MaxEntries, long CurrentBytes, long MaxBytes) GetStats()
        => (_hits, _misses, _sets, _count, _maxEntries, _currentBytes, _maxBytes);

    /// <inheritdoc />
    public bool TryGet(string key, out RawChatResponse? response)
    {
        bool found = _cache.TryGetValue(key, out response);
        if (found) System.Threading.Interlocked.Increment(ref _hits);
        else System.Threading.Interlocked.Increment(ref _misses);
        return found;
    }

    /// <inheritdoc />
    public void Set(string key, RawChatResponse response, TimeSpan ttl)
    {
        // intentional-simple: 软容量保护，非精确 LRU；TTL 过期由 IMemoryCache 自动淘汰并经回调减计数。
        if (System.Threading.Interlocked.Increment(ref _count) > _maxEntries)
        {
            System.Threading.Interlocked.Decrement(ref _count);
            return;
        }

        // 字节预算：Body 的 UTF-8 体量 + 固定开销。超预算不写（与条目数保护同语义）。
        long entryBytes = Encoding.UTF8.GetByteCount(response.Body) + PerEntryOverheadBytes;
        if (_maxBytes > 0 && System.Threading.Interlocked.Add(ref _currentBytes, entryBytes) > _maxBytes)
        {
            System.Threading.Interlocked.Add(ref _currentBytes, -entryBytes);
            System.Threading.Interlocked.Decrement(ref _count);
            return;
        }

        System.Threading.Interlocked.Increment(ref _sets);

        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
        if (_useSize)
            options.Size = 1;
        options.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
        {
            // 字节数经闭包捕获：淘汰时按写入时估算值归还，与计数同步减。
            EvictionCallback = (_, _, _, _) =>
            {
                System.Threading.Interlocked.Decrement(ref _count);
                System.Threading.Interlocked.Add(ref _currentBytes, -entryBytes);
            }
        });

        try
        {
            _cache.Set(key, response, options);
        }
        catch (Exception ex)
        {
            System.Threading.Interlocked.Decrement(ref _count);
            System.Threading.Interlocked.Add(ref _currentBytes, -entryBytes);
            // Set 失败常见于 SizeLimit 配置不一致；留日志便于排查
            _logger?.LogWarning(ex, "Response cache Set failed; entry not cached");
        }
    }
}

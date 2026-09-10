using System.Collections.Concurrent;

namespace OptiRouter.Concurrency;

/// <summary>
/// 租户或会话维度的分区并发闸注册表（DI 单例，每进程一份；静态实现跨测试宿主共享状态，
/// 且返回裸 <see cref="SemaphoreSlim"/> 使"引用—获取—可淘汰判定"分离，存在淘汰/替换竞态）。
/// <para>
/// 通过 <see cref="Acquire"/> 获取分区租约：等待在注册表内完成，租约持有期间分区条目不可被
/// 淘汰（活跃计数由 <see cref="Entry.Active"/> 在条目锁内维护），修复前"拿到信号量引用但尚未
/// Wait 时条目被当作空闲淘汰 → 同分区出现新旧两道闸、实际并发超过单闸上限"的窗口。
/// <see cref="ConcurrencyLease.Dispose"/> 释放信号量并递减活跃计数，必须与 <see cref="Acquire"/>
/// 严格配对（调用方 try/finally 保证）。
/// </para>
/// <para>
/// 空闲清理：并发限制变更时原子替换条目（新配置对后续请求生效，旧租约自然排空后随
/// 条目脱离注册表被回收）；活跃数为零且信号量回满的条目标记空闲起点，超过
/// <see cref="IdleEvictionInterval"/> 仍空闲则移除，下次请求按新条目重建。
/// 扫描按 <see cref="SweepInterval"/> 节流，避免每次获取全量遍历。
/// </para>
/// </summary>
public sealed class ConcurrencyRegistry
{
    /// <summary>空闲条目的淘汰年龄。默认 5 分钟。</summary>
    public TimeSpan IdleEvictionInterval { get; }

    /// <summary>扫描间隔下限，避免每次获取都全量遍历。默认 1 分钟。</summary>
    private TimeSpan SweepInterval { get; }

    private readonly ConcurrentDictionary<string, Entry> _semaphores = new(StringComparer.Ordinal);
    private DateTime _lastSweepUtc = DateTime.UtcNow;
    private readonly object _sweepLock = new();

    public ConcurrencyRegistry()
        : this(idleEvictionInterval: TimeSpan.FromMinutes(5), sweepInterval: TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>测试用构造：可注入更短的淘汰/扫描间隔驱动扫描路径。</summary>
    public ConcurrencyRegistry(TimeSpan idleEvictionInterval, TimeSpan sweepInterval)
    {
        IdleEvictionInterval = idleEvictionInterval;
        SweepInterval = sweepInterval;
    }

    /// <summary>当前注册表内的分区条目数（测试观察淘汰行为用）。</summary>
    internal int TrackedPartitionCount => _semaphores.Count;

    internal sealed class Entry
    {
        public required SemaphoreSlim Semaphore { get; init; }
        public int InitialCount;
        /// <summary>活跃租约数。条目锁内维护；&gt;0 期间不可淘汰。</summary>
        public int Active;
        /// <summary>首次观察到空闲（无活跃租约且信号量回满）的 UTC 时间；null 表示非空闲。</summary>
        public DateTime? IdleSinceUtc;
        /// <summary>已被扫描移除。锁内置位，让并发进入的获取方放弃本条目重新取当前条目。</summary>
        public bool Evicted;
    }

    /// <summary>
    /// 获取指定分区的并发租约；分区并发已达上限返回 null（调用方回 429）。
    /// 等待为非阻塞 TryWait（0 超时），与既有中间件语义一致。
    /// </summary>
    public ConcurrencyLease? Acquire(string key, int maxConcurrency)
    {
        TrySweep();

        while (true)
        {
            var entry = GetOrAddEntry(key, maxConcurrency);

            lock (entry)
            {
                if (entry.Evicted)
                    continue; // 扫描已移除该条目：重取当前条目

                entry.Active++;
                try
                {
                    if (!entry.Semaphore.Wait(0))
                    {
                        entry.Active--;
                        return null;
                    }
                }
                catch
                {
                    entry.Active--;
                    throw;
                }
            }

            return new ConcurrencyLease(entry);
        }
    }

    /// <summary>
    /// 取条目；并发限制变更时原子替换（新配置对后续获取生效，旧租约排空后随旧条目回收）。
    /// 替换竞争失败（他人已替换/移除）不回退旧条目——重试读取当前条目，
    /// 修复前"竞争失败仍返回 prior existing 引用"会让旧限制继续生效。
    /// </summary>
    private Entry GetOrAddEntry(string key, int maxConcurrency)
    {
        while (true)
        {
            if (_semaphores.TryGetValue(key, out var existing))
            {
                if (existing.InitialCount == maxConcurrency)
                    return existing;

                var replacement = new Entry
                {
                    Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency),
                    InitialCount = maxConcurrency
                };
                if (_semaphores.TryUpdate(key, replacement, existing))
                    return replacement;

                replacement.Semaphore.Dispose(); // 竞争失败：放弃自己的新条目，重试读取当前条目
                continue;
            }

            var created = new Entry
            {
                Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency),
                InitialCount = maxConcurrency
            };
            var winner = _semaphores.GetOrAdd(key, created);
            if (ReferenceEquals(winner, created))
                return created;

            created.Semaphore.Dispose(); // 并发首建竞争失败：重试读取胜出条目
        }
    }

    /// <summary>
    /// 节流后的非阻塞扫描：移除空闲超时的条目。淘汰判定与置位在条目锁内原子完成，
    /// 活跃租约数 &gt;0 的条目绝不淘汰（这是租约化修复的核心不变量）。
    /// 使用 TryEnter 试探锁，若已有其他线程在扫描则立刻跳过，保障请求主管道 0 阻塞。
    /// </summary>
    private void TrySweep()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastSweepUtc < SweepInterval) return;

        if (!Monitor.TryEnter(_sweepLock)) return;
        try
        {
            if (now - _lastSweepUtc < SweepInterval) return;
            _lastSweepUtc = now;

            foreach (var kvp in _semaphores)
            {
                var entry = kvp.Value;
                lock (entry)
                {
                    if (entry.Evicted)
                        continue;

                    bool idle = entry.Active == 0 && entry.Semaphore.CurrentCount == entry.InitialCount;
                    if (!idle)
                    {
                        entry.IdleSinceUtc = null;
                        continue;
                    }

                    entry.IdleSinceUtc ??= now;
                    if (now - entry.IdleSinceUtc.Value >= IdleEvictionInterval)
                    {
                        entry.Evicted = true;
                        _semaphores.TryRemove(kvp.Key, out _);
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(_sweepLock);
        }
    }

    /// <summary>
    /// 分区并发租约：<see cref="ConcurrencyRegistry.Acquire"/> 成功的凭证，
    /// <see cref="Dispose"/> 释放信号量并递减活跃计数（幂等，防重复释放）。
    /// </summary>
    public sealed class ConcurrencyLease : IDisposable
    {
        private Entry? _entry;

        internal ConcurrencyLease(Entry entry) => _entry = entry;

        /// <inheritdoc />
        public void Dispose()
        {
            var entry = _entry;
            if (entry is null) return;
            _entry = null;

            lock (entry)
            {
                entry.Semaphore.Release();
                entry.Active--;
                // 归还后回满：由下一次扫描把条目标记为空闲起点（此处不预置时间，保持判定单点）。
            }
        }
    }
}

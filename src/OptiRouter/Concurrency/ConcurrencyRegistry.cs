using System.Collections.Concurrent;

namespace OptiRouter.Concurrency;

/// <summary>
/// 租户或会话维度的并发信号量注册表。
/// 通过对每个 PartitionKey 使用一个 SemaphoreSlim 门控来限制最大并发量，防止单一租户或 IP 打爆系统。
/// <para>
/// 空闲清理：每个 PartitionKey 永久驻留会随匿名 IP/Token 数增长而 OOM。
/// <c>GetSemaphore</c> 惰性触发扫描——释放回满（CurrentCount == InitialCount）的信号量标记空闲起点，
/// 超过 <see cref="IdleEvictionInterval"/> 仍空闲则移除。下次该 key 请求到达时按 <see cref="GetSemaphore"/>
/// 重建（固定窗口计数状态重置，仅丢失该分区当前窗口的限流计数，功能不损）。
/// 扫描按 <see cref="SweepInterval"/> 节流，避免每次调用全量遍历。
/// </para>
/// </summary>
public static class ConcurrencyRegistry
{
    /// <summary>
    /// 空闲信号量的淘汰年龄。默认 5 分钟。
    /// </summary>
    public static TimeSpan IdleEvictionInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 扫描间隔下限，避免每次 GetSemaphore 都全量遍历。默认 1 分钟。
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentDictionary<string, Entry> _semaphores = new(StringComparer.Ordinal);
    private static DateTime _lastSweepUtc = DateTime.UtcNow;
    private static readonly object _sweepLock = new();

    private sealed class Entry
    {
        public required SemaphoreSlim Semaphore { get; init; }
        /// <summary>
        /// 首次观察到信号量空闲（CurrentCount == InitialCount）的 UTC 时间。
        /// null 表示当前有占用或尚未观察到空闲。
        /// </summary>
        public DateTime? IdleSinceUtc;
        public int InitialCount;
    }

    /// <summary>
    /// 获取或创建指定 Partition 维度的信号量。
    /// </summary>
    public static SemaphoreSlim GetSemaphore(string key, int maxConcurrency)
    {
        TrySweep();

        // 惰性观察：若已存在且当前空闲，清掉 IdleSince（视为重新激活）。
        if (_semaphores.TryGetValue(key, out var existing))
        {
            // 若并发限制变更，重建信号量（原子替换），让新配置生效。
            // 旧信号量被当前持有者引用，Release 正常；无引用后 GC 回收。
            if (existing.InitialCount != maxConcurrency)
            {
                var newEntry = new Entry
                {
                    Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency),
                    InitialCount = maxConcurrency
                };
                if (_semaphores.TryUpdate(key, newEntry, existing))
                    return newEntry.Semaphore;
                // 并发竞争：另一线程已替换该 key，回退到现有 entry。
                newEntry.Semaphore.Dispose();
            }

            if (existing.Semaphore.CurrentCount == existing.InitialCount)
                existing.IdleSinceUtc = null;
            return existing.Semaphore;
        }

        var entry = new Entry
        {
            Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency),
            InitialCount = maxConcurrency
        };
        return _semaphores.GetOrAdd(key, entry).Semaphore;
    }

    /// <summary>
    /// 节流后的惰性扫描：移除空闲超时的信号量。
    /// </summary>
    private static void TrySweep()
    {
        DateTime now = DateTime.UtcNow;
        // 快路径：间隔未到跳过。无锁读，偶发多线程同时通过仅多扫一次，无副作用。
        if (now - _lastSweepUtc < SweepInterval) return;

        lock (_sweepLock)
        {
            if (now - _lastSweepUtc < SweepInterval) return;
            _lastSweepUtc = now;

            foreach (var kvp in _semaphores)
            {
                var entry = kvp.Value;
                bool isIdle = entry.Semaphore.CurrentCount == entry.InitialCount;
                if (isIdle)
                {
                    entry.IdleSinceUtc ??= now;
                    if (now - entry.IdleSinceUtc.Value >= IdleEvictionInterval)
                    {
                        // 尝试移除：仅当仍空闲时（并发刚占用则跳过）。
                        // 不 Dispose 信号量：GetSemaphore 可能正返回同一实例给调用方，
                        // Dispose 后调用方 WaitAsync/Release 会抛 ObjectDisposedException。
                        // 移除后旧实例脱离 registry，调用方 Release 完毕无引用即由 GC 回收。
                        if (entry.Semaphore.CurrentCount == entry.InitialCount)
                        {
                            _semaphores.TryRemove(kvp.Key, out _);
                        }
                    }
                }
                else
                {
                    entry.IdleSinceUtc = null;
                }
            }
        }
    }
}

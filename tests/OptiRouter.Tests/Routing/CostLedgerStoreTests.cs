using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 成本账本存储契约测试：对内存与 SQLite 两种实现跑同一套断言，
/// 确保两者行为一致。SQLite 额外测持久化与并发。
/// </summary>
public class CostLedgerStoreTests
{
    /// <summary>
    /// 返回被测 store 工厂列表。每个工厂返回一个独立、初始干净的 store 实例。
    /// </summary>
    public static IEnumerable<object[]> StoreFactories()
    {
        yield return new object[] { (Func<ICostLedgerStore>)(static () => new InMemoryCostLedgerStore()) };
        // SQLite 用临时文件，每次新建独立 DB。
        yield return new object[] { (Func<ICostLedgerStore>)(() => new SqliteCostLedgerStore(TempDbPath())) };
    }

    private static string TempDbPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "optirouter-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test-budget.db");
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void AddDaily_AccumulatesAtomically(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow;

        Assert.Equal(1.5m, store.AddDaily(today, 1.5m));
        Assert.Equal(2.5m, store.AddDaily(today, 1.0m));
        Assert.Equal(2.5m, store.GetDaily(today));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void AddSession_AccumulatesAtomically(Func<ICostLedgerStore> factory)
    {
        using var store = factory();

        Assert.Equal(0.8m, store.AddSession("s1", 0.8m));
        Assert.Equal(1.3m, store.AddSession("s1", 0.5m));
        Assert.Equal(1.3m, store.GetSession("s1"));
        Assert.Equal(0m, store.GetSession("s2"));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void AddTotal_AccumulatesAcrossResets(Func<ICostLedgerStore> factory)
    {
        using var store = factory();

        Assert.Equal(2.0m, store.AddTotal(2.0m));
        store.ResetDaily();
        // total 不受 daily reset 影响
        Assert.Equal(2.0m, store.GetTotal());
        Assert.Equal(3.5m, store.AddTotal(1.5m));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetDaily_NonExistentDate_ReturnsZero(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        Assert.Equal(0m, store.GetDaily(DateTime.UtcNow));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void ResetDaily_ClearsAllDays(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow;
        store.AddDaily(today, 5.0m);
        Assert.Equal(5.0m, store.GetDaily(today));

        store.ResetDaily();
        Assert.Equal(0m, store.GetDaily(today));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void ResetSession_ClearsSpecificSessionOnly(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        store.AddSession("s1", 1.0m);
        store.AddSession("s2", 2.0m);

        store.ResetSession("s1");
        Assert.Equal(0m, store.GetSession("s1"));
        Assert.Equal(2.0m, store.GetSession("s2"));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void ClearAll_WipesEverything(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow;
        store.AddDaily(today, 1.0m);
        store.AddSession("s1", 2.0m);
        store.AddTotal(3.0m);

        store.ClearAll();
        Assert.Equal(0m, store.GetDaily(today));
        Assert.Equal(0m, store.GetSession("s1"));
        Assert.Equal(0m, store.GetTotal());
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void AddDaily_DifferentDates_Isolated(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow.Date;
        var yesterday = today.AddDays(-1);

        store.AddDaily(today, 10m);
        store.AddDaily(yesterday, 20m);

        Assert.Equal(10m, store.GetDaily(today));
        Assert.Equal(20m, store.GetDaily(yesterday));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public async Task ConcurrentAdds_AccumulateCorrectly(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow;
        int threads = 10;
        decimal perThread = 0.1m;

        var tasks = Enumerable.Range(0, threads)
            .Select(_ => Task.Run(() => store.AddDaily(today, perThread)))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(threads * perThread, store.GetDaily(today));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void EvictSessionsBefore_RemovesStale(Func<ICostLedgerStore> factory)
    {
        using var store = factory();

        // 写入 s1，等 10ms，写入 s2，等 10ms，写入 s3
        store.AddSession("s1", 1.0m);
        Thread.Sleep(10);
        var between = DateTime.UtcNow;
        store.AddSession("s2", 2.0m);
        Thread.Sleep(10);
        store.AddSession("s3", 3.0m);

        // 淘汰 s1（s1 在 between 之前写入）
        int removed = store.EvictSessionsBefore(between);
        Assert.Equal(1, removed);
        Assert.Equal(0m, store.GetSession("s1"));
        Assert.Equal(2.0m, store.GetSession("s2"));
        Assert.Equal(3.0m, store.GetSession("s3"));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void EvictSessionsBefore_KeepsRecent(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var now = DateTime.UtcNow;

        store.AddSession("recent", 5.0m);
        // 淘汰早于 now-1h 的，recent 在 now 写入，应保留
        int removed = store.EvictSessionsBefore(now.AddHours(-1));
        Assert.Equal(0, removed);
        Assert.Equal(5.0m, store.GetSession("recent"));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void EvictSessionsBefore_NoSessions_ReturnsZero(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        int removed = store.EvictSessionsBefore(DateTime.UtcNow);
        Assert.Equal(0, removed);
    }

    // ---- SQLite 专属：持久化跨实例 ----

    [Fact]
    public void Sqlite_PersistsAcrossInstances()
    {
        string path = TempDbPath();
        try
        {
            using (var a = new SqliteCostLedgerStore(path))
            {
                var today = DateTime.UtcNow;
                a.AddDaily(today, 1.5m);
                a.AddSession("s1", 2.0m);
                a.AddTotal(3.0m);
            }

            // 新实例打开同一文件，应读到旧数据
            using var b = new SqliteCostLedgerStore(path);
            Assert.Equal(1.5m, b.GetDaily(DateTime.UtcNow));
            Assert.Equal(2.0m, b.GetSession("s1"));
            Assert.Equal(3.0m, b.GetTotal());
        }
        finally
        {
            CleanupDb(path);
        }
    }

    // ---- Daily History ----

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void SnapshotDaily_ArchivesCurrentSpend(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow.Date;
        store.AddDaily(today, 5.0m);
        Assert.Equal(5.0m, store.GetDaily(today));

        store.SnapshotDaily(today);
        var history = store.GetDailyHistory(1);
        Assert.Single(history);
        Assert.Equal(today, history[0].Date);
        Assert.Equal(5.0m, history[0].Amount);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void SnapshotDaily_ZeroSpend_NoEntry(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow.Date;
        // No spend added — snapshot should produce no history entry.
        store.SnapshotDaily(today);
        Assert.Empty(store.GetDailyHistory(1));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetDailyHistory_ReturnsMultipleDays(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow.Date;
        var yesterday = today.AddDays(-1);
        var twoDaysAgo = today.AddDays(-2);

        store.AddDaily(twoDaysAgo, 3.0m);
        store.AddDaily(yesterday, 2.0m);
        store.AddDaily(today, 1.0m);

        // Snapshot each day.
        store.SnapshotDaily(twoDaysAgo);
        store.SnapshotDaily(yesterday);
        store.SnapshotDaily(today);

        var history = store.GetDailyHistory(3);
        Assert.Equal(3, history.Count);
        Assert.Equal(twoDaysAgo, history[0].Date);
        Assert.Equal(3.0m, history[0].Amount);
        Assert.Equal(yesterday, history[1].Date);
        Assert.Equal(today, history[2].Date);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetDailyHistory_RespectsDaysLimit(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow.Date;
        var fiveDaysAgo = today.AddDays(-5);

        store.AddDaily(fiveDaysAgo, 10m);
        store.SnapshotDaily(fiveDaysAgo);

        // Request only 3 days back.
        var history = store.GetDailyHistory(3);
        Assert.Empty(history); // 5 days ago is outside the 3-day window.
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetDailyHistory_ZeroDays_ReturnsEmpty(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        Assert.Empty(store.GetDailyHistory(0));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void ResetDaily_ArchivesBeforeClearing(Func<ICostLedgerStore> factory)
    {
        using var store = factory();
        var today = DateTime.UtcNow.Date;
        store.AddDaily(today, 7.0m);

        store.SnapshotDaily(today);
        store.ResetDaily();

        // Daily should be cleared, but history should retain the snapshot.
        Assert.Equal(0m, store.GetDaily(today));
        var history = store.GetDailyHistory(1);
        Assert.Single(history);
        Assert.Equal(7.0m, history[0].Amount);
    }

    // ---- SQLite 专属：daily_spend_history 持久化跨实例 ----

    [Fact]
    public void Sqlite_DailyHistoryPersistsAcrossInstances()
    {
        string path = TempDbPath();
        try
        {
            var today = DateTime.UtcNow.Date;
            using (var a = new SqliteCostLedgerStore(path))
            {
                a.AddDaily(today, 6.0m);
                a.SnapshotDaily(today);
            }

            using var b = new SqliteCostLedgerStore(path);
            var history = b.GetDailyHistory(1);
            Assert.Single(history);
            Assert.Equal(6.0m, history[0].Amount);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    private static void CleanupDb(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-wal")) File.Delete(path + "-wal");
            if (File.Exists(path + "-shm")) File.Delete(path + "-shm");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* 测试清理容忍失败 */ }
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void ResetDaily_PreservesArchivedHistory(Func<ICostLedgerStore> factory)
    {
        // M6 契约钉子：ResetDaily 归零当日累计，但 SnapshotDaily 已归档的历史必须保留
        //（Postgres/Redis 的按日期分键实现据此只清当日条目——清全部会毁掉趋势数据）。
        using var store = factory();
        var today = DateTime.UtcNow;

        store.AddDaily(today, 5.0m);
        store.SnapshotDaily(today);
        store.ResetDaily();

        Assert.Equal(0m, store.GetDaily(today));
        var history = store.GetDailyHistory(1);
        var entry = Assert.Single(history);
        Assert.Equal(today.Date, entry.Date);
        Assert.Equal(5.0m, entry.Amount);
    }

    [Fact]
    public void PostgresStore_ConnectionFailure_FallsBackWithSingleErrorLog()
    {
        // M5：DB 不可达时降级内存必须留痕；构造 + 多次操作失败按状态迁移只告警一次，不逐请求刷屏。
        var logger = new CountingLogger();
        using var store = new PostgresCostLedgerStore(
            "Host=127.0.0.1;Port=1;Username=u;Password=p;Timeout=2", logger: logger);

        Assert.Equal(1.5m, store.AddDaily(DateTime.UtcNow, 1.5m));
        Assert.Equal(2.5m, store.AddDaily(DateTime.UtcNow, 1.0m));
        Assert.Equal(0m, store.GetTotal());

        Assert.Equal(1, logger.ErrorCount);
        Assert.Contains("degraded", logger.Messages[0]);
    }

    [Fact]
    public void RedisStore_ConstructionFailure_LogsErrorAndFallsBack()
    {
        // M5：Redis 构造失败即永久降级内存（无重连路径），必须记一次错误日志。
        var logger = new CountingLogger();
        using var store = new RedisCostLedgerStore(
            "127.0.0.1:1,connectTimeout=200,connectRetry=1", logger: logger);

        Assert.Equal(1.5m, store.AddDaily(DateTime.UtcNow, 1.5m));

        Assert.Equal(1, logger.ErrorCount);
        Assert.Contains("Redis cost ledger unavailable", logger.Messages[0]);
    }

    /// <summary>计数型 ILogger 桩：记录 Error 次数与消息，验证降级日志行为。</summary>
    private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public int ErrorCount { get; private set; }
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
            {
                ErrorCount++;
            }
            Messages.Add(formatter(state, exception));
        }
    }
}

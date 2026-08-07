using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 请求审计存储契约测试：内存与 SQLite 两种实现共享同一套断言。
/// </summary>
public class RequestAuditStoreTests
{
    public static IEnumerable<object[]> StoreFactories()
    {
        yield return new object[] { (Func<IRequestAuditStore>)(static () => new InMemoryRequestAuditStore()) };
        yield return new object[] { (Func<IRequestAuditStore>)(() => new SqliteRequestAuditStore(TempDbPath())) };
    }

    private static string TempDbPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "optirouter-audit-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test-audit.db");
    }

    private static RequestAuditRecord SampleRecord(string model = "gpt-4o", bool success = true, DateTime? ts = null, bool isStreaming = false)
        => new(
            Timestamp: ts ?? DateTime.UtcNow,
            RequestId: "req-" + Guid.NewGuid().ToString("N")[..8],
            Model: model,
            EstimatedInputTokens: 100,
            PromptTokens: 80,
            CompletionTokens: 40,
            Cost: 0.001m,
            LatencyMs: 250,
            SessionId: "sess-1",
            RoutingReason: "test-reason",
            Success: success,
            ErrorMessage: success ? null : "upstream 500",
            IsStreaming: isStreaming);

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void Append_ThenGetRecent_ReturnsRecords(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var r1 = SampleRecord();
        var r2 = SampleRecord(model: "claude-3");

        store.Append(r1);
        store.Append(r2);

        var recent = store.GetRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal(r2.Model, recent[0].Model); // newest first
        Assert.Equal(r1.Model, recent[1].Model);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetRecent_RespectsLimit(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        for (int i = 0; i < 10; i++)
            store.Append(SampleRecord(model: $"model-{i}"));

        Assert.Equal(5, store.GetRecent(5).Count);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetRecent_EmptyStore_ReturnsEmpty(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        Assert.Empty(store.GetRecent(10));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetByModel_FiltersCorrectly(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        store.Append(SampleRecord(model: "gpt-4o"));
        store.Append(SampleRecord(model: "claude-3"));
        store.Append(SampleRecord(model: "gpt-4o"));

        var gptRecords = store.GetByModel("gpt-4o", 10);
        Assert.Equal(2, gptRecords.Count);
        Assert.All(gptRecords, r => Assert.Equal("gpt-4o", r.Model));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetByModel_EmptyModel_ReturnsEmpty(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        store.Append(SampleRecord());
        Assert.Empty(store.GetByModel("", 10));
        Assert.Empty(store.GetByModel(null!, 10));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetByTimeRange_PaginationWorks(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            store.Append(SampleRecord(ts: now.AddSeconds(-i)));
        }

        var (page1, total) = store.GetByTimeRange(now.AddSeconds(-60), now, 3, 0);
        Assert.Equal(3, page1.Count);
        Assert.Equal(10, total);

        var (page2, _) = store.GetByTimeRange(now.AddSeconds(-60), now, 3, 3);
        Assert.Equal(3, page2.Count);
        Assert.NotEqual(page1[0].RequestId, page2[0].RequestId);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetByTimeRange_NoResults_ReturnsEmpty(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        store.Append(SampleRecord(ts: DateTime.UtcNow.AddDays(-10)));
        var (items, total) = store.GetByTimeRange(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, 10, 0);
        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetByTimeRange_InvalidLimit_ReturnsEmpty(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var (items, total) = store.GetByTimeRange(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, 0, 0);
        Assert.Empty(items);
        Assert.Equal(0, total);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void EvictBefore_RemovesOldRecords(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var now = DateTime.UtcNow;
        store.Append(SampleRecord(ts: now.AddMinutes(-10), model: "old"));
        store.Append(SampleRecord(ts: now, model: "new"));

        int removed = store.EvictBefore(now.AddMinutes(-5));
        Assert.Equal(1, removed);

        var recent = store.GetRecent(10);
        Assert.Single(recent);
        Assert.Equal("new", recent[0].Model);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void EvictBefore_NoRecords_ReturnsZero(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        Assert.Equal(0, store.EvictBefore(DateTime.UtcNow));
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void SuccessAndFailureFlags_Preserved(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        store.Append(SampleRecord(success: true));
        store.Append(SampleRecord(success: false, model: "fail-model"));

        var all = store.GetRecent(10);
        Assert.Equal(2, all.Count);
        Assert.False(all[0].Success); // newest first: second append (failure)
        Assert.True(all[1].Success);  // first append (success)
        Assert.Equal("upstream 500", all[0].ErrorMessage);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void StreamingFlag_Preserved(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        store.Append(SampleRecord(isStreaming: true));
        store.Append(SampleRecord(isStreaming: false));

        var all = store.GetRecent(10);
        Assert.False(all[0].IsStreaming); // newest first: second append (false)
        Assert.True(all[1].IsStreaming);  // first append (true)
    }

    // ---- SQLite 专属：持久化跨实例 ----

    [Fact]
    public void Sqlite_PersistsAcrossInstances()
    {
        string path = TempDbPath();
        try
        {
            using (var a = new SqliteRequestAuditStore(path))
            {
                a.Append(SampleRecord(model: "gpt-4o"));
                a.Append(SampleRecord(model: "claude-3"));
            }

            using var b = new SqliteRequestAuditStore(path);
            var recent = b.GetRecent(10);
            Assert.Equal(2, recent.Count);
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
        catch { /* test cleanup tolerates failures */ }
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void Append_WithNewFields_RoundTrips(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var record = SampleRecord() with
        {
            RoutedTier = ModelTier.Cheap,
            CascadeTriggered = true,
            UpgradedFrom = "cheap-model"
        };

        store.Append(record);

        var recent = store.GetRecent(1);
        Assert.Single(recent);
        Assert.Equal(ModelTier.Cheap, recent[0].RoutedTier);
        Assert.True(recent[0].CascadeTriggered);
        Assert.Equal("cheap-model", recent[0].UpgradedFrom);
    }

    [Fact]
    public void Sqlite_MigratesOldSchema_AddsNewColumns()
    {
        // 构造旧 schema（无 routed_tier/cascade_triggered/upgraded_from），写入旧记录。
        string path = TempDbPath();
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE request_audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        request_id TEXT NOT NULL,
                        model TEXT NOT NULL,
                        estimated_tokens INTEGER NOT NULL,
                        prompt_tokens INTEGER NOT NULL DEFAULT 0,
                        completion_tokens INTEGER NOT NULL DEFAULT 0,
                        cost REAL NOT NULL DEFAULT 0,
                        latency_ms INTEGER NOT NULL DEFAULT 0,
                        session_id TEXT,
                        routing_reason TEXT NOT NULL,
                        success INTEGER NOT NULL,
                        error_message TEXT,
                        is_streaming INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO request_audit (timestamp, request_id, model, estimated_tokens, prompt_tokens,
                        completion_tokens, cost, latency_ms, session_id, routing_reason, success, error_message, is_streaming)
                    VALUES ('2026-01-01T00:00:00.0000000Z', 'r1', 'old-model', 10, 5, 5, 0.01, 100, null, 'old', 1, null, 0);
                    """;
                cmd.ExecuteNonQuery();
            }

            // 用旧 DB 构造 store → 应触发列迁移。
            using var store = new SqliteRequestAuditStore(path);

            // 旧记录可读，新字段取默认值。
            var old = store.GetRecent(10);
            Assert.Single(old);
            Assert.Equal(ModelTier.Medium, old[0].RoutedTier); // 默认
            Assert.False(old[0].CascadeTriggered);
            Assert.Null(old[0].UpgradedFrom);

            // 新记录可写入读回。
            store.Append(SampleRecord() with { RoutedTier = ModelTier.Strong, CascadeTriggered = true, UpgradedFrom = "src" });
            var all = store.GetRecent(10);
            Assert.Equal(2, all.Count);
            Assert.Equal(ModelTier.Strong, all[0].RoutedTier);
            Assert.True(all[0].CascadeTriggered);
            Assert.Equal("src", all[0].UpgradedFrom);
        }
        finally
        {
            CleanupDb(path);
        }
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetLatencyStatsSince_AggregatesSuccessfulRequests(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        // model-a：3 次成功（100, 200, 300 ms → 均 200），1 次失败（应排除）。
        store.Append(SampleRecord("model-a", success: true) with { LatencyMs = 100 });
        store.Append(SampleRecord("model-a", success: true) with { LatencyMs = 200 });
        store.Append(SampleRecord("model-a", success: true) with { LatencyMs = 300 });
        store.Append(SampleRecord("model-a", success: false) with { LatencyMs = 9999 });
        // model-b：1 次成功。
        store.Append(SampleRecord("model-b", success: true) with { LatencyMs = 50 });

        var stats = store.GetLatencyStatsSince(DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(2, stats.Count);
        Assert.Equal(200.0, stats["model-a"].AverageLatencyMs, precision: 1);
        Assert.Equal(3, stats["model-a"].SampleCount);
        Assert.Equal(50.0, stats["model-b"].AverageLatencyMs);
        Assert.Equal(1, stats["model-b"].SampleCount);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void GetLatencyStatsSince_OldRecordsExcluded(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var old = DateTime.UtcNow.AddHours(-2);
        var recent = DateTime.UtcNow;

        store.Append(SampleRecord("model-a", success: true, ts: old) with { LatencyMs = 100 });
        store.Append(SampleRecord("model-a", success: true, ts: recent) with { LatencyMs = 200 });

        // cutoff = 1 小时前 → 仅 recent 记录计入。
        var stats = store.GetLatencyStatsSince(DateTime.UtcNow.AddHours(-1));

        Assert.Single(stats);
        Assert.Equal(200.0, stats["model-a"].AverageLatencyMs);
        Assert.Equal(1, stats["model-a"].SampleCount);
    }

    [Theory]
    [MemberData(nameof(StoreFactories))]
    public void Append_WithParallelFields_RoundTrips(Func<IRequestAuditStore> factory)
    {
        using var store = factory();
        var adopted = SampleRecord("fast-model") with { IsAdopted = true, ParallelGroupId = "group-1" };
        var cancelled = SampleRecord("slow-model") with
        {
            IsAdopted = false,
            ParallelGroupId = "group-1",
            Success = false,
            ErrorMessage = "cancelled"
        };

        store.Append(adopted);
        store.Append(cancelled);

        var recent = store.GetRecent(10);
        Assert.Equal(2, recent.Count);
        var fast = recent.First(r => r.Model == "fast-model");
        var slow = recent.First(r => r.Model == "slow-model");
        Assert.True(fast.IsAdopted);
        Assert.Equal("group-1", fast.ParallelGroupId);
        Assert.False(slow.IsAdopted);
        Assert.Equal("group-1", slow.ParallelGroupId);
    }

    [Fact]
    public void SqliteStore_MigratesOldDb_AddsParallelColumns()
    {
        string path = TempDbPath();
        try
        {
            // 构造旧 schema（无 is_adopted/parallel_group_id）。
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE request_audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        timestamp TEXT NOT NULL,
                        request_id TEXT NOT NULL,
                        model TEXT NOT NULL,
                        estimated_tokens INTEGER NOT NULL,
                        prompt_tokens INTEGER NOT NULL DEFAULT 0,
                        completion_tokens INTEGER NOT NULL DEFAULT 0,
                        cost REAL NOT NULL DEFAULT 0,
                        latency_ms INTEGER NOT NULL DEFAULT 0,
                        session_id TEXT,
                        routing_reason TEXT NOT NULL,
                        success INTEGER NOT NULL,
                        error_message TEXT,
                        is_streaming INTEGER NOT NULL DEFAULT 0,
                        routed_tier TEXT,
                        cascade_triggered INTEGER NOT NULL DEFAULT 0,
                        upgraded_from TEXT
                    );
                    INSERT INTO request_audit
                        (timestamp, request_id, model, estimated_tokens, prompt_tokens,
                         completion_tokens, cost, latency_ms, session_id, routing_reason,
                         success, error_message, is_streaming, routed_tier, cascade_triggered, upgraded_from)
                    VALUES ('2026-01-01T00:00:00.0000000Z', 'r1', 'old', 10, 5, 5, 0.01, 100, null, 'old', 1, null, 0, 'Medium', 0, null);
                    """;
                cmd.ExecuteNonQuery();
            }

            using var store = new SqliteRequestAuditStore(path);

            // 旧记录读回，新字段取默认值（IsAdopted=true，ParallelGroupId=null）。
            var old = store.GetRecent(10);
            Assert.Single(old);
            Assert.True(old[0].IsAdopted);
            Assert.Null(old[0].ParallelGroupId);

            // 新记录带并行字段可写入读回。
            store.Append(SampleRecord("fast") with { IsAdopted = true, ParallelGroupId = "g1" });
            var all = store.GetRecent(10);
            Assert.Equal(2, all.Count);
            var fast = all.First(r => r.Model == "fast");
            Assert.True(fast.IsAdopted);
            Assert.Equal("g1", fast.ParallelGroupId);
        }
        finally
        {
            CleanupDb(path);
        }
    }
}

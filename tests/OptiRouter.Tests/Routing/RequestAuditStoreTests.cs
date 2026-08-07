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
}

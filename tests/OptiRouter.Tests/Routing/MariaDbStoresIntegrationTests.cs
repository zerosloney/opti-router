using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit.Abstractions;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// MariaDB store 集成测试：需真实 MariaDB，经环境变量 OPTIROUTER_MARIADB_TEST 提供连接串时才执行
/// （建议指向专用临时库，如 Database=optirouter_it）；未设置时静默跳过，CI/无库环境不失败。
/// 本地执行示例：
/// <c>OPTIROUTER_MARIADB_TEST="Server=127.0.0.1;Database=optirouter_it;User ID=root;Password=..." dotnet test</c>
/// </summary>
public class MariaDbStoresIntegrationTests(ITestOutputHelper output)
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("OPTIROUTER_MARIADB_TEST");

    private bool ShouldSkip => string.IsNullOrWhiteSpace(ConnectionString);

    [Fact]
    public void CostLedger_Roundtrip_WritesAndReadsBack()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        using var store = new MariaDbCostLedgerStore(ConnectionString!);
        string sid = "it-session-" + Guid.NewGuid().ToString("N");
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        decimal daily = store.AddDaily(date, 1.5m);
        decimal session = store.AddSession(sid, 0.25m);
        decimal total = store.AddTotal(2m);

        Assert.Equal(1.5m, daily);
        Assert.Equal(0.25m, session);
        Assert.Equal(1.5m, store.GetDaily(date));
        Assert.Equal(0.25m, store.GetSession(sid));
        Assert.Equal(2m, store.GetTotal());

        // RecordAtomic 在同一事务累加三个账户。
        store.RecordAtomic(date, 0.5m, 0.5m, sid, 0.5m);
        Assert.Equal(2.0m, store.GetDaily(date));
        Assert.Equal(0.75m, store.GetSession(sid));
        Assert.Equal(2.5m, store.GetTotal());

        // 断路器状态回读。
        string model = "it-model-" + Guid.NewGuid().ToString("N");
        store.SaveCircuitState(model, CircuitState.Open, 3, date.AddHours(1));
        var circuits = store.LoadCircuitStates();
        Assert.True(circuits.ContainsKey(model));
        Assert.Equal(CircuitState.Open, circuits[model].State);
        Assert.Equal(3, circuits[model].FailureCount);

        // 快照归档 + 历史回读。
        store.SnapshotDaily(date);
        var history = store.GetDailyHistory(365);
        Assert.Contains(history, h => h.Date == date.Date && h.Amount == 2.0m);

        // 清理：会话/当日/总额归零（历史与断路器行留在专用测试库）。
        store.ResetSession(sid);
        store.ResetDaily();
        store.ResetTotal();
        Assert.Equal(0m, store.GetSession(sid));
        Assert.Equal(0m, store.GetDaily(date));
        Assert.Equal(0m, store.GetTotal());
    }

    [Fact]
    public void CostLedger_UnreachableServer_FallsBackToInMemory()
    {
        // 不可达连接串不依赖环境变量，可无条件执行：构造降级内存，写入不抛。
        using var store = new MariaDbCostLedgerStore(
            "Server=127.0.0.1;Port=47890;Database=none;User ID=x;Password=x;Connection Timeout=1;Default Command Timeout=1");
        var date = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        decimal daily = store.AddDaily(date, 3m);
        Assert.Equal(3m, daily);
        Assert.Equal(3m, store.GetDaily(date));
        Assert.Equal(0m, store.GetTotal());
    }

    [Fact]
    public async Task RequestAudit_Append_FlushesAndReadsBack()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        using var store = new MariaDbRequestAuditStore(ConnectionString!);
        string model = "it-audit-" + Guid.NewGuid().ToString("N");
        string rid = "it-req-" + Guid.NewGuid().ToString("N");

        // 基线：测试库可能残留其他用例/历史运行的行，窗口断言一律用增量口径。
        var windowFrom = DateTime.UtcNow.AddMinutes(-5);
        var windowTo = DateTime.UtcNow.AddMinutes(5);
        int failuresBefore = store.GetFailureStats(windowFrom, windowTo).Failures;

        var record = new RequestAuditRecord(
            Timestamp: DateTime.UtcNow,
            RequestId: rid,
            Model: model,
            EstimatedInputTokens: 100,
            PromptTokens: 80,
            CompletionTokens: 20,
            Cost: 0.123m,
            LatencyMs: 456,
            SessionId: "it-audit-session",
            RoutingReason: "integration-test",
            Success: true,
            ErrorMessage: null,
            IsStreaming: true,
            RoutedTier: ModelTier.Strong,
            Reward: 0.5,
            RequestContent: "{\"probe\":true}");
        store.Append(record);

        // 后台批量写：轮询 GetRecent 直到可见（上限 10s）。
        IReadOnlyList<RequestAuditRecord> recent = Array.Empty<RequestAuditRecord>();
        for (int i = 0; i < 100; i++)
        {
            recent = store.GetRecent(50);
            if (recent.Any(r => r.RequestId == rid)) break;
            await Task.Delay(100);
        }

        var stored = recent.Single(r => r.RequestId == rid);
        Assert.Equal(model, stored.Model);
        Assert.Equal(0.123m, stored.Cost);
        Assert.Equal(456, stored.LatencyMs);
        Assert.True(stored.IsStreaming);
        Assert.Equal(ModelTier.Strong, stored.RoutedTier);
        Assert.Equal(0.5, stored.Reward);
        Assert.Equal("{\"probe\":true}", stored.RequestContent);

        // 按模型过滤与时间窗聚合。
        var byModel = store.GetByModel(model, 10);
        Assert.Single(byModel);
        var stats = store.GetAggregateStats(windowFrom, windowTo);
        Assert.True(stats.TotalRequests >= 1);
        // 本用例只写成功记录：失败数应与基线一致。
        var failureStats = store.GetFailureStats(windowFrom, windowTo);
        Assert.Equal(failuresBefore, failureStats.Failures);
    }

    [Fact]
    public void LearningState_Roundtrip_PersistsThompsonAndBandit()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        using var store = new MariaDbLearningStateStore(ConnectionString!);
        string model = "it-learn-" + Guid.NewGuid().ToString("N");

        ((IThompsonStateStore)store).Save(model, alpha: 3.5, beta: 4.5);
        var thompson = ((IThompsonStateStore)store).LoadAll();
        Assert.Equal((3.5, 4.5), thompson[model]);

        var a = new double[,] { { 1.5, 0.5 }, { 0.5, 2.5 } };
        var b = new double[] { 0.25, 0.75 };
        ((IBanditStateStore)store).Save(model, dim: 2, a: a, b: b, n: 7);
        var bandit = ((IBanditStateStore)store).LoadAll();
        Assert.True(bandit.ContainsKey(model));
        Assert.Equal(2, bandit[model].Dim);
        Assert.Equal(7, bandit[model].N);
        Assert.Equal(1.5, bandit[model].A[0, 0]);
        Assert.Equal(0.5, bandit[model].A[0, 1]);
        Assert.Equal(2.5, bandit[model].A[1, 1]);
        Assert.Equal(0.25, bandit[model].B[0]);
        Assert.Equal(0.75, bandit[model].B[1]);
    }

    [Fact]
    public void ClientKeyService_MariaDbBackend_Roundtrip()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        // flushInterval=0 禁用后台定时器，全部同步落库。
        using var service = new ClientKeyService(
            filePath: "n/a.json",
            logger: NullLogger<ClientKeyService>.Instance,
            flushInterval: TimeSpan.Zero,
            mariaDbConnectionString: ConnectionString!);

        var (plaintext, info) = service.CreateKey("it-tenant-" + Guid.NewGuid().ToString("N"), dailyBudgetUsd: 42m, maxQps: 7);
        Assert.StartsWith("opti-key-", plaintext);
        Assert.Equal(42m, info.DailyBudgetUsd);
        Assert.Equal(7, info.MaxQps);

        // 用明文授权（哈希比对 + QPS 窗口）。
        var authorized = service.AuthorizeRequest(plaintext);
        Assert.True(authorized.IsAuthorized);
        Assert.Equal(info.KeyId, authorized.KeyId);
        Assert.False(service.AuthorizeRequest("wrong-key").IsAuthorized);

        // 花费记账 + 更新 + 回读（DB 往返后字段保真）。
        service.RecordSpend(info.KeyId, 1.25m);
        service.UpdateKey(info.KeyId, enabled: null, dailyBudgetUsd: 50m, maxQps: null);
        service.Flush(); // 增量为去抖提交，重载前先 flush

        using var reloaded = new ClientKeyService(
            filePath: "n/a.json",
            logger: NullLogger<ClientKeyService>.Instance,
            flushInterval: TimeSpan.Zero,
            mariaDbConnectionString: ConnectionString!);
        var stored = reloaded.GetAllKeys().Single(k => k.KeyId == info.KeyId);
        Assert.Equal(1.25m, stored.DailySpendUsd);
        Assert.Equal(1, stored.DailyRequestCount);
        Assert.Equal(50m, stored.DailyBudgetUsd);
        Assert.True(stored.Enabled);

        // 删除后不可再授权（用重新加载的实例验证，删除方缓存已剔除该 Key）。
        Assert.True(reloaded.DeleteKey(info.KeyId));
        Assert.False(reloaded.AuthorizeRequest(plaintext).IsAuthorized);
    }

    [Fact]
    public void ClientKeyService_MultiInstance_DeltasAccumulateWithoutClobbering()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        // 实例 A：建 key + 记账 + 提交增量。
        using var instanceA = new ClientKeyService(
            filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
            flushInterval: TimeSpan.Zero, mariaDbConnectionString: ConnectionString!);
        var (plaintext, info) = instanceA.CreateKey("it-multi-" + Guid.NewGuid().ToString("N"), 100m, 50);
        Assert.True(instanceA.AuthorizeRequest(plaintext).IsAuthorized);
        instanceA.RecordSpend(info.KeyId, 1.0m);
        instanceA.Flush();

        // 实例 B：启动加载到 A 的全局累计，再叠加自己的增量（不覆盖 A 的）。
        using var instanceB = new ClientKeyService(
            filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
            flushInterval: TimeSpan.Zero, mariaDbConnectionString: ConnectionString!);
        var bView = instanceB.GetAllKeys().Single(k => k.KeyId == info.KeyId);
        Assert.Equal(1.0m, bView.DailySpendUsd);
        Assert.True(instanceB.AuthorizeRequest(plaintext).IsAuthorized);
        instanceB.RecordSpend(info.KeyId, 0.5m);
        instanceB.Flush();

        // 全新实例读全局：两实例增量在库内累加，无相互覆盖。
        using var instanceC = new ClientKeyService(
            filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
            flushInterval: TimeSpan.Zero, mariaDbConnectionString: ConnectionString!);
        var merged = instanceC.GetAllKeys().Single(k => k.KeyId == info.KeyId);
        Assert.Equal(1.5m, merged.DailySpendUsd);
        Assert.Equal(2, merged.DailyRequestCount);
        instanceC.DeleteKey(info.KeyId);
    }

    [Fact]
    public void ClientKeyService_GlobalLimits_EnforcedAcrossInstances()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        ClientKeyService NewService() => new(
            filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
            flushInterval: TimeSpan.Zero, mariaDbConnectionString: ConnectionString!);

        using var a = NewService();
        var (plaintext, info) = a.CreateKey("it-global-" + Guid.NewGuid().ToString("N"), dailyBudgetUsd: 1m, maxQps: 2);

        // 全局 QPS：maxQps=2，跨实例共享同一秒窗，窗口内第 3 次被拒。
        Assert.True(a.AuthorizeRequest(plaintext).IsAuthorized);
        using var b = NewService();
        Assert.True(b.AuthorizeRequest(plaintext).IsAuthorized);
        bool sawRateLimit = false;
        for (int i = 0; i < 6 && !sawRateLimit; i++)
            sawRateLimit = b.AuthorizeRequest(plaintext).Status == ClientKeyAuthorizationStatus.RateLimited;
        Assert.True(sawRateLimit);

        // 全局预算：花费提交到预算上限后，新实例也被拒（RetryAfter 到次日 UTC 零点）。
        a.RecordSpend(info.KeyId, 1.0m);
        a.Flush();
        using var c = NewService();
        var over = c.AuthorizeRequest(plaintext);
        Assert.Equal(ClientKeyAuthorizationStatus.BudgetExhausted, over.Status);
        Assert.True(over.RetryAfterSeconds > 0);

        c.DeleteKey(info.KeyId);
    }

    [Fact]
    public async Task AuditAnalysis_OverMariaDbStore_ProducesReport()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        using var store = new MariaDbRequestAuditStore(ConnectionString!);
        string model = "it-analyze-" + Guid.NewGuid().ToString("N")[..8];
        var baseTime = DateTime.UtcNow.AddMinutes(-1);

        store.Append(new RequestAuditRecord(
            Timestamp: baseTime, RequestId: "r1", Model: model,
            EstimatedInputTokens: 10, PromptTokens: 100, CompletionTokens: 50,
            Cost: 0.1m, LatencyMs: 120, SessionId: null, RoutingReason: "initial",
            Success: true, ErrorMessage: null, IsStreaming: false,
            RoutedTier: ModelTier.Strong));
        store.Append(new RequestAuditRecord(
            Timestamp: baseTime.AddSeconds(5), RequestId: "r2", Model: model,
            EstimatedInputTokens: 10, PromptTokens: 100, CompletionTokens: 50,
            Cost: 0m, LatencyMs: 0, SessionId: null, RoutingReason: "failover",
            Success: false, ErrorMessage: "upstream 500", IsStreaming: false,
            RoutedTier: ModelTier.Strong, CascadeTriggered: true, UpgradedFrom: "other"));

        // 等后台批量写落库。
        for (int i = 0; i < 50; i++)
        {
            if (store.GetByModel(model, 10).Count == 2) break;
            await Task.Delay(100);
        }

        var analyzer = new AuditAnalysisService(store);
        var report = analyzer.Analyze(baseTime.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        // 至少包含本用例的 2 条（其他历史行可能共存），分模型维度可精确断言。
        var row = report.ByModel.Single(m => m.Model == model);
        Assert.Equal(2, row.Requests);
        Assert.Equal(1, row.Failures);
        Assert.Equal(50.0, row.SuccessRatePct);
        Assert.Equal(0.1, row.CostUsd);
        Assert.Equal(120.0, row.AvgLatencyMs);
        Assert.Contains(report.Cascade.UpgradedFrom, kv => kv.Key == "other");
    }

    [Fact]
    public void AppConfigStore_Facade_RoutesToMariaDbBackend()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        // 传入连接串 → MariaDb 后端；dbPath 参数在该分支不使用。
        using var store = new AppConfigDbStore("n/a.db", ConnectionString!);

        string scope = "it-scope-" + Guid.NewGuid().ToString("N");
        store.SaveDocument(scope, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", store.LoadDocument(scope));
        Assert.True(store.HasData());

        // 配置变更历史。
        store.AppendConfigChange("integration-test", "[{\"k\":\"v\"}]");
        var changes = store.LoadConfigChanges(10);
        Assert.Contains(changes, c => c.Actor == "integration-test");

        // 模型行 upsert / 原始 ApiKey / 删除（用唯一名避免碰到真实模型行）。
        string modelName = "it-model-" + Guid.NewGuid().ToString("N");
        var model = new ModelEndpointOptions
        {
            Name = modelName,
            BaseUrl = "https://example.com",
            ApiKey = "sk-it",
            Tier = ModelTier.Medium,
            MaxContextTokens = 8192
        };
        Assert.Equal(1, store.UpsertModel(model));
        Assert.Contains(store.LoadModelsRaw(), m => m.Name == modelName);
        Assert.Equal("sk-it", store.GetRawApiKey(modelName));
        Assert.True(store.DeleteModel(modelName));
        Assert.DoesNotContain(store.LoadModelsRaw(), m => m.Name == modelName);

        // 评测批次：保存 + 倒序读取。
        string batchId = "it-batch-" + Guid.NewGuid().ToString("N");
        store.SaveEvalBatch(batchId, DateTime.UtcNow.ToString("o"), "{\"report\":true}");
        var batches = store.LoadEvalBatches();
        Assert.Contains(batches, b => b.BatchId == batchId);
    }
}

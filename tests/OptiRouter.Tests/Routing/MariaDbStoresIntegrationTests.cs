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

        var analyzer = new AuditAnalysisService(store, new FixedOptionsMonitor(new RouterOptions()));
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

    /// <summary>
    /// P1-4 回归：MariaDB 路由/预算文档保存必须是跨实例原子 CAS。修复前事务内普通 SELECT
    /// 检查版本后直接 UPSERT，两个实例持相同 expectedVersion 并发保存会双双通过、后写覆盖先写。
    /// 修复后：文档行按旧内容条件 UPDATE（InnoDB 当前读 + 行锁），首次无文档场景并发首写
    /// 由主键冲突判定——同版本并发保存恰好一方成功。需真实 MariaDB（OPTIROUTER_MARIADB_TEST）。
    /// </summary>
    [Fact]
    public async Task AppConfigStore_TrySaveRoutingBudget_ConcurrentSameVersion_ExactlyOneWins()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        // 构造即建表（CREATE TABLE IF NOT EXISTS），随后清出确定的空文档状态。
        using var storeA = new AppConfigDbStore("n/a.db", ConnectionString!);
        ClearRoutingBudgetDocuments();
        using var storeB = new AppConfigDbStore("n/a.db", ConnectionString!);

        // 首次无文档场景：两写手拿"空文档版本"并发首写，恰好一方成功（INSERT 主键冲突判定）。
        var (_, _, emptyVersion) = storeA.LoadRoutingBudgetSnapshot();
        Assert.Equal(1, (await RaceSaveRoutingBudget(storeA, storeB, emptyVersion, "first")).Count(w => w));

        // 常规冲突：每轮两写手拿同一 expectedVersion 并发覆盖，恰好一方成功。
        // 修复前此处大概率出现双成功（后写覆盖先写）；多轮循环放大竞态窗口。
        for (int round = 0; round < 10; round++)
        {
            var (_, _, version) = storeA.LoadRoutingBudgetSnapshot();
            Assert.Equal(1, (await RaceSaveRoutingBudget(storeA, storeB, version, $"round-{round}")).Count(w => w));
        }

        // 串行过期版本：旧 expectedVersion 失败且返回当前库内版本。
        var (_, _, currentVersion) = storeA.LoadRoutingBudgetSnapshot();
        Assert.False(storeA.TrySaveRoutingBudgetDocuments(
            "stale-version", "{\"x\":1}", "{\"x\":1}", out string conflictVersion));
        Assert.Equal(currentVersion, conflictVersion);
        // 失败方不得改动文档：并发轮的胜者内容保持原样。
        var (routing, _, _) = storeA.LoadRoutingBudgetSnapshot();
        Assert.Contains("winner", routing);
    }

    /// <summary>两个独立 store 实例（模拟两个进程）屏障后用同一版本并发保存，返回双方成败。</summary>
    private static async Task<bool[]> RaceSaveRoutingBudget(AppConfigDbStore a, AppConfigDbStore b, string expectedVersion, string tag)
    {
        string Routing(string writer) => $"{{\"writer\":\"{writer}\",\"tag\":\"{tag}\"}}";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t1 = Task.Run(() => { start.Task.Wait(); return a.TrySaveRoutingBudgetDocuments(expectedVersion, Routing("winner-A"), Routing("winner-A"), out _); });
        var t2 = Task.Run(() => { start.Task.Wait(); return b.TrySaveRoutingBudgetDocuments(expectedVersion, Routing("winner-B"), Routing("winner-B"), out _); });
        start.SetResult();
        return await Task.WhenAll(t1, t2);
    }

    /// <summary>清空路由/预算文档行，保证"首次无文档"场景可确定性重现（专用测试库约定）。</summary>
    private void ClearRoutingBudgetDocuments()
    {
        using var conn = new MySqlConnector.MySqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM optirouter_app_config
            WHERE `key` = 'document' AND scope IN ('{AppConfigDbStore.RoutingScope}', '{AppConfigDbStore.BudgetScope}');
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// P1-5 回归：DB 故障跨越缓存 TTL 后，缓存刷新异常不得先于准入降级抛出。
    /// 修复前 AuthorizeRequest 在 TTL 过期时先走 Flush/Load，异常直接传播；
    /// 原有的进程内降级准入（AuthorizeViaDbNoLock 的 catch）永远执行不到。
    /// 注入方式：测试中途 DROP 掉 scratch 库（真实 MySQL 异常），时钟推过 30s TTL。
    /// </summary>
    [Fact]
    public void ClientKeyService_RefreshFailureAfterTtl_DegradesToSnapshotAuthorization()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        // 独立 scratch 库：可整体 DROP 制造真实 DB 故障，不污染共享测试库。
        string scratchDb = "optirouter_p1refresh_" + Guid.NewGuid().ToString("N")[..8];
        string cs = System.Text.RegularExpressions.Regex.Replace(
            ConnectionString!, @"Database=[^;]*", $"Database={scratchDb}");
        ExecuteOnServer(ConnectionString!, $"CREATE DATABASE IF NOT EXISTS `{scratchDb}`");

        var clock = new MutableClock(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        try
        {
            using var service = new ClientKeyService(
                filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
                timeProvider: clock, flushInterval: TimeSpan.Zero, mariaDbConnectionString: cs);
            var (plaintext, info) = service.CreateKey("p1-refresh-tenant", dailyBudgetUsd: 0m, maxQps: 2);

            // DB 正常：全局口径放行。
            Assert.True(service.AuthorizeRequest(plaintext).IsAuthorized);

            // 注入 DB 故障（表所在库整体消失），时钟推过 30s 缓存刷新周期。
            ExecuteOnServer(ConnectionString!, $"DROP DATABASE IF EXISTS `{scratchDb}`");
            clock.Advance(TimeSpan.FromSeconds(31));

            // 修复前：刷新异常直接抛出（Table doesn't exist）。
            // 修复后：降级为快照 + 进程内 QPS 准入，正常放行。
            Assert.True(service.AuthorizeRequest(plaintext).IsAuthorized);
            Assert.True(service.AuthorizeRequest(plaintext).IsAuthorized);
            // 降级口径的进程内 QPS 仍在工作：maxQps=2，窗口内第 3 次拒绝。
            Assert.Equal(
                ClientKeyAuthorizationStatus.RateLimited,
                service.AuthorizeRequest(plaintext).Status);

            // 管理端列举同样走缓存刷新路径：故障期间应返回快照而非抛异常。
            Assert.Single(service.GetAllKeys(), k => k.KeyId == info.KeyId);
        }
        finally
        {
            ExecuteOnServer(ConnectionString!, $"DROP DATABASE IF EXISTS `{scratchDb}`");
        }
    }

    /// <summary>连接服务器（不带 Database）执行 DDL，用于创建/销毁 scratch 库。</summary>
    private static void ExecuteOnServer(string connectionString, string ddl)
    {
        string serverCs = System.Text.RegularExpressions.Regex.Replace(connectionString, @"Database=[^;]*;?", "");
        using var conn = new MySqlConnector.MySqlConnection(serverCs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>
    /// P2-3 回归：日消费增量必须携带业务发生日期。修复前 flush 时重新取当天，
    /// 午夜前消费、午夜后 flush 会把增量记入新一天预算（DB 故障重试跨日进一步放大）。
    /// 库端合并规则：同日累加；行已滚到新一天的迟到旧日增量折入当前日（保守多算防超订）；
    /// 行未滚动的增量触发滚动。
    /// </summary>
    [Fact]
    public void ClientKeyService_SpendDeltaCarriesOccurrenceDate_AcrossMidnightFlush()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        string scratchDb = "optirouter_p2spend_" + Guid.NewGuid().ToString("N")[..8];
        string cs = System.Text.RegularExpressions.Regex.Replace(
            ConnectionString!, @"Database=[^;]*", $"Database={scratchDb}");
        ExecuteOnServer(ConnectionString!, $"CREATE DATABASE IF NOT EXISTS `{scratchDb}`");

        // 时钟钉在午夜前 10 秒：记账发生日在 03-01，flush 已跨到 03-02。
        var clock = new MutableClock(new DateTimeOffset(2026, 3, 1, 23, 59, 50, TimeSpan.Zero));
        try
        {
            using var service = new ClientKeyService(
                filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
                timeProvider: clock, flushInterval: TimeSpan.Zero, mariaDbConnectionString: cs);
            var (_, info) = service.CreateKey("p2-spend-tenant", dailyBudgetUsd: 0m, maxQps: 50);

            service.RecordSpend(info.KeyId, 0.5m); // 发生于 03-01
            clock.Advance(TimeSpan.FromSeconds(20)); // 跨过午夜
            service.Flush();

            // 修复前：flush 取 03-02，行被重置为 03-02/0.5，03-01 的预算口径丢失。
            Assert.Equal(("2026-03-01", 0.5m), ReadSpendRow(cs, info.KeyId));

            // 新一天记账：行滚动到 03-02 并从增量起算。
            service.RecordSpend(info.KeyId, 0.25m);
            service.Flush();
            Assert.Equal(("2026-03-02", 0.25m), ReadSpendRow(cs, info.KeyId));
        }
        finally
        {
            ExecuteOnServer(ConnectionString!, $"DROP DATABASE IF EXISTS `{scratchDb}`");
        }
    }

    /// <summary>库端合并规则直测：迟到旧日增量折入当前行日期累加，不重置、不丢新日花费。</summary>
    [Fact]
    public void ApplySpendDelta_LateOldDateDelta_FoldsIntoCurrentRowDate()
    {
        if (ShouldSkip) { output.WriteLine("OPTIROUTER_MARIADB_TEST 未设置，跳过。"); return; }

        string scratchDb = "optirouter_p2spend_" + Guid.NewGuid().ToString("N")[..8];
        string cs = System.Text.RegularExpressions.Regex.Replace(
            ConnectionString!, @"Database=[^;]*", $"Database={scratchDb}");
        ExecuteOnServer(ConnectionString!, $"CREATE DATABASE IF NOT EXISTS `{scratchDb}`");

        try
        {
            var clock = new MutableClock(new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero));
            using var service = new ClientKeyService(
                filePath: "n/a.json", logger: NullLogger<ClientKeyService>.Instance,
                timeProvider: clock, flushInterval: TimeSpan.Zero, mariaDbConnectionString: cs);
            var (_, info) = service.CreateKey("p2-late-tenant", dailyBudgetUsd: 0m, maxQps: 50);
            service.RecordSpend(info.KeyId, 0.25m); // 03-02
            service.Flush();
            Assert.Equal(("2026-03-02", 0.25m), ReadSpendRow(cs, info.KeyId));

            var store = new OptiRouter.Configuration.MariaDbClientKeyStore(cs);
            // 迟到的 03-01 增量：折入当前行日期（03-02/0.35），不得重置回 03-01。
            store.ApplySpendDelta(info.KeyId, new DateTime(2026, 3, 1), 0.1m);
            Assert.Equal(("2026-03-02", 0.35m), ReadSpendRow(cs, info.KeyId));

            // 同日增量：直接累加。
            store.ApplySpendDelta(info.KeyId, new DateTime(2026, 3, 2), 0.05m);
            Assert.Equal(("2026-03-02", 0.4m), ReadSpendRow(cs, info.KeyId));
        }
        finally
        {
            ExecuteOnServer(ConnectionString!, $"DROP DATABASE IF EXISTS `{scratchDb}`");
        }
    }

    /// <summary>直读 keys 表的单日花费行（date, spend），绕过服务端内存滚动视图。</summary>
    private static (string Date, decimal Spend) ReadSpendRow(string cs, string keyId)
    {
        using var conn = new MySqlConnector.MySqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT daily_spend_date_utc, daily_spend_usd FROM optirouter_client_keys WHERE key_id = @kid;";
        cmd.Parameters.AddWithValue("@kid", keyId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"key row not found: {keyId}");
        return (reader.GetString(0), reader.GetDecimal(1));
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

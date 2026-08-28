using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Routing;

/// <summary>固定快照的 IOptionsMonitor 替身，仅供测试注入模型→供应商映射。</summary>
internal sealed class FixedOptionsMonitor(RouterOptions value) : IOptionsMonitor<RouterOptions>
{
    public RouterOptions CurrentValue => value;
    public RouterOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<RouterOptions, string?> listener) => null;
}

/// <summary>
/// 审计分析服务聚合口径测试：基于 InMemoryRequestAuditStore 构造跨模型/分档/级联/多日的
/// 合成记录，验证总览、分模型、分供应商、分档、级联、路由原因与日趋势的数值正确性。
/// </summary>
public class AuditAnalysisServiceTests
{
    private static AuditAnalysisService CreateAnalyzer(
        InMemoryRequestAuditStore store, params (string Name, string Provider)[] models)
    {
        var options = new RouterOptions();
        foreach (var (name, provider) in models)
            options.Models.Add(new ModelEndpointOptions { Name = name, Provider = provider });
        return new AuditAnalysisService(store, new FixedOptionsMonitor(options));
    }

    private static RequestAuditRecord Rec(
        DateTime ts, string model, bool success, decimal cost, long latency,
        ModelTier tier = ModelTier.Medium, bool cascade = false, string? upgradedFrom = null,
        string reason = "initial", string? fusionRole = null, int cached = 0,
        string? requestId = null) => new(
        Timestamp: ts,
        RequestId: requestId,
        Model: model,
        EstimatedInputTokens: 10,
        PromptTokens: 100,
        CompletionTokens: 50,
        Cost: cost,
        LatencyMs: latency,
        SessionId: null,
        RoutingReason: reason,
        Success: success,
        ErrorMessage: null,
        IsStreaming: false,
        RoutedTier: tier,
        CascadeTriggered: cascade,
        UpgradedFrom: upgradedFrom,
        FusionRole: fusionRole,
        CachedInputTokens: cached);

    [Fact]
    public void Analyze_AggregatesAcrossAllDimensions()
    {
        var store = new InMemoryRequestAuditStore();
        var day1 = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc);

        store.Append(Rec(day1, "model-a", success: true, cost: 0.1m, latency: 100, tier: ModelTier.Strong));
        store.Append(Rec(day1, "model-a", success: false, cost: 0m, latency: 0, tier: ModelTier.Strong, reason: "failover"));
        store.Append(Rec(day1, "model-b", success: true, cost: 0.2m, latency: 200, tier: ModelTier.Cheap, cascade: true, upgradedFrom: "model-a", fusionRole: "outer", cached: 40));
        store.Append(Rec(day2, "model-a", success: true, cost: 0.3m, latency: 300, tier: ModelTier.Strong, reason: "initial"));
        store.Append(Rec(day2, "model-c", success: true, cost: 0.4m, latency: 500, tier: ModelTier.Medium, reason: "fusion: panel"));

        var analyzer = CreateAnalyzer(store);
        var report = analyzer.Analyze(
            new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));

        // 总览：5 条，4 成 1 败，成本合计 1.0，成功延迟样本 4 个。
        Assert.Equal(5, report.TotalRequests);
        Assert.Equal(4, report.Summary.Successes);
        Assert.Equal(1, report.Summary.Failures);
        Assert.Equal(80.0, report.Summary.SuccessRatePct);
        Assert.Equal(1.0, report.Summary.TotalCostUsd);
        Assert.Equal(4, report.Summary.LatencySamples);
        Assert.Equal(500, report.Summary.PromptTokens); // 5 × 100
        Assert.Equal(250, report.Summary.CompletionTokens);
        Assert.Equal(40, report.Summary.CachedInputTokens);

        // 成功延迟均值 (100+200+300+500)/4 = 275；P95 用线性插值取高分位。
        Assert.Equal(275.0, report.Summary.AvgLatencyMs);

        // 分模型：按请求量降序，model-a 3 条 1 败。
        Assert.Equal(3, report.ByModel.Count);
        var a = report.ByModel.Single(m => m.Model == "model-a");
        Assert.Equal(3, a.Requests);
        Assert.Equal(1, a.Failures);
        Assert.Equal(66.67, a.SuccessRatePct);
        Assert.Equal(0.4, a.CostUsd);

        // 分档：Strong 3 / Cheap 1 / Medium 1；成本份额合计 100%。
        Assert.Equal(3, report.ByTier.Single(t => t.Tier == "Strong").Requests);
        Assert.Equal(100.0, report.ByTier.Sum(t => t.CostSharePct), 2);

        // 级联：1/5 = 20%，upgradedFrom 分布 model-a × 1。
        Assert.Equal(1, report.Cascade.Triggered);
        Assert.Equal(20.0, report.Cascade.TriggerRatePct);
        Assert.Equal(1, report.Cascade.UpgradedFrom["model-a"]);

        // Fusion：1 条，角色 outer × 1。
        Assert.Equal(1, report.Fusion.FusionRequests);
        Assert.Equal(1, report.Fusion.ByRole["outer"]);

        // 路由原因：initial × 3 排首，failover 1 条 0 成。
        var topReason = report.ByReason.First();
        Assert.Equal("initial", topReason.Reason);
        Assert.Equal(3, topReason.Requests);
        Assert.Equal(100.0, topReason.SuccessRatePct);
        var failover = report.ByReason.Single(r => r.Reason == "failover");
        Assert.Equal(0, failover.SuccessRatePct);

        // 日趋势：两天升序，day1=3 条 2 成，day2=2 条 2 成。
        Assert.Equal(2, report.DailyTrend.Count);
        Assert.Equal("2026-08-18", report.DailyTrend[0].Day);
        Assert.Equal(3, report.DailyTrend[0].Requests);
        Assert.Equal(2, report.DailyTrend[0].Successes);
        Assert.Equal("2026-08-19", report.DailyTrend[1].Day);
        Assert.Equal(2, report.DailyTrend[1].Successes);
    }

    [Fact]
    public void Analyze_FusionRequests_DeduplicatedByRequestId()
    {
        // 一次融合请求产生多行（secondary/analyst 均带 FusionRole 且共享 request_id）：
        // FusionRequests 必须按 request_id 去重计数，而非逐行累加（旧口径夸大约 4 倍）。
        var store = new InMemoryRequestAuditStore();
        var ts = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        store.Append(Rec(ts, "model-a", success: true, cost: 0.1m, latency: 100, fusionRole: "secondary", requestId: "req-1"));
        store.Append(Rec(ts, "model-b", success: true, cost: 0.1m, latency: 100, fusionRole: "secondary", requestId: "req-1"));
        store.Append(Rec(ts, "model-c", success: true, cost: 0.1m, latency: 100, fusionRole: "analyst", requestId: "req-1"));
        // 第二次融合请求 + 一条无 request_id 的旧记录（按行回退计数）。
        store.Append(Rec(ts, "model-a", success: true, cost: 0.1m, latency: 100, fusionRole: "secondary", requestId: "req-2"));
        store.Append(Rec(ts, "model-b", success: true, cost: 0.1m, latency: 100, fusionRole: "analyst"));

        var analyzer = CreateAnalyzer(store);
        var report = analyzer.Analyze(ts.AddHours(-1), ts.AddHours(1));

        Assert.Equal(3, report.Fusion.FusionRequests); // req-1 去重为 1 + req-2 为 1 + 无 id 行为 1
        Assert.Equal(3, report.Fusion.ByRole["secondary"]);
        Assert.Equal(2, report.Fusion.ByRole["analyst"]);
    }

    [Fact]
    public void Analyze_EmptyWindow_ReturnsZeroedReport()
    {
        var store = new InMemoryRequestAuditStore();
        var analyzer = CreateAnalyzer(store);

        var report = analyzer.Analyze(
            new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(0, report.TotalRequests);
        Assert.Equal(0, report.Summary.Successes);
        Assert.Empty(report.ByModel);
        Assert.Empty(report.ByTier);
        Assert.Empty(report.DailyTrend);
    }

    [Fact]
    public void Analyze_OutOfRangeWindow_ExcludesRecordsOutside()
    {
        var store = new InMemoryRequestAuditStore();
        var ts = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        store.Append(Rec(ts, "model-a", success: true, cost: 0.1m, latency: 100));
        store.Append(Rec(ts.AddDays(5), "model-a", success: true, cost: 0.1m, latency: 100));

        var analyzer = CreateAnalyzer(store);
        var report = analyzer.Analyze(ts.AddHours(-1), ts.AddHours(1));

        Assert.Equal(1, report.TotalRequests);
    }

    [Fact]
    public void Analyze_ReversedRange_Throws()
    {
        var analyzer = CreateAnalyzer(new InMemoryRequestAuditStore());
        Assert.Throws<ArgumentException>(() => analyzer.Analyze(
            new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Analyze_ByProvider_GroupsConfiguredModels_AndUnknownBucketForRest()
    {
        var store = new InMemoryRequestAuditStore();
        var ts = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        store.Append(Rec(ts, "alpha-1", success: true, cost: 0.1m, latency: 100, cached: 60));
        store.Append(Rec(ts, "alpha-2", success: false, cost: 0m, latency: 0));
        store.Append(Rec(ts, "beta-1", success: true, cost: 0.2m, latency: 200));
        store.Append(Rec(ts, "orphan", success: true, cost: 0.3m, latency: 300));

        // "gone" 已下线无流量；"alpha-empty" Provider 为空——两者都不应产生额外桶。
        var analyzer = CreateAnalyzer(store,
            ("alpha-1", "one"), ("alpha-2", "one"), ("beta-1", "two"),
            ("gone", "gone-provider"), ("alpha-empty", ""));
        var report = analyzer.Analyze(ts.AddHours(-1), ts.AddHours(1));

        Assert.Equal(3, report.ByProvider.Count);

        var one = report.ByProvider.Single(p => p.Provider == "one");
        Assert.Equal(2, one.Requests);
        Assert.Equal(1, one.Failures);
        Assert.Equal(50.0, one.SuccessRatePct);
        Assert.Equal(2, one.ModelCount);
        Assert.Equal(200, one.PromptTokens);       // 2 × 100
        Assert.Equal(60, one.CachedInputTokens);   // 仅 alpha-1 带 cached
        Assert.Equal(100, one.CompletionTokens);   // 2 × 50
        Assert.Equal(0.1, one.CostUsd);

        var two = report.ByProvider.Single(p => p.Provider == "two");
        Assert.Equal(1, two.Requests);
        Assert.Equal(1, two.ModelCount);
        Assert.Equal(100, two.PromptTokens);

        var unknown = report.ByProvider.Single(p => p.Provider == AuditAnalysisService.UnknownProvider);
        Assert.Equal(1, unknown.Requests);
        Assert.Equal(1, unknown.ModelCount);

        // 逐模型缓存命中：ByModel 补充 CachedInputTokens。
        Assert.Equal(60, report.ByModel.Single(m => m.Model == "alpha-1").CachedInputTokens);
        Assert.Equal(0, report.ByModel.Single(m => m.Model == "alpha-2").CachedInputTokens);
    }

    [Fact]
    public void Analyze_DeletedModel_AttributesViaTombstones_AndReportCarriesMap()
    {
        // 回归保护：删除模型后其历史审计行不能降级"(未配置)"——
        // 墓碑（provider-tombstones 文档）兜底归组，报告同时下发映射供前端同口径渲染。
        var auditStore = new InMemoryRequestAuditStore();
        var ts = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);
        auditStore.Append(Rec(ts, "deleted-a", success: true, cost: 0.1m, latency: 100));
        auditStore.Append(Rec(ts, "live-a", success: true, cost: 0.2m, latency: 100));

        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-tomb-{Guid.NewGuid():N}.db");
        using var configDb = new AppConfigDbStore(dbPath);
        configDb.SaveDocument(AppConfigDbStore.ProviderTombstoneScope,
            System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["deleted-a"] = "deepseek" }));

        // 当前配置只剩 live-a；deleted-a 仅墓碑可归组。
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions { Name = "live-a", Provider = "acme" });

        var analyzer = new AuditAnalysisService(auditStore, new FixedOptionsMonitor(options), configDb);
        var report = analyzer.Analyze(ts.AddHours(-1), ts.AddHours(1));

        Assert.Equal(2, report.ByProvider.Count);
        Assert.Equal(1, report.ByProvider.Single(p => p.Provider == "acme").Requests);
        var deepseek = report.ByProvider.Single(p => p.Provider == "deepseek");
        Assert.Equal(1, deepseek.Requests);
        Assert.Equal(1, deepseek.ModelCount);
        Assert.DoesNotContain(report.ByProvider, p => p.Provider == AuditAnalysisService.UnknownProvider);

        // 报告内置映射与 ByProvider 同源：前端明细列直接消费。
        Assert.Equal("deepseek", report.ProviderByModel["deleted-a"]);
        Assert.Equal("acme", report.ProviderByModel["live-a"]);
    }
}

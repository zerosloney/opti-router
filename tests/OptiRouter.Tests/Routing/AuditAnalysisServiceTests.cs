using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 审计分析服务聚合口径测试：基于 InMemoryRequestAuditStore 构造跨模型/分档/级联/多日的
/// 合成记录，验证总览、分模型、分档、级联、路由原因与日趋势的数值正确性。
/// </summary>
public class AuditAnalysisServiceTests
{
    private static RequestAuditRecord Rec(
        DateTime ts, string model, bool success, decimal cost, long latency,
        ModelTier tier = ModelTier.Medium, bool cascade = false, string? upgradedFrom = null,
        string reason = "initial", string? fusionRole = null, int cached = 0) => new(
        Timestamp: ts,
        RequestId: null,
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

        var analyzer = new AuditAnalysisService(store);
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
    public void Analyze_EmptyWindow_ReturnsZeroedReport()
    {
        var store = new InMemoryRequestAuditStore();
        var analyzer = new AuditAnalysisService(store);

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

        var analyzer = new AuditAnalysisService(store);
        var report = analyzer.Analyze(ts.AddHours(-1), ts.AddHours(1));

        Assert.Equal(1, report.TotalRequests);
    }

    [Fact]
    public void Analyze_ReversedRange_Throws()
    {
        var analyzer = new AuditAnalysisService(new InMemoryRequestAuditStore());
        Assert.Throws<ArgumentException>(() => analyzer.Analyze(
            new DateTime(2026, 8, 19, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 18, 0, 0, 0, DateTimeKind.Utc)));
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 质量驱动 reward 通道测试。验证 <see cref="OutcomeRecorder.RecordQualityOutcome"/> 把显式质量 reward
/// 正确注入 Thompson 采样状态（而非延迟映射），修复"质量信号被丢弃、学习状态只看快慢"的缺陷。
/// </summary>
public sealed class QualityRewardTests
{
    private static OutcomeRecorder CreateRecorder(
        ThompsonStateStore tsStore,
        RouterOptions? options = null,
        ContextualBanditState? bandit = null)
    {
        var monitor = new FakeRouterOptionsMonitor(options ?? new RouterOptions());
        return new OutcomeRecorder(
            auditStore: null!,
            metrics: null!,
            ledger: null!,
            options: monitor,
            affinityCache: null!,
            tsStore: tsStore,
            promptAffinityStore: null!,
            quotaStore: null!,
            logger: NullLogger<OutcomeRecorder>.Instance,
            banditStore: bandit);
    }

    [Fact]
    public void LowQualityReward_PenalizesModel_InThompsonState()
    {
        // reward 0.0 = 答案质量差 → Beta（不佳次数）应增长，Alpha（良好次数）应收缩。
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        recorder.RecordQualityOutcome("cheap-model", 0.0);

        var stats = tsStore.GetOrAdd("cheap-model");
        // 初始 Alpha=1, Beta=1, discount=0.95: Alpha=1*0.95+0=0.95, Beta=1*0.95+(1-0)=1.95
        Assert.Equal(0.95, stats.Alpha, precision: 3);
        Assert.Equal(1.95, stats.Beta, precision: 3);
    }

    [Fact]
    public void HighQualityReward_RewardsModel_InThompsonState()
    {
        // reward 1.0 = 答案质量高 → Alpha（良好次数）应增长，Beta 应收缩。
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        recorder.RecordQualityOutcome("cheap-model", 1.0);

        var stats = tsStore.GetOrAdd("cheap-model");
        // Alpha=1*0.95+1=1.95, Beta=1*0.95+0=0.95
        Assert.Equal(1.95, stats.Alpha, precision: 3);
        Assert.Equal(0.95, stats.Beta, precision: 3);
    }

    [Fact]
    public void OutOfRangeReward_IsClampedToUnitInterval()
    {
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        // 5.0 应 clamp 到 1.0（等同高质量正反馈）
        recorder.RecordQualityOutcome("model-high", 5.0);
        // -3.0 应 clamp 到 0.0（等同低质量负反馈）
        recorder.RecordQualityOutcome("model-low", -3.0);

        var high = tsStore.GetOrAdd("model-high");
        var low = tsStore.GetOrAdd("model-low");
        Assert.Equal(1.95, high.Alpha, precision: 3);   // clamp(5.0)=1.0
        Assert.Equal(0.95, low.Alpha, precision: 3);    // clamp(-3.0)=0.0
    }

    [Fact]
    public void QualityReward_DistinctFromLatencyReward()
    {
        // 同一模型：先快成功（延迟 reward=1.0），再质量负反馈（reward=0.0）。
        // 两者都应作用于同一 Beta 分布，质量负反馈应抵消部分延迟正反馈。
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);

        recorder.RecordThompsonOutcome("cheap-model", elapsedMs: 0); // 0ms → reward 1.0（平滑曲线边界，避免耦合中间值）
        recorder.RecordQualityOutcome("cheap-model", 0.0);             // 质量差 → reward 0.0

        var stats = tsStore.GetOrAdd("cheap-model");
        // 第一次 reward 1.0: Alpha=1*0.95+1=1.95, Beta=1*0.95+0=0.95
        // 第二次 reward 0.0: Alpha=1.95*0.95+0=1.8525, Beta=0.95*0.95+1=1.9025
        Assert.Equal(1.8525, stats.Alpha, precision: 4);
        Assert.Equal(1.9025, stats.Beta, precision: 4);
    }

    [Fact]
    public void HardFailure_WithClassificationContext_UpdatesBandit()
    {
        var options = new RouterOptions();
        options.Routing.EnableContextualBandit = true;
        var bandit = new ContextualBanditState();
        var recorder = CreateRecorder(new ThompsonStateStore(), options, bandit);

        recorder.RecordThompsonOutcome("failing-model", elapsedMs: null, new RouterDecision
        {
            Candidates = Array.Empty<ModelEndpointOptions>(),
            Reason = "test",
            ClassificationSignal = "code-complex",
            ClassificationTargetTier = ModelTier.Strong,
            EstimatedInputTokens = 4095,
            RequestIsStreaming = true,
            RequestMessageCount = 3
        });

        Assert.Equal(1, bandit.GetOrAdd("failing-model").N);
        var feature = ContextualBanditFeatureBuilder.Build(
            "code-complex", ModelTier.Strong, 4095, isStreaming: true, messageCount: 3);
        Assert.Equal(0.0, bandit.Predict("failing-model", feature, alpha: 0.0), precision: 8);
    }

    [Fact]
    public void CostAware_HighCost_LowersReward_MoreThanFree()
    {
        var options = new RouterOptions();
        options.Routing.CostAwareWeight = 0.5;
        options.Routing.CostAwareBaselineUsd = 0.01m;
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore, options);

        recorder.RecordThompsonOutcome("expensive", elapsedMs: 100, cost: 0.1m);
        recorder.RecordThompsonOutcome("free", elapsedMs: 100, cost: 0m);

        Assert.True(tsStore.GetOrAdd("expensive").Alpha < tsStore.GetOrAdd("free").Alpha,
            "高成本模型 Alpha 应低于免费模型（成本感知压低 reward）");
    }

    [Fact]
    public void CostAware_Disabled_NoAdjustment()
    {
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore); // CostAwareWeight 默认 0

        recorder.RecordThompsonOutcome("m", elapsedMs: 0, cost: 0.5m);
        Assert.Equal(1.95, tsStore.GetOrAdd("m").Alpha, precision: 3); // 0ms→reward 1.0: Alpha=0.95+1
    }

    /// <summary>
    /// 平滑延迟→reward 映射的锚点断言（替代旧 0/0.3/1.0 阶跃）。
    /// </summary>
    [Fact]
    public void MapLatencyToReward_SmoothCurve_AnchorPoints()
    {
        const double target = 800.0;
        Assert.Equal(0.0, OutcomeRecorder.MapLatencyToReward(null, target));          // 失败
        Assert.Equal(1.0, OutcomeRecorder.MapLatencyToReward(0, target));              // 极快
        Assert.Equal(0.7, OutcomeRecorder.MapLatencyToReward((long)target, target), precision: 6);   // target 点 → 0.7
        Assert.Equal(0.3, OutcomeRecorder.MapLatencyToReward((long)(2 * target), target), precision: 6); // 2×target → 0.3
        Assert.Equal(0.3, OutcomeRecorder.MapLatencyToReward((long)(3 * target), target), precision: 6); // 超 2×target 地板 0.3

        // 单调性：越快 reward 越高。
        Assert.True(OutcomeRecorder.MapLatencyToReward(200, target) > OutcomeRecorder.MapLatencyToReward(500, target));
        Assert.True(OutcomeRecorder.MapLatencyToReward(900, target) > OutcomeRecorder.MapLatencyToReward(1500, target));
    }

    [Fact]
    public void ResolveLatencyTarget_PerTierHit_AndGlobalFallback()
    {
        var routing = new RoutingOptions(); // 默认 per-tier {Strong:1500, Medium:1000, Cheap:600}, 全局 800
        Assert.Equal(1500.0, OutcomeRecorder.ResolveLatencyTarget(ModelTier.Strong, routing));
        Assert.Equal(1000.0, OutcomeRecorder.ResolveLatencyTarget(ModelTier.Medium, routing));
        Assert.Equal(600.0, OutcomeRecorder.ResolveLatencyTarget(ModelTier.Cheap, routing));
        // 未传 tier → 回退全局
        Assert.Equal(800.0, OutcomeRecorder.ResolveLatencyTarget(null, routing));

        // per-tier 清空后，所有 tier 回退全局
        routing.ThompsonLatencyTargetMsByTier.Clear();
        Assert.Equal(800.0, OutcomeRecorder.ResolveLatencyTarget(ModelTier.Strong, routing));
    }

    [Fact]
    public void ExtractQualityFactor_DetectsLowQualitySignals()
    {
        const double penalty = 0.3;
        // null 响应（无可判内容）→ 不惩罚（失败由 latency reward=0 处理）
        Assert.Equal(1.0, OutcomeRecorder.ExtractQualityFactor(null, penalty));
        // 正常 stop + 有 content → 1.0
        var ok = new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"完整答案\"},\"finish_reason\":\"stop\"}]}", null);
        Assert.Equal(1.0, OutcomeRecorder.ExtractQualityFactor(ok, penalty));
        // finish_reason=length（截断）→ penalty
        var truncated = new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"部分\"},\"finish_reason\":\"length\"}]}", null);
        Assert.Equal(penalty, OutcomeRecorder.ExtractQualityFactor(truncated, penalty));
        // content_filter → penalty
        var filtered = new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"x\"},\"finish_reason\":\"content_filter\"}]}", null);
        Assert.Equal(penalty, OutcomeRecorder.ExtractQualityFactor(filtered, penalty));
        // 空 content → penalty
        var empty = new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\"}]}", null);
        Assert.Equal(penalty, OutcomeRecorder.ExtractQualityFactor(empty, penalty));
        // 正常短答（"yes"）不误伤 → 1.0
        var shortOk = new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"yes\"},\"finish_reason\":\"stop\"}]}", null);
        Assert.Equal(1.0, OutcomeRecorder.ExtractQualityFactor(shortOk, penalty));
    }

    [Fact]
    public void RecordThompsonOutcome_AppliesSmoothLatencyTimesQualityFactor()
    {
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore);
        // 100ms（平滑 reward=0.9625）× 质量因子 0.3（截断）→ reward≈0.28875
        recorder.RecordThompsonOutcome("m", elapsedMs: 100, qualityFactor: 0.3);
        double latencyReward = 1.0 - 0.3 * (100.0 / 800.0); // = 0.9625
        double expected = latencyReward * 0.3;
        var stats = tsStore.GetOrAdd("m");
        Assert.Equal(0.95 + expected, stats.Alpha, precision: 4);
    }

    [Fact]
    public void RecordThompsonOutcome_UsesPerTierTarget()
    {
        var options = new RouterOptions();
        var tsStore = new ThompsonStateStore();
        var recorder = CreateRecorder(tsStore, options);
        // Cheap per-tier target=600。1000ms 落在 (600, 1200]：0.7 - 0.4*((1000-600)/600) ≈ 0.4333。
        // 若误用全局 800：1000 落在 (800,1600]：0.7-0.4*(200/800)=0.6（更宽松，验证 per-tier 确实生效）。
        recorder.RecordThompsonOutcome("m", elapsedMs: 1000, actualTier: ModelTier.Cheap);
        const double cheapTarget = 600.0;
        double expectedReward = 0.7 - 0.4 * ((1000.0 - cheapTarget) / cheapTarget);
        Assert.Equal(0.95 + expectedReward, tsStore.GetOrAdd("m").Alpha, precision: 4);
    }
}

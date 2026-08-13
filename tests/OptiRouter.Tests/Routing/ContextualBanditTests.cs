using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// P3 上下文老虎机（LinUCB）测试：状态数学、特征构造、LatencyAwarePolicy 集成、配置校验。
/// </summary>
public class ContextualBanditTests
{
    // ---- ContextualBanditFeatureBuilder ----

    [Fact]
    public void FeatureBuilder_CodeComplex_OneHotSignalAndTier()
    {
        var x = ContextualBanditFeatureBuilder.Build("code-complex", ModelTier.Strong);

        Assert.Equal(ContextualBanditFeatureBuilder.Dimension, x.Length);
        Assert.Equal(15, x.Length);
        // code-complex 是信号列表第 2 位（index 1）。
        Assert.Equal(1.0, x[1]);
        // Strong 是 tier 列表第 1 位（8 个信号之后的 index 8）。
        Assert.Equal(1.0, x[8]);
        // bias 恒 1。
        Assert.Equal(1.0, x[14]);
        // 其余位为 0。
        Assert.Equal(0.0, x[0]);
        Assert.Equal(0.0, x[2]);
    }

    [Fact]
    public void FeatureBuilder_UnknownSignal_OnlyBiasAndTier()
    {
        var x = ContextualBanditFeatureBuilder.Build("unknown-signal", ModelTier.Cheap);

        // 未知信号 → 信号位全零；Cheap 是 tier 第 3 位（index 9）；bias=1。
        Assert.Equal(0.0, x[0]);
        Assert.Equal(1.0, x[10]);
        Assert.Equal(1.0, x[14]);
    }

    [Fact]
    public void FeatureBuilder_NullInputs_OnlyBias()
    {
        var x = ContextualBanditFeatureBuilder.Build(null, null);

        Assert.Equal(1.0, x[14]);
        for (int i = 0; i < 14; i++)
            Assert.Equal(0.0, x[i]);
    }

    [Fact]
    public void FeatureBuilder_IncludesStableRequestShape()
    {
        var x = ContextualBanditFeatureBuilder.Build(
            "semantic:deep-analysis",
            ModelTier.Strong,
            estimatedInputTokens: 4095,
            isStreaming: true,
            messageCount: 3);

        Assert.Equal(1.0, x[7]);
        Assert.Equal(1.0, x[8]);
        Assert.InRange(x[11], 0.59, 0.61);
        Assert.Equal(1.0, x[12]);
        Assert.Equal(1.0, x[13]);
    }

    // ---- ContextualBanditState ----

    [Fact]
    public void State_ColdStart_AllArmsEqualScore()
    {
        var state = new ContextualBanditState();
        var feature = ContextualBanditFeatureBuilder.Build("simple-qa", ModelTier.Cheap);

        // 冷启动：θ=0，仅 UCB 项（A 单位阵 → sqrt(xᵀx)）。所有 arm 同特征同分。
        double s1 = state.Predict("model-a", feature, 1.0);
        double s2 = state.Predict("model-b", feature, 1.0);

        Assert.Equal(s1, s2, 10);
    }

    [Fact]
    public void State_Update_PositiveRewardRaisesScore()
    {
        var state = new ContextualBanditState();
        var feature = ContextualBanditFeatureBuilder.Build("code-complex", ModelTier.Strong);

        // 模型 a 收到正奖励，模型 b 未更新 → a 的 θ·x 更高。
        state.Update("model-a", feature, 1.0, 0.95);
        state.Update("model-a", feature, 1.0, 0.95);
        state.Update("model-a", feature, 1.0, 0.95);

        double sa = state.Predict("model-a", feature, 0.0);  // α=0 纯利用
        double sb = state.Predict("model-b", feature, 0.0);

        Assert.True(sa > sb, $"expected model-a ({sa}) > model-b ({sb}) after positive rewards");
    }

    [Fact]
    public void State_Update_NegativeRewardLowersScore()
    {
        var state = new ContextualBanditState();
        var feature = ContextualBanditFeatureBuilder.Build("code-complex", ModelTier.Strong);

        state.Update("model-a", feature, 0.0, 0.95);  // 失败奖励
        state.Update("model-a", feature, 0.0, 0.95);

        double sa = state.Predict("model-a", feature, 0.0);
        double sb = state.Predict("model-b", feature, 0.0);

        // 0.0 奖励不把 θ 推负（b 全零 → θ=0），只不抬升——a 不应高于未训练的 b。
        Assert.True(sa <= sb, $"expected model-a ({sa}) <= model-b ({sb}) after failures");
    }

    [Fact]
    public void State_DiscountPreservesUnitRidgeForUntouchedFeatures()
    {
        var state = new ContextualBanditState();
        var feature = ContextualBanditFeatureBuilder.Build("simple-qa", ModelTier.Cheap);

        for (int i = 0; i < 100; i++)
            state.Update("model-a", feature, 1.0, 0.5);

        var arm = state.GetOrAdd("model-a");
        Assert.Equal(1.0, arm.A[0, 0], precision: 10);
    }

    [Fact]
    public void State_ContextSensitive_DifferentSignalsDifferentScores()
    {
        var state = new ContextualBanditState();
        var codeFeature = ContextualBanditFeatureBuilder.Build("code-complex", ModelTier.Strong);
        var qaFeature = ContextualBanditFeatureBuilder.Build("simple-qa", ModelTier.Cheap);

        // 模型 a 只在 code 场景收到正奖励。
        state.Update("model-a", codeFeature, 1.0, 0.95);
        state.Update("model-a", codeFeature, 1.0, 0.95);

        // 模型 b 只在 qa 场景收到正奖励。
        state.Update("model-b", qaFeature, 1.0, 0.95);
        state.Update("model-b", qaFeature, 1.0, 0.95);

        // code 场景：a 应优于 b（a 在 code 有正历史）。
        double aCode = state.Predict("model-a", codeFeature, 0.0);
        double bCode = state.Predict("model-b", codeFeature, 0.0);
        Assert.True(aCode > bCode, $"code: model-a ({aCode}) should beat model-b ({bCode})");

        // qa 场景：b 应优于 a（b 在 qa 有正历史）。
        double aQa = state.Predict("model-a", qaFeature, 0.0);
        double bQa = state.Predict("model-b", qaFeature, 0.0);
        Assert.True(bQa > aQa, $"qa: model-b ({bQa}) should beat model-a ({aQa})");
    }

    [Fact]
    public void State_Retain_RemovesStaleModels()
    {
        var state = new ContextualBanditState();
        var feature = ContextualBanditFeatureBuilder.Build("simple-qa", ModelTier.Cheap);
        state.Update("model-a", feature, 1.0, 0.95);
        state.Update("model-b", feature, 1.0, 0.95);
        Assert.Equal(2, state.Count);

        int removed = state.Retain(new[] { "model-a" });

        Assert.Equal(1, removed);
        Assert.Equal(1, state.Count);
    }

    [Fact]
    public void State_ConcurrentUpdates_NoThrow()
    {
        var state = new ContextualBanditState();
        var feature = ContextualBanditFeatureBuilder.Build("simple-qa", ModelTier.Cheap);

        // 并发更新同一 arm 不应抛异常（线程安全）。
        Parallel.For(0, 100, i => state.Update("model-a", feature, 1.0, 0.95));

        Assert.Equal(1, state.Count);
        double score = state.Predict("model-a", feature, 1.0);
        Assert.True(double.IsFinite(score));
    }

    // ---- LatencyAwarePolicy 集成 ----

    [Fact]
    public void LatencyAware_ContextualBandit_ContextAffectsSelection()
    {
        var options = TestHelpers.BuildOptions(
            ("model-a", ModelTier.Medium, 8000, 1m),
            ("model-b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableContextualBandit = true;
        options.Routing.EnableLatencyAware = false;
        options.Routing.EnableThompsonSampling = false;
        // α=0 纯利用：让 θ·x 均值主导（确定性），避免 UCB 探索项掩盖上下文差异。
        options.Routing.ContextualBanditAlpha = 0.0;

        var bandit = new ContextualBanditState();
        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(), new ThompsonStateStore(), null, bandit);

        // 训练：model-a 在 code 场景好，model-b 在 qa 场景好。
        var codeFeature = ContextualBanditFeatureBuilder.Build("code-complex", ModelTier.Strong);
        var qaFeature = ContextualBanditFeatureBuilder.Build("simple-qa", ModelTier.Cheap);
        bandit.Update("model-a", codeFeature, 1.0, 0.95);
        bandit.Update("model-a", codeFeature, 1.0, 0.95);
        bandit.Update("model-b", qaFeature, 1.0, 0.95);
        bandit.Update("model-b", qaFeature, 1.0, 0.95);

        // code 请求：决策带 code-complex 分类 → model-a 应优先。
        var codeCtx = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "```python\ndef f(): pass\n```")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0
        };
        // 初始候选 model-b 在前，验证 bandit 把 model-a 提到前（真正发生重排）。
        var codeInitial = new RouterDecision
        {
            Candidates = new[] { options.Models[1], options.Models[0] },  // [model-b, model-a]
            Reason = "initial",
            EstimatedInputTokens = 0,
            ClassificationSignal = "code-complex",
            ClassificationTargetTier = ModelTier.Strong
        };
        var codeResult = policy.Apply(codeCtx, codeInitial);
        Assert.Equal("model-a", codeResult.Candidates[0].Name);
        Assert.Contains("reordered", codeResult.Reason);
        Assert.Contains("[Contextual Bandit]", codeResult.Reason);

        // qa 请求：决策带 simple-qa 分类 → model-b 应优先。
        var qaCtx = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "hello")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0
        };
        var qaInitial = new RouterDecision
        {
            Candidates = options.Models.Where(m => m.Enabled).ToList(),
            Reason = "initial",
            EstimatedInputTokens = 0,
            ClassificationSignal = "simple-qa",
            ClassificationTargetTier = ModelTier.Cheap
        };
        var qaResult = policy.Apply(qaCtx, qaInitial);
        Assert.Equal("model-b", qaResult.Candidates[0].Name);
    }

    [Fact]
    public void LatencyAware_ContextualBandit_Disabled_BackwardCompatible()
    {
        // EnableContextualBandit=false（默认）→ 行为与现有一致（透传，无 bandit 重排）。
        var options = TestHelpers.BuildOptions(
            ("a", ModelTier.Medium, 8000, 1m),
            ("b", ModelTier.Medium, 8000, 1m));
        options.Routing.EnableLatencyAware = false;
        options.Routing.EnableThompsonSampling = false;

        var policy = new LatencyAwarePolicy(
            new StubLatencyStatsProvider(), new ThompsonStateStore(), null, new ContextualBanditState());

        var (ctx, initial) = Setup(options, options.Models);
        var result = policy.Apply(ctx, initial);

        Assert.Equal(initial.Candidates, result.Candidates);
        Assert.Contains("latency-aware: disabled", result.Reason);
    }

    private static (RouterContext Context, RouterDecision Initial) Setup(
        RouterOptions options, IEnumerable<ModelEndpointOptions> initialCandidates)
    {
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "hi")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0
        };
        var initial = new RouterDecision
        {
            Candidates = initialCandidates.ToList(),
            Reason = "initial",
            EstimatedInputTokens = 0
        };
        return (context, initial);
    }
}

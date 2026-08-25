using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class RouterEngineTests
{
    [Fact]
    public void Decide_LongInput_RoutesToLargeContextModel()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("small-model", ModelTier.Cheap, 8000, 0.005m));
        options.Routing.LongInputThresholdTokens = 1000;

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        // Code request triggers Strong tier, ensuring gpt-4o is in candidates.
        // 40000 chars → ~11430 tokens, needs context >= 11430*1.2 = 13716
        var longContent = new string('x', 40000);
        var request = TestHelpers.BuildRequest(("user", $"```{longContent}```"));

        var result = engine.Decide(request, options);

        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o", result.Candidates[0].Name);
    }

    [Fact]
    public void Decide_BudgetExhaustedWithDegrade_RoutesToCheapest()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));
        options.Budget.DailyBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Degrade;
        ledger.Record(2m); // exceeds daily

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        var request = TestHelpers.BuildRequest(("user", "hello"));

        var result = engine.Decide(request, options);

        Assert.Single(result.Candidates);
        Assert.Equal("deepseek-chat", result.Candidates[0].Name);
        Assert.Contains("degraded", result.Reason);
    }

    [Fact]
    public void Decide_DataSovereigntyAndFailover_DoesNotReintroduceCloudFallback()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("local-strong", ModelTier.Strong, 128000, 5m),
            ("cloud-medium", ModelTier.Medium, 128000, 0.15m));
        options.Models[0].IsLocalOrPrivate = true;
        options.Routing.EnableDataSovereignty = true;
        options.Routing.EnableFailover = true;

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new DataSovereigntyPolicy(),
            new FailoverPolicy(new ModelHealthTracker())
        });

        var request = TestHelpers.BuildRequest(("user", "hello"));
        var result = engine.Decide(request, options, new HashSet<string> { "local-strong" });

        Assert.Empty(result.Candidates);
        Assert.DoesNotContain(result.Candidates, model => !model.IsLocalOrPrivate);
    }

    [Fact]
    public void Decide_DataSovereigntyAndBudgetDegrade_DoesNotReintroduceCloudModel()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("local-strong", ModelTier.Strong, 128000, 5m),
            ("cloud-cheap", ModelTier.Cheap, 128000, 0.01m));
        options.Models[0].IsLocalOrPrivate = true;
        options.Budget.DailyBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Degrade;
        options.Routing.EnableDataSovereignty = true;
        ledger.Record(2m);

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new DataSovereigntyPolicy(),
            new BudgetGuardPolicy(ledger)
        });

        var request = TestHelpers.BuildRequest(("user", "hello"));
        var result = engine.Decide(request, options);

        Assert.Single(result.Candidates);
        Assert.Equal("local-strong", result.Candidates[0].Name);
        Assert.All(result.Candidates, model => Assert.True(model.IsLocalOrPrivate));
        Assert.Contains("degraded", result.Reason);
    }

    [Fact]
    public void Decide_PinnedModelFailed_FallsBackToConfiguredChain()
    {
        // 显式 pin 的模型失败后应走其配置的 fallbackChain，而不是"all candidates failed"：
        // 单调资格池若被 pin 缩到单元素，FailoverPolicy 将无法从 AllModels 解析出链中模型。
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("ox-alpha-01", ModelTier.Strong, 200000, 5m),
            ("ox-alpha", ModelTier.Strong, 200000, 5m),
            ("ox-alpha-02", ModelTier.Strong, 200000, 5m));
        options.Models[0].FallbackChain = new List<string> { "ox-alpha", "ox-alpha-02" };
        options.Routing.EnableFailover = true;

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new ExplicitModelPolicy(),
            new FailoverPolicy(new ModelHealthTracker())
        });

        var request = new ChatRequest
        {
            Model = "ox-alpha-01",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") }
        };

        var result = engine.Decide(request, options, new HashSet<string> { "ox-alpha-01" });

        Assert.NotEmpty(result.Candidates);
        Assert.Contains(result.Candidates, m => m.Name == "ox-alpha");
    }

    [Fact]
    public void Decide_PrimaryFailed_UsesFallbackChain()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        // Code request triggers RuleClassifier → Strong tier → only gpt-4o remains.
        // Mark gpt-4o as failed so Failover must build a fallback chain from remaining tiers.
        var request = TestHelpers.BuildRequest(("user", "```python\ndef foo(): pass\n```"));
        var failedModels = new HashSet<string> { "gpt-4o" };

        var result = engine.Decide(request, options, failedModels);

        Assert.Single(result.Candidates);
        Assert.Equal("gpt-4o-mini", result.Candidates[0].Name);
        Assert.Contains("fallback", result.Reason);
    }

    [Fact]
    public void Decide_CodeRequest_RoutesToStrongTier()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        var request = TestHelpers.BuildRequest(("user", "```python\ndef foo(): pass\n```"));

        var result = engine.Decide(request, options);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("rule-classifier", result.Reason);
    }

    [Fact]
    public void Decide_LongInputAndBudgetTight_SatisfiesBothConstraints()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("cheap-large", ModelTier.Cheap, 128000, 0.01m),
            ("cheap-small", ModelTier.Cheap, 8000, 0.005m));
        options.Routing.DefaultTier = ModelTier.Cheap;
        options.Budget.DailyBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Degrade;
        options.Routing.LongInputThresholdTokens = 1000;
        ledger.Record(2m); // budget exhausted

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        // Long content triggers LongInputPolicy; DefaultTier=Cheap so RuleClassifier keeps cheap candidates.
        // LongInputPolicy filters out cheap-small (8000 ctx < 13716 required).
        // BudgetGuardPolicy Degrade 应尊重上下文硬约束，只保留能装下输入的 Cheap 模型（cheap-large）。
        var longContent = new string('x', 40000);
        var request = TestHelpers.BuildRequest(("user", longContent));

        var result = engine.Decide(request, options);

        // B5 修复后：BudgetGuard Degrade 尊重上下文硬约束，cheap-small(8000) 装不下 ~11430 tokens 被排除，
        // 只剩 cheap-large，真正同时满足"降级到 cheap"与"上下文够大"两个约束。
        Assert.Single(result.Candidates);
        Assert.Equal("cheap-large", result.Candidates[0].Name);
        Assert.Contains("degraded", result.Reason);
    }

    [Fact]
    public void Decide_DisabledPolicies_SkipsThoseSteps()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));
        options.Routing.EnableRuleClassifier = false;
        options.Routing.EnableTokenEstimator = false;
        options.Routing.EnableBudgetGuard = false;
        options.Routing.EnableFailover = false;

        // Only RuleClassifier is registered (but disabled)
        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy()
        });

        var request = TestHelpers.BuildRequest(("user", "```code```"));

        var result = engine.Decide(request, options);

        // With rule classifier disabled, all 3 enabled models should remain as initial candidates
        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public void Decide_NoEnabledModels_ReturnsEmptyCandidates()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("disabled-model", ModelTier.Strong, 128000, 5m));
        options.Models[0].Enabled = false;

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        var request = TestHelpers.BuildRequest(("user", "hello"));

        var result = engine.Decide(request, options);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Decide_WithTiktokenEstimator_UsesBpeTokenCount()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m));

        var engine = new RouterEngine(
            ledger,
            new IRouterPolicy[] { new LongInputPolicy() },
            new TiktokenTokenEstimator());

        // 1000 x 'a'：真实 BPE 为 125 tokens（重复字符被压缩）。
        var request = TestHelpers.BuildRequest(("user", new string('a', 1000)));

        var result = engine.Decide(request, options);

        // 125 + 3（消息开销）= 128，而非分桶粗估的 253。
        Assert.Equal(128, result.EstimatedInputTokens);
    }

    [Fact]
    public void Decide_GroupAwareExecution_MatchesPolicyChainOrder()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new CapabilityFilterPolicy(),
            new RuleClassifierPolicy(),
            new LongInputPolicy(),
            new BudgetGuardPolicy(ledger),
            new FailoverPolicy(new ModelHealthTracker())
        });

        var request = TestHelpers.BuildRequest(("user", "```python\ndef foo(): pass\n```"));

        var result = engine.Decide(request, options);

        // Code request → Strong tier, gpt-4o primary.
        Assert.Equal("gpt-4o", result.Candidates[0].Name);
        Assert.Equal("code-detected", result.ClassificationSignal);
        Assert.Equal(ModelTier.Strong, result.ClassificationTargetTier);
        // ReasonEvents 结构化：按组依赖序累积（capability-filter → rule-classifier → ...）。
        Assert.Contains(result.ReasonEvents, e => e.Policy == "rule-classifier");
        Assert.Contains(result.ReasonEvents, e => e.Policy == "capability-filter");
    }

    [Fact]
    public void Decide_Policies_DeclareCorrectGroups()
    {
        // P2 分组契约：每个策略声明所属分组，供 RouterEngine 按依赖序执行。
        var ledger = new CostLedger();
        Assert.Equal(PolicyGroup.Filter, new CapabilityFilterPolicy().Group);
        Assert.Equal(PolicyGroup.Filter, new LongInputPolicy().Group);
        Assert.Equal(PolicyGroup.Filter, new FailoverPolicy(new ModelHealthTracker()).Group);
        // QuotaAware 只重排（不足后置）不剔除，修正为 Order 语义
        Assert.Equal(PolicyGroup.Order, new QuotaAwarePolicy(new UpstreamQuotaStateStore()).Group);
        Assert.Equal(PolicyGroup.Classify, new RuleClassifierPolicy().Group);
        Assert.Equal(PolicyGroup.Classify, new SemanticRouterPolicy().Group);
        Assert.Equal(PolicyGroup.Order, new LatencyAwarePolicy(new LatencyStatsCache(), new ThompsonStateStore()).Group);
        Assert.Equal(PolicyGroup.Order, new PromptCacheAffinityPolicy(new PromptCacheAffinityStore()).Group);
        Assert.Equal(PolicyGroup.Constraint, new BudgetGuardPolicy(ledger).Group);
        Assert.Equal(PolicyGroup.Constraint, new SessionAffinityPolicy(new MemoryCache(new MemoryCacheOptions())).Group);
        Assert.Equal(PolicyGroup.Constraint, new LoadBalancePolicy().Group);
    }

    [Fact]
    public void Decide_FillsNewFeatureFields()
    {
        // 验证 RouterEngine.Decide 正确填充新特征字段
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var engine = new RouterEngine(ledger, new IRouterPolicy[]
        {
            new RuleClassifierPolicy(),
            new BudgetGuardPolicy(ledger)
        });

        // 构造带 MaxTokens 和 tools 的 ChatRequest
        var request = TestHelpers.BuildRequest(("user", "你好，世界"));
        // 使用反射设置 MaxTokens 和 ExtensionData（因为 ChatRequest 是 record）
        var requestWithTokens = request with
        {
            MaxTokens = 2048,
            ExtensionData = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>
            {
                ["tools"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(@"[{""type"":""code""}]")
            }
        };

        var result = engine.Decide(requestWithTokens, options);

        // 断言新字段被正确填充
        Assert.True(result.CjkRatio > 0, "CJK ratio should be positive for Chinese text");
        Assert.Equal(2048, result.MaxTokens);
        Assert.True(result.HasTools, "HasTools should be true when tools are present");
    }
}

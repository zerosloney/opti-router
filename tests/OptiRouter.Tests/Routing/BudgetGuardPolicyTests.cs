using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class BudgetGuardPolicyTests
{
    private static RouterDecision Apply(
        BudgetGuardPolicy policy,
        RouterOptions options,
        IReadOnlyList<ModelEndpointOptions> candidates,
        string? sessionId = null)
    {
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "test")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0,
            FailedModels = new HashSet<string>(),
            SessionId = sessionId
        };
        var decision = new RouterDecision
        {
            Candidates = candidates,
            Reason = "initial",
            EstimatedInputTokens = 0
        };
        return policy.Apply(context, decision);
    }

    [Fact]
    public void Apply_UnderBudget_KeepsCandidatesUnchanged()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek", ModelTier.Cheap, 32000, 0.01m));
        options.Budget.DailyBudgetUsd = 100m;

        var policy = new BudgetGuardPolicy(ledger);
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, candidates);

        Assert.Equal(candidates.Count, result.Candidates.Count);
        Assert.Contains("budget-guard:", result.Reason);
    }

    [Fact]
    public void Apply_DailyBudgetExhausted_DegradeToCheapest()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));
        options.Budget.DailyBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Degrade;
        ledger.Record(1.5m); // exceeds daily budget

        var policy = new BudgetGuardPolicy(ledger);
        var candidates = options.Models.Where(m => m.Enabled).ToList();
        var result = Apply(policy, options, candidates);

        Assert.Single(result.Candidates);
        Assert.Equal("deepseek-chat", result.Candidates[0].Name);
        Assert.Contains("degraded", result.Reason);
    }

    [Fact]
    public void Apply_DailyBudgetExhausted_Reject_ReturnsEmptyCandidates()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m));
        options.Budget.DailyBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Reject;
        ledger.Record(1.5m);

        var policy = new BudgetGuardPolicy(ledger);
        var result = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList());

        Assert.Empty(result.Candidates);
        Assert.Contains("budget exhausted, reject", result.Reason);
    }

    [Fact]
    public void Apply_SessionBudgetExhausted_DegradeToCheapest()
    {
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));
        options.Budget.DailyBudgetUsd = 100m; // not exhausted
        options.Budget.SessionBudgetUsd = 0.5m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Degrade;
        ledger.Record(0.8m, "session-1"); // exceeds session budget for session-1

        var policy = new BudgetGuardPolicy(ledger);
        var result = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList(), sessionId: "session-1");

        Assert.Single(result.Candidates);
        Assert.Equal("deepseek-chat", result.Candidates[0].Name);
    }

    [Fact]
    public void Apply_SessionBudget_NoSessionIdHeader_SkipsSessionCheck()
    {
        // 缺 X-Session-Id 头时，即使配置了会话预算也不启用——仅日预算生效。
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m));
        options.Budget.DailyBudgetUsd = 100m; // not exhausted
        options.Budget.SessionBudgetUsd = 0.5m; // configured but should be skipped
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Reject;

        var policy = new BudgetGuardPolicy(ledger);
        // sessionId 为 null，会话预算跳过
        var result = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList(), sessionId: null);

        Assert.NotEmpty(result.Candidates);
        Assert.Contains("session=disabled(no-header)", result.Reason);
    }

    [Fact]
    public void Apply_OtherInflightRequestReserved_TreatsDailyBudgetAsExhausted()
    {
        // TOCTOU 防护：已入账 0.9 < 预算 1，但另一并发请求 in-flight 预留 0.5——
        // 守卫必须读"已入账 + 预留"（1.4 ≥ 1）拒绝，而不是等流结束后才反应。
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));
        options.Budget.DailyBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Reject;
        ledger.Record(0.9m);
        ledger.Reserve(0.5m); // 模拟另一并发请求的 in-flight 预扣

        var policy = new BudgetGuardPolicy(ledger);
        var result = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList());

        Assert.Empty(result.Candidates);
        Assert.True(result.BudgetExhausted);

        // 预留释放后恢复放行。
        ledger.Release(0.5m);
        var after = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList());
        Assert.NotEmpty(after.Candidates);
    }

    [Fact]
    public void Apply_SessionReservation_BlocksSessionBudget()
    {
        // 会话维度同样受 in-flight 预留约束。
        var ledger = new CostLedger();
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));
        options.Budget.DailyBudgetUsd = 100m;
        options.Budget.SessionBudgetUsd = 1m;
        options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Reject;
        ledger.Record(0.6m, "session-1");
        ledger.Reserve(0.5m, "session-1");

        var policy = new BudgetGuardPolicy(ledger);
        var result = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList(), sessionId: "session-1");

        Assert.Empty(result.Candidates);
        Assert.True(result.BudgetExhausted);
    }
}

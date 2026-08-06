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
        IReadOnlyList<ModelEndpointOptions> candidates)
    {
        var context = new RouterContext
        {
            Request = TestHelpers.BuildRequest(("user", "test")),
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = 0,
            FailedModels = new HashSet<string>()
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
        ledger.Record(0.8m); // exceeds session budget

        var policy = new BudgetGuardPolicy(ledger);
        var result = Apply(policy, options, options.Models.Where(m => m.Enabled).ToList());

        Assert.Single(result.Candidates);
        Assert.Equal("deepseek-chat", result.Candidates[0].Name);
    }
}

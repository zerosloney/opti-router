using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class RuleClassifierPolicyTests
{
    private static RouterDecision Apply(RuleClassifierPolicy policy, RouterOptions options, ChatRequest request)
    {
        var models = options.Models.Where(m => m.Enabled).ToList();
        var context = new RouterContext
        {
            Request = request,
            AllModels = models,
            Options = options,
            EstimatedInputTokens = 0
        };
        var initial = new RouterDecision
        {
            Candidates = models.OrderBy(m => (int)m.Tier).ThenByDescending(m => m.MaxContextTokens).ToList(),
            Reason = "initial",
            EstimatedInputTokens = 0
        };
        return policy.Apply(context, initial);
    }

    [Fact]
    public void Apply_ContainsCode_SelectsStrongTier()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "```csharp\npublic class Foo {}\n```"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
    }

    [Fact]
    public void Apply_SingleShortMessageNoCode_SelectsCheapTier()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "hello"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Cheap, m.Tier));
    }

    [Fact]
    public void Apply_LongSystemPromptMultiTurn_SelectsStrongTier()
    {
        var longSystem = new string('x', 2001);
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(
            ("system", longSystem),
            ("user", "continue"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
    }

    [Fact]
    public void Apply_NormalMultiTurn_SelectsDefaultTier()
    {
        // DefaultTier = Medium
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(
            ("user", "tell me about LLMs"),
            ("assistant", "LLMs are large language models."),
            ("user", "more details"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
    }

    [Fact]
    public void Apply_TargetTierHasNoModels_FallsBackToDefaultTier()
    {
        // No Strong models configured
        var options = TestHelpers.BuildOptions(
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("deepseek-chat", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "```python\ndef foo(): pass\n```"));

        var result = Apply(policy, options, request);

        // Falls back to DefaultTier = Medium
        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
    }

    [Theory]
    [InlineData("SELECT id, name FROM users WHERE active = 1")]
    [InlineData("create table orders (id int primary key, total decimal)")]
    [InlineData("#!/bin/bash\necho hello")]
    [InlineData("sudo apt-get update")]
    [InlineData("func main() { go func() {} }")]
    [InlineData("package main\nimport \"fmt\"")]
    [InlineData("fn fibonacci(n: u32) -> u32 { n }")]
    [InlineData("impl Iterator for MyStruct { }")]
    [InlineData("cargo build --release")]
    public void Apply_DetectsSqlShellGoRust_SelectsStrongTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
    }
}

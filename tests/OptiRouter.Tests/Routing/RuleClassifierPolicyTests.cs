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

    [Theory]
    [InlineData("求解这个微分方程: dy/dx = 2x")]
    [InlineData("计算二次方程 ax^2 + bx + c = 0 的根")]
    [InlineData("证明不等式 (a+b)/2 >= sqrt(ab)")]
    [InlineData("\\begin{equation}\nE = mc^2\n\\end{equation}")]
    [InlineData("\\frac{1}{2} + \\frac{1}{3} = ?")]
    [InlineData("对 f(x) = x^2 求导")]
    [InlineData("计算定积分 \\int_0^1 x dx")]
    [InlineData("f(x) = 2x + 1, find f(3)")]
    public void Apply_DetectsMath_SelectsStrongTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
    }

    [Theory]
    [InlineData("translate this book to French")]
    [InlineData("translate the paragraph into Japanese please")]
    [InlineData("帮我把这段翻译成英文")]
    [InlineData("翻译以下内容为日语")]
    [InlineData("把这封信翻译成德语")]
    public void Apply_DetectsTranslation_SelectsMediumTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 8000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
    }

    [Theory]
    // 讨论性文本，不应触发翻译/数学/代码。
    [InlineData("the translation quality is poor")]
    [InlineData("翻译理论很重要")]
    [InlineData("等于号表示赋值")]
    [InlineData("平均成绩是 85 分")]
    [InlineData("Can you select a nice shirt for me?")]
    [InlineData("This hotel offers first class service.")]
    [InlineData("Education is a public good in modern society.")]
    [InlineData("We need to import more raw materials from abroad.")]
    [InlineData("What is the main function of the human kidney?")]
    public void Apply_NaturalLanguageNoTrigger_SelectsDefaultTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        // 默认 tier = Medium（单轮短消息无代码无数学无翻译 → 不命中 simple-qa 因长度可能 >100 或多词，
        // 但翻译/数学/代码不应触发，避免误升档）。这里断言：不应是 Strong（无代码）。
        Assert.All(result.Candidates, m => Assert.NotEqual(ModelTier.Strong, m.Tier));
    }

    [Fact]
    public void Apply_TargetAndDefaultTierBothEmpty_KeepsOriginalCandidates()
    {
        // 配置只有 Strong + Cheap，无 Medium。翻译请求目标 tier = Medium，
        // DefaultTier 默认也是 Medium → 两级过滤都空。应保留原候选而非清空（避免 503）。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "translate this book to French"));

        var result = Apply(policy, options, request);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("keeping original", result.Reason);
    }

    [Fact]
    public void Apply_TargetTierEmpty_DefaultTierHasModels_SelectsDefaultTier()
    {
        // 目标 tier 空但 DefaultTier 有模型：应回落到 DefaultTier，不保留原候选。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "```csharp\npublic class Foo {}\n```"));

        var models = options.Models.Where(m => m.Enabled).ToList();
        var context = new RouterContext
        {
            Request = request,
            AllModels = models,
            Options = options,
            EstimatedInputTokens = 0
        };
        var previous = new RouterDecision
        {
            Candidates = models.Where(m => m.Tier != ModelTier.Strong).ToList(),
            Reason = "initial",
            EstimatedInputTokens = 0
        };

        var result = policy.Apply(context, previous);

        Assert.Single(result.Candidates);
        Assert.Equal(ModelTier.Medium, result.Candidates[0].Tier);
        Assert.Contains("fallback-to-default", result.Reason);
    }
}

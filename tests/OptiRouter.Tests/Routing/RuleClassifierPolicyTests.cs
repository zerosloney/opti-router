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

    [Theory]
    [InlineData("帮我 debug 这段代码，为什么报错？\n```python\ndef f():\n    return 1/0\n```")]
    [InlineData("fix this bug: \n```js\nlet x = null; x.y;\n```")]
    [InlineData("重构下面的函数，让它更清晰\n```csharp\npublic void A(int x){ if(x>0){...} }\n```")]
    [InlineData("优化这个算法的性能\n```python\ndef sort(arr): ...\n```")]
    [InlineData("这个程序崩溃了，异常在哪？\n```java\ntry{}catch(Exception e){}\n```")]
    public void Apply_ComplexCodeIntent_SelectsStrongTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-complex", result.Reason);
        Assert.Equal(RequestComplexity.Complex, result.RequestComplexity);
    }

    [Theory]
    [InlineData("一个 hello world 示例\n```python\nprint('hello')\n```")]
    [InlineData("给一个简单的示例代码\n```go\npackage main\nfunc main(){}\n```")]
    [InlineData("写一个脚手架项目\n```bash\nmkdir -p src\n```")]
    public void Apply_SimpleCodeIntent_SelectsMediumTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
        Assert.Contains("code-simple", result.Reason);
        Assert.Equal(RequestComplexity.Standard, result.RequestComplexity);
    }

    [Fact]
    public void Apply_BareCodeBlockNoIntent_KeepsStrongTier()
    {
        // 裸代码块（无复杂/简单意图词）：保守 Strong，代码能力优先不降级。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "```python\ndef quicksort(arr):\n    return arr\n```"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-detected", result.Reason);
        Assert.Equal(RequestComplexity.Complex, result.RequestComplexity);
    }

    [Fact]
    public void Apply_CodeClassNamedExample_NotDowngradedToSimple()
    {
        // 回归保护：裸代码类名含 "Example" 不应触发 simple 意图被降级到 Medium
        // （\bexample\b 会误配 "public class Example {}"）。复杂/简单信号均无 → 保守 Strong。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "public class Example {}"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-detected", result.Reason);
    }

    [Fact]
    public void Apply_CodeNamedBasicAuth_NotDowngradedToSimple()
    {
        // 回归保护：\bbasic\b 会误配 "BasicAuth" 等标识符 → 应保持 Strong。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "class BasicAuth { string token; }"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-detected", result.Reason);
    }

    [Fact]
    public void Apply_ComplexAndSimpleSignalsTogether_ComplexWins()
    {
        // 复杂+简单信号同现：complex 优先，不降级到 Medium。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "explain and fix this bug\n```python\ndef f():\n    return 1/0\n```"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-complex", result.Reason);
    }

    [Fact]
    public void Apply_ExplainCode_NotDowngradedToSimple()
    {
        // 语义保护：explain/解释 不再归为简单意图——解释复杂代码需要 Strong 推理。
        // 无其它简单信号时保守 Strong（code-detected），避免把复杂代码解释任务降级到 Medium。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "解释一下这段代码\n```python\ndef quicksort(arr):\n    return arr\n```"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-detected", result.Reason);
    }

    [Fact]
    public void Apply_CodeBlockContainingHelloWorldString_NotDowngradedToSimple()
    {
        // 代码正文泄漏保护：代码块内的字符串/注释含 "hello world" 不应触发 simple 意图。
        // 意图检测只跑指令文本（剔除代码块）→ 无简单信号 → 保守 Strong。
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "```python\nprint(\"hello world\")\n```"));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("code-detected", result.Reason);
    }

    [Fact]
    public void Apply_StructuredFields_PopulatedForCodeComplex()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "修复这个 bug\n```python\ndef f(): return 1/0\n```"));

        var result = Apply(policy, options, request);

        Assert.Equal("code-complex", result.ClassificationSignal);
        Assert.Equal(ModelTier.Strong, result.ClassificationTargetTier);
        Assert.Equal(RequestComplexity.Complex, result.RequestComplexity);
        // Reason 保持 target=Tier(signal) 可解析格式（analyze_audit 依赖）。
        Assert.Contains("target=Strong(code-complex)", result.Reason);
        // ReasonEvents 结构化事件存在。
        Assert.Contains(result.ReasonEvents, e => e.Policy == "rule-classifier"
            && e.Detail.Contains("code-complex"));
    }

    [Fact]
    public void Apply_StructuredFields_PopulatedForSimpleQa()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 32000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "hello"));

        var result = Apply(policy, options, request);

        Assert.Equal("simple-qa", result.ClassificationSignal);
        Assert.Equal(ModelTier.Cheap, result.ClassificationTargetTier);
        Assert.Equal(RequestComplexity.Simple, result.RequestComplexity);
        Assert.Contains("target=Cheap(simple-qa)", result.Reason);
    }

    [Fact]
    public void Apply_StructuredFields_NullWhenDisabled()
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m));
        options.Routing.EnableRuleClassifier = false;

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", "hello"));

        var result = Apply(policy, options, request);

        Assert.Null(result.ClassificationSignal);
        Assert.Null(result.ClassificationTargetTier);
    }

    [Theory]
    // 数学扩展：Unicode 符号、中文术语、英文词汇。
    [InlineData("计算 ∑(i=1..n) i 的值")]
    [InlineData("求 √2 + √3 的近似值")]
    [InlineData("当 x → ∞ 时求极限")]
    [InlineData("矩阵 A 的特征值怎么求")]
    [InlineData("解这个方程组: x+y=3, x-y=1")]
    [InlineData("线性代数中的秩是什么")]
    [InlineData("贝叶斯公式的推导")]
    [InlineData("find the derivative of sin(x)")]
    [InlineData("prove that √2 is irrational")]
    [InlineData("calculate the standard deviation of this dataset")]
    [InlineData("what is the eigenvalue problem")]
    public void Apply_DetectsMathExpanded_SelectsStrongTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("math-detected", result.Reason);
    }

    [Theory]
    // 翻译扩展：口语化指令与语言对缩写。
    [InlineData("帮我翻译")]
    [InlineData("请翻译")]
    [InlineData("翻译一下")]
    [InlineData("翻译这段")]
    [InlineData("中译英")]
    [InlineData("英译中")]
    [InlineData("can you translate this for me")]
    [InlineData("please translate the following text")]
    public void Apply_DetectsTranslationExpanded_SelectsMediumTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 8000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
        Assert.Contains("translation-request", result.Reason);
    }

    [Theory]
    // 复杂指令扩展：深度分析/多步骤/结构化长文。
    [InlineData("帮我做一个深入的可行性分析")]
    [InlineData("对比分析这两个方案的利弊")]
    [InlineData("一步一步推导这个结论")]
    [InlineData("一步一步教我搭这个环境")]
    [InlineData("我要写一篇关于气候变化的论文")]
    [InlineData("explain step by step how quicksort works")]
    [InlineData("compare and contrast REST and GraphQL")]
    [InlineData("list the pros and cons of microservices")]
    public void Apply_DetectsComplexInstruction_SelectsStrongTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Strong, m.Tier));
        Assert.Contains("complex-instruction", result.Reason);
    }

    [Theory]
    // 写作类：体裁明确的语言生成 → Medium。
    [InlineData("帮我写一封请假邮件")]
    [InlineData("写封感谢信给客户")]
    [InlineData("帮我写本周周报")]
    [InlineData("起草一份合作备忘录")]
    [InlineData("润色这段文案")]
    [InlineData("write an email to my manager")]
    [InlineData("draft a press release for the launch")]
    public void Apply_DetectsWritingRequest_SelectsMediumTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m),
            ("cheap", ModelTier.Cheap, 8000, 0.01m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
        Assert.Contains("writing-request", result.Reason);
    }

    [Theory]
    // 翻译优先于复杂指令：明确翻译意图不应被"论文"等关键词抢入 Strong。
    [InlineData("帮我翻译这篇论文的摘要")]
    [InlineData("translate this research report into Chinese")]
    public void Apply_TranslationWinsOverComplexKeywords_SelectsMediumTier(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.All(result.Candidates, m => Assert.Equal(ModelTier.Medium, m.Tier));
    }

    [Theory]
    // 扩展后的误报防护：讨论性/闲聊文本不应升档。
    [InlineData("翻译理论在语言学中很重要")]
    [InlineData("统计学家的就业前景如何")]
    [InlineData("这部电影一步一步展现了主角的成长")]
    [InlineData("she works as a research assistant")]
    [InlineData("my essay got a good grade last semester")]
    public void Apply_ExpandedPatterns_DoNotFalsePositive(string content)
    {
        var options = TestHelpers.BuildOptions(
            ("gpt-4o", ModelTier.Strong, 128000, 5m),
            ("gpt-4o-mini", ModelTier.Medium, 128000, 0.15m));

        var policy = new RuleClassifierPolicy();
        var request = TestHelpers.BuildRequest(("user", content));

        var result = Apply(policy, options, request);

        Assert.DoesNotContain("math-detected", result.Reason);
        Assert.DoesNotContain("translation-request", result.Reason);
        Assert.DoesNotContain("complex-instruction", result.Reason);
        Assert.DoesNotContain("writing-request", result.Reason);
    }
}

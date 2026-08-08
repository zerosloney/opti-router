using System.Collections.Generic;
using Xunit;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using OptiRouter.Clients;

namespace OptiRouter.Tests.Routing;

public class SemanticRouterTests
{
    private static List<ModelEndpointOptions> GetMockModels()
    {
        return new List<ModelEndpointOptions>
        {
            new() { Name = "model-strong", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 },
            new() { Name = "model-medium", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
            new() { Name = "model-cheap", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 16000 }
        };
    }

    [Fact]
    public void DisabledSemanticRouter_ShouldKeepPreviousDecision()
    {
        // Arrange
        var policy = new SemanticRouterPolicy();
        var options = new RouterOptions();
        options.Routing.EnableSemanticRouter = false;

        var context = new RouterContext
        {
            Request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") } },
            AllModels = GetMockModels(),
            Options = options
        };

        var previousDecision = new RouterDecision
        {
            Candidates = GetMockModels(),
            Reason = "initial"
        };

        // Act
        var result = policy.Apply(context, previousDecision);

        // Assert
        Assert.Contains("semantic-router: disabled", result.Reason);
        Assert.Equal(previousDecision.Candidates.Count, result.Candidates.Count);
    }

    [Fact]
    public void MatchingPhrase_ShouldDirectToTargetTier()
    {
        // Arrange
        var policy = new SemanticRouterPolicy();
        var options = new RouterOptions();
        options.Routing.EnableSemanticRouter = true;
        options.Routing.SemanticSimilarityThreshold = 0.25;
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "code-assistance",
                TargetTier = ModelTier.Strong,
                Phrases = new List<string>
                {
                    "write a python function to compute fibonacci",
                    "implement binary search in rust"
                }
            },
            new()
            {
                Name = "casual-chat",
                TargetTier = ModelTier.Cheap,
                Phrases = new List<string>
                {
                    "how is the weather today",
                    "tell me a funny story"
                }
            }
        };

        var mockModels = GetMockModels();

        // 1. 测试高度相似的代码编写意图 -> 应该路由到 Strong 档模型
        var codingRequest = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "write python code for fibonacci") }
        };
        var codingContext = new RouterContext
        {
            Request = codingRequest,
            AllModels = mockModels,
            Options = options
        };
        var codingDecision = policy.Apply(codingContext, new RouterDecision { Candidates = mockModels, Reason = "init" });

        Assert.Contains("matched=code-assistance", codingDecision.Reason);
        Assert.Single(codingDecision.Candidates);
        Assert.Equal("model-strong", codingDecision.Candidates[0].Name);

        // 2. 测试相似的闲聊意图 -> 应该路由到 Cheap 档模型
        var casualRequest = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "tell me a funny story please") }
        };
        var casualContext = new RouterContext
        {
            Request = casualRequest,
            AllModels = mockModels,
            Options = options
        };
        var casualDecision = policy.Apply(casualContext, new RouterDecision { Candidates = mockModels, Reason = "init" });

        Assert.Contains("matched=casual-chat", casualDecision.Reason);
        Assert.Single(casualDecision.Candidates);
        Assert.Equal("model-cheap", casualDecision.Candidates[0].Name);

        // 3. 测试完全无关的输入 -> 应该不匹配 (no-match)
        var unrelatedRequest = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "buy stocks now") }
        };
        var unrelatedContext = new RouterContext
        {
            Request = unrelatedRequest,
            AllModels = mockModels,
            Options = options
        };
        var unrelatedDecision = policy.Apply(unrelatedContext, new RouterDecision { Candidates = mockModels, Reason = "init" });

        Assert.Contains("semantic-router: no-match", unrelatedDecision.Reason);
        Assert.Equal(mockModels.Count, unrelatedDecision.Candidates.Count);
    }

    [Fact]
    public void HotReload_ShouldImmediatelyUseNewRoutesAndPhrases()
    {
        // Arrange
        var policy = new SemanticRouterPolicy();
        var options = new RouterOptions();
        options.Routing.EnableSemanticRouter = true;
        options.Routing.SemanticSimilarityThreshold = 0.25;
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "original-route",
                TargetTier = ModelTier.Strong,
                Phrases = new List<string> { "write compile and run programs" }
            }
        };

        var mockModels = GetMockModels();
        var query = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "database query optimize") } };

        // 第一次运行：此时无数据库匹配，应返回 no-match
        var context1 = new RouterContext
        {
            Request = query,
            AllModels = mockModels,
            Options = options
        };
        var decision1 = policy.Apply(context1, new RouterDecision { Candidates = mockModels, Reason = "init" });
        Assert.Contains("semantic-router: no-match", decision1.Reason);

        // Act: 模拟 IOptionsMonitor 动态更新，替换整个 SemanticRoutes 数组
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "database-route",
                TargetTier = ModelTier.Medium,
                Phrases = new List<string> { "database query optimization index" }
            }
        };

        // 第二次运行：应当即时热更新词袋特征并成功命中 database-route 路由到 Medium
        var context2 = new RouterContext
        {
            Request = query,
            AllModels = mockModels,
            Options = options
        };
        var decision2 = policy.Apply(context2, new RouterDecision { Candidates = mockModels, Reason = "init" });

        // Assert
        Assert.Contains("matched=database-route", decision2.Reason);
        Assert.Single(decision2.Candidates);
        Assert.Equal("model-medium", decision2.Candidates[0].Name);
    }

    /// <summary>
    /// TF-IDF 改进验证：常见词 "write" 同时出现在 code 和 chat route 时，纯词频会因 "write" 高权重
    /// 把 "write me a funny story" 误判到 code-assistance。IDF 加权后 "write" 权重被抑制，
    /// "story"/"funny" 成判别特征，应命中 casual-chat 而非 code-assistance。
    /// </summary>
    [Fact]
    public void TfIdf_CommonWordDepreciated_StoryNotRoutedToCode()
    {
        var policy = new SemanticRouterPolicy();
        var options = new RouterOptions();
        options.Routing.EnableSemanticRouter = true;
        options.Routing.SemanticSimilarityThreshold = 0.25;
        // 关键：两个 route 都含 "write"，制造常见词冲突。
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "code-assistance",
                TargetTier = ModelTier.Strong,
                Phrases = new List<string>
                {
                    "write a python function to compute fibonacci",
                    "write code to implement binary search"
                }
            },
            new()
            {
                Name = "casual-chat",
                TargetTier = ModelTier.Cheap,
                Phrases = new List<string>
                {
                    "write me a funny story",
                    "tell me a joke"
                }
            }
        };

        var mockModels = GetMockModels();
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "write me a funny story please") }
        };
        var context = new RouterContext
        {
            Request = request,
            AllModels = mockModels,
            Options = options
        };
        var decision = policy.Apply(context, new RouterDecision { Candidates = mockModels, Reason = "init" });

        // 应命中 casual-chat（Cheap），而非 code-assistance（Strong）。
        Assert.Contains("matched=casual-chat", decision.Reason);
        Assert.Single(decision.Candidates);
        Assert.Equal("model-cheap", decision.Candidates[0].Name);
    }

    [Fact]
    public void Apply_FiltersWithinPreviousCandidates_PreservesUpstreamFilters()
    {
        var policy = new SemanticRouterPolicy();
        var options = new RouterOptions();
        options.Routing.EnableSemanticRouter = true;
        options.Routing.SemanticSimilarityThreshold = 0.25;
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "casual-chat",
                TargetTier = ModelTier.Cheap,
                Phrases = new List<string> { "tell me a funny story" }
            }
        };

        var allModels = new List<ModelEndpointOptions>
        {
            new() { Name = "model-strong-vision", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000, Tags = { "vision" } },
            new() { Name = "model-cheap-no-vision", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 16000 }
        };

        // 模拟上游 CapabilityFilterPolicy 已经过滤掉了不支持 vision 的 model-cheap-no-vision
        var previousCandidates = new List<ModelEndpointOptions>
        {
            allModels[0] // 仅剩 model-strong-vision
        };

        var request = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "tell me a funny story") }
        };
        var context = new RouterContext
        {
            Request = request,
            AllModels = allModels,
            Options = options
        };

        var decision = policy.Apply(context, new RouterDecision { Candidates = previousCandidates, Reason = "capability-filtered" });

        // 由于 previousCandidates 中没有 Cheap 模型，SemanticRouterPolicy 尝试在 previousCandidates 中按 Cheap 筛选结果为 0，
        // 从而不覆盖 previousCandidates（不带回无 vision 标签的 model-cheap-no-vision）。
        Assert.Single(decision.Candidates);
        Assert.Equal("model-strong-vision", decision.Candidates[0].Name);
    }

    [Fact]
    public void CjkTokenizer_SegmenterExtractsUnigramsAndBigrams()
    {
        var tokens = TfIdfSemanticVectorEngine.Tokenize("写 Python 快排");
        Assert.Contains("python", tokens);
        Assert.Contains("写", tokens);
        Assert.Contains("快", tokens);
        Assert.Contains("排", tokens);
        Assert.Contains("快排", tokens);
    }

    [Fact]
    public void ChinesePhrases_MatchesSemanticRoute()
    {
        var policy = new SemanticRouterPolicy(new TfIdfSemanticVectorEngine());
        var options = new RouterOptions();
        options.Routing.EnableSemanticRouter = true;
        options.Routing.SemanticSimilarityThreshold = 0.25;
        options.Routing.SemanticRoutes = new List<SemanticRouteOptions>
        {
            new()
            {
                Name = "chinese-code",
                TargetTier = ModelTier.Strong,
                Phrases = new List<string> { "用 Python 实现快速排序算法" }
            },
            new()
            {
                Name = "chinese-translation",
                TargetTier = ModelTier.Medium,
                Phrases = new List<string> { "翻译下面的文字为英语" }
            }
        };

        var mockModels = GetMockModels();

        // 1. 中文代码类 Query -> 应命中 chinese-code (Strong)
        var codeRequest = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "请帮我写个 Python 快排") }
        };
        var codeContext = new RouterContext { Request = codeRequest, AllModels = mockModels, Options = options };
        var codeDecision = policy.Apply(codeContext, new RouterDecision { Candidates = mockModels, Reason = "init" });

        Assert.Contains("matched=chinese-code", codeDecision.Reason);
        Assert.Equal("model-strong", codeDecision.Candidates[0].Name);

        // 2. 中文翻译类 Query -> 应命中 chinese-translation (Medium)
        var transRequest = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "请把这段文本翻译成英文") }
        };
        var transContext = new RouterContext { Request = transRequest, AllModels = mockModels, Options = options };
        var transDecision = policy.Apply(transContext, new RouterDecision { Candidates = mockModels, Reason = "init" });

        Assert.Contains("matched=chinese-translation", transDecision.Reason);
        Assert.Equal("model-medium", transDecision.Candidates[0].Name);
    }
}

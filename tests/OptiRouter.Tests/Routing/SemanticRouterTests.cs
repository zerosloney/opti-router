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
}

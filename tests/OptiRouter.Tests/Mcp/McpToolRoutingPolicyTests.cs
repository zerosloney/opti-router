using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Mcp;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Mcp;

public class McpToolRoutingPolicyTests
{
    private static RouterContext CreateContext(ChatRequest request, bool enableMcp = true)
    {
        var options = new RouterOptions
        {
            Routing = new RoutingOptions
            {
                EnableMcpComplexityRouting = enableMcp
            }
        };

        var models = new List<ModelEndpointOptions>
        {
            new() { Id = "cheap-1", Name = "gpt-4o-mini", Tier = ModelTier.Cheap, Enabled = true },
            new() { Id = "medium-1", Name = "gpt-4o", Tier = ModelTier.Medium, Enabled = true },
            new() { Id = "strong-1", Name = "claude-3-5-sonnet", Tier = ModelTier.Strong, Enabled = true }
        };

        return new RouterContext
        {
            Request = request,
            Options = options,
            AllModels = models
        };
    }

    [Fact]
    public void McpToolRoutingPolicy_HighComplexity_PromotesStrongTier()
    {
        var policy = new McpToolRoutingPolicy();

        var toolsJson = @"[
            { ""type"": ""function"", ""function"": { ""name"": ""t1"", ""parameters"": { ""type"": ""object"", ""properties"": { ""a"": { ""type"": ""string"" } } } } },
            { ""type"": ""function"", ""function"": { ""name"": ""t2"", ""parameters"": { ""type"": ""object"", ""properties"": { ""b"": { ""type"": ""string"" } } } } },
            { ""type"": ""function"", ""function"": { ""name"": ""t3"", ""parameters"": { ""type"": ""object"", ""properties"": { ""c"": { ""type"": ""string"" } } } } },
            { ""type"": ""function"", ""function"": { ""name"": ""t4"", ""parameters"": { ""type"": ""object"", ""properties"": { ""d"": { ""type"": ""string"" } } } } },
            { ""type"": ""function"", ""function"": { ""name"": ""t5"", ""parameters"": { ""type"": ""object"", ""properties"": { ""e"": { ""type"": ""string"" } } } } },
            { ""type"": ""function"", ""function"": { ""name"": ""t6"", ""parameters"": { ""type"": ""object"", ""properties"": { ""f"": { ""type"": ""string"" } } } } }
        ]";

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Run multiple operations") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["tools"] = JsonDocument.Parse(toolsJson).RootElement
            }
        };

        var context = CreateContext(req, enableMcp: true);
        var initialDecision = new RouterDecision
        {
            Candidates = context.AllModels.ToList(),
            Reason = "initial"
        };

        var result = policy.Apply(context, initialDecision);

        Assert.Equal("claude-3-5-sonnet", result.Candidates[0].Name);
        Assert.Equal("gpt-4o", result.Candidates[1].Name);
        Assert.Equal("gpt-4o-mini", result.Candidates[2].Name);
        Assert.Contains("mcp-tool-complexity", result.Reason);
    }

    [Fact]
    public void McpToolRoutingPolicy_SimpleComplexity_PromotesCheapTier()
    {
        var policy = new McpToolRoutingPolicy();

        var toolsJson = @"[
            { ""type"": ""function"", ""function"": { ""name"": ""ping"", ""parameters"": { ""type"": ""object"", ""properties"": { ""host"": { ""type"": ""string"" } } } } }
        ]";

        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Ping google.com") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["tools"] = JsonDocument.Parse(toolsJson).RootElement
            }
        };

        var context = CreateContext(req, enableMcp: true);
        var initialDecision = new RouterDecision
        {
            Candidates = new List<ModelEndpointOptions>
            {
                context.AllModels.First(m => m.Tier == ModelTier.Strong),
                context.AllModels.First(m => m.Tier == ModelTier.Cheap)
            },
            Reason = "initial"
        };

        var result = policy.Apply(context, initialDecision);

        Assert.Equal("gpt-4o-mini", result.Candidates[0].Name);
        Assert.Equal("claude-3-5-sonnet", result.Candidates[1].Name);
    }
}

using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

public class ModelDisplayIdsTests
{
    private static List<ModelEndpointOptions> Models() => new()
    {
        new() { Name = "deepseek-primary", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1" },
        new() { Name = "deepseek-chat-backup", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1" },
        new() { Name = "gpt-4o", Id = "gpt-4o-2024-11-20", BaseUrl = "https://api.openai.com/v1" }
    };

    [Fact]
    public void Compute_ProviderSlashId_WithDuplicateNumbering()
    {
        var ids = ModelDisplayIds.Compute(Models());

        Assert.Equal(
            new[] { "deepseek/deepseek-chat", "deepseek/deepseek-chat #2", "openai/gpt-4o-2024-11-20" },
            ids);
    }

    [Fact]
    public void Compute_ExplicitProvider_WinsOverBaseUrlInference()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "x", Id = "llm", BaseUrl = "https://proxy.example.com/v1", Provider = "mycloud" }
        };

        Assert.Equal("mycloud/llm", ModelDisplayIds.Compute(models)[0]);
    }

    [Fact]
    public void Resolve_ByRoutingName()
    {
        var matches = ModelDisplayIds.Resolve(Models(), "gpt-4o");

        Assert.Equal("gpt-4o", Assert.Single(matches).Name);
    }

    [Fact]
    public void Resolve_ByDisplayId_ReturnsAllEndpointsOfferingModel()
    {
        var matches = ModelDisplayIds.Resolve(Models(), "deepseek/deepseek-chat");

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Equal("deepseek-chat", m.Id));
    }

    [Fact]
    public void Resolve_ByDisplayIdWithSuffix_ReturnsNumberedEndpoint()
    {
        var matches = ModelDisplayIds.Resolve(Models(), "deepseek/deepseek-chat #2");

        Assert.Equal("deepseek-chat-backup", Assert.Single(matches).Name);
    }

    [Fact]
    public void Resolve_ByBareUpstreamId_ReturnsAllProviders()
    {
        var matches = ModelDisplayIds.Resolve(Models(), "deepseek-chat");

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var matches = ModelDisplayIds.Resolve(Models(), "OpenAI/GPT-4O-2024-11-20");

        Assert.Equal("gpt-4o", Assert.Single(matches).Name);
    }

    [Fact]
    public void Resolve_RoutingName_TakesPrecedenceOverDisplayId()
    {
        var models = new List<ModelEndpointOptions>
        {
            // 路由名恰好等于另一端点的显示 ID 形态：按 Name 精确命中第一端点。
            new() { Name = "deepseek/deepseek-chat", Id = "other", BaseUrl = "https://api.deepseek.com/v1" },
            new() { Name = "mirror", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1" }
        };

        var matches = ModelDisplayIds.Resolve(models, "deepseek/deepseek-chat");

        Assert.Equal("deepseek/deepseek-chat", Assert.Single(matches).Name);
    }

    [Fact]
    public void Resolve_Unknown_ReturnsEmpty()
    {
        Assert.Empty(ModelDisplayIds.Resolve(Models(), "no-such-model"));
        Assert.Empty(ModelDisplayIds.Resolve(Models(), " "));
    }
}

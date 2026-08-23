using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 记录调用过的模型路由名（用于断言固定路由不串模型）。
/// 上游请求体 model 值的替换（路由名 Name → 真实模型 Id）发生在真实客户端内部，
/// 由 OpenAICompatibleModelClientTests 用 TestHandler 验证；此处 mock 只观察路由结果。
/// </summary>
internal sealed class AutoRoutingMockClient : IModelClient
{
    private readonly ConcurrentQueue<string> _calls;

    public AutoRoutingMockClient(ModelEndpointOptions endpoint, ConcurrentQueue<string> calls)
    {
        Endpoint = endpoint;
        _calls = calls;
    }

    public ModelEndpointOptions Endpoint { get; }

    public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(Endpoint.Name);
        return Task.FromResult(new RawChatResponse(
            $"{{\"model\":\"{Endpoint.Name}\",\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":\"ok\"}}}}]}}",
            null));
    }

    public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Stream not used in these tests.");

    public Task<Clients.ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<Clients.ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
        => Task.FromResult(new ModelHealthResult(true, 1));
}

internal sealed class AutoRoutingClientProvider : IModelClientProvider
{
    private readonly Dictionary<string, IModelClient> _clients;

    public AutoRoutingClientProvider(Dictionary<string, IModelClient> clients) => _clients = clients;

    public IModelClient GetClient(ModelEndpointOptions endpoint) =>
        _clients.TryGetValue(endpoint.Name, out var client)
            ? client
            : throw new InvalidOperationException($"No mock client for model '{endpoint.Name}'.");
}

internal sealed class AutoRoutingWebApplicationFactory : WebApplicationFactory<Program>
{
    public ConcurrentQueue<string> CalledModels { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OptiRouter:ProxyApiKey"] = "auto-test-key"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveBackgroundServices();
            services.Configure<RouterOptions>(options =>
            {
                options.Models.Clear();
                // 同供应商（deepseek）双端点不同 Key 提供同一真实模型 deepseek-chat，
                // 加另一家供应商（openai）提供 gpt-4o-2024-11-20。
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "deepseek-primary",
                    Id = "deepseek-chat",
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-key-1",
                    Tier = ModelTier.Medium,
                    MaxContextTokens = 64000,
                    Enabled = true
                });
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "deepseek-chat-backup",
                    Id = "deepseek-chat",
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-key-2",
                    Tier = ModelTier.Medium,
                    MaxContextTokens = 64000,
                    Enabled = true
                });
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "gpt-4o",
                    Id = "gpt-4o-2024-11-20",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "sk-openai",
                    Tier = ModelTier.Strong,
                    MaxContextTokens = 128000,
                    Enabled = true
                });
            });

            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IModelClientProvider));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IModelClientProvider>(sp =>
            {
                var models = sp.GetRequiredService<IOptions<RouterOptions>>().Value.Models;
                var clients = models.ToDictionary(
                    m => m.Name,
                    m => (IModelClient)new AutoRoutingMockClient(m, CalledModels));
                return new AutoRoutingClientProvider(clients);
            });
        });
    }
}

public class AutoModelRoutingEndpointTests
{
    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client, string modelJson)
    {
        var content = new StringContent(
            $$"""{"model":{{modelJson}},"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    private static HttpClient CreateClient(AutoRoutingWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "auto-test-key");
        return client;
    }

    [Fact]
    public async Task ModelsList_ShowsProviderSlashIdFormat_WithDuplicateNumbering()
    {
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal(4, data.GetArrayLength());

        var first = data[0];
        Assert.Equal("auto", first.GetProperty("id").GetString());
        Assert.Equal("auto", first.GetProperty("routing").GetString());

        // 真实模型 id 统一 {供应商}/{真实模型 Id}；同供应商同模型多 Key 追加序号。
        var ids = data.EnumerateArray()
            .Where(e => e.GetProperty("id").GetString() != "auto")
            .Select(e => e.GetProperty("id").GetString())
            .ToList();
        Assert.Equal(new[] { "deepseek/deepseek-chat", "deepseek/deepseek-chat #2", "openai/gpt-4o-2024-11-20" }, ids);

        var byId = data.EnumerateArray()
            .Where(e => e.GetProperty("id").GetString() != "auto")
            .ToDictionary(e => e.GetProperty("id").GetString()!);

        Assert.Equal("direct", byId["openai/gpt-4o-2024-11-20"].GetProperty("routing").GetString());
        // upstream_id 是发往供应商 API 的真实模型值；name 是配置路由名。
        Assert.Equal("gpt-4o-2024-11-20", byId["openai/gpt-4o-2024-11-20"].GetProperty("upstream_id").GetString());
        Assert.Equal("gpt-4o", byId["openai/gpt-4o-2024-11-20"].GetProperty("name").GetString());
        Assert.Equal("deepseek-chat", byId["deepseek/deepseek-chat"].GetProperty("upstream_id").GetString());
        Assert.Equal("deepseek-primary", byId["deepseek/deepseek-chat"].GetProperty("name").GetString());
    }

    [Fact]
    public async Task RequestWithAutoModel_RoutesToConfiguredModel()
    {
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"auto\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(factory.CalledModels);
        Assert.All(factory.CalledModels, name =>
            Assert.Contains(name, new[] { "deepseek-primary", "deepseek-chat-backup", "gpt-4o" }));
    }

    [Fact]
    public async Task RequestWithoutModel_RoutesToConfiguredModel()
    {
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "null");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(factory.CalledModels);
    }

    [Fact]
    public async Task RequestWithExplicitModel_PinsToThatModelOnly()
    {
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"deepseek-chat-backup\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var called = factory.CalledModels.ToList();
        Assert.Equal("deepseek-chat-backup", Assert.Single(called));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"model\":\"deepseek-chat-backup\"", body);
    }

    [Fact]
    public async Task RequestByProviderSlashIdFormat_PinsAndConvertsToInternalModelId()
    {
        // 客户端用 /v1/models 展示的 "{供应商}/{Id}" 格式请求：
        // 自动解析为提供该模型的端点集合，上游收到内部真实模型 Id。
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"openai/gpt-4o-2024-11-20\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gpt-4o", Assert.Single(factory.CalledModels.ToList()));
    }

    [Fact]
    public async Task RequestByProviderSlashIdWithoutSuffix_PinsToAllOfferingEndpoints()
    {
        // 无序号的 "deepseek/deepseek-chat" 同时指向同供应商双 Key 的两个端点。
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"deepseek/deepseek-chat\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var called = factory.CalledModels.ToList();
        Assert.Single(called);
        Assert.Contains(called[0], new[] { "deepseek-primary", "deepseek-chat-backup" });
    }

    [Fact]
    public async Task RequestByProviderSlashIdWithSuffix_PinsToNumberedEndpoint()
    {
        // "deepseek/deepseek-chat #2" 精确指向列表中第 2 个提供该模型的端点。
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"deepseek/deepseek-chat #2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("deepseek-chat-backup", Assert.Single(factory.CalledModels.ToList()));
    }

    [Fact]
    public async Task RequestWithUnknownModel_Returns404ModelNotFound()
    {
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"no-such-model\"");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("model_not_found", body);
        Assert.Empty(factory.CalledModels);
    }

    [Fact]
    public async Task RequestByUpstreamModelId_PinsToSoleEndpointOfferingIt()
    {
        // 客户端直接用真实模型 id 请求：唯一提供方是路由名 gpt-4o 的端点。
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"gpt-4o-2024-11-20\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gpt-4o", Assert.Single(factory.CalledModels.ToList()));
    }

    [Fact]
    public async Task RequestByUpstreamModelIdWithMultipleEndpoints_RoutesWithinOfferingEndpoints()
    {
        // deepseek-chat 由两个端点提供（同供应商双 Key）：按 id 请求时固定在这两者之内，
        // 不会路由到 gpt-4o。
        using var factory = new AutoRoutingWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client, "\"deepseek-chat\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var called = factory.CalledModels.ToList();
        Assert.Single(called);
        Assert.Contains(called[0], new[] { "deepseek-primary", "deepseek-chat-backup" });
    }
}

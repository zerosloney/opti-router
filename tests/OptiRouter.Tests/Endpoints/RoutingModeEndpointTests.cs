using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 可配置失败的 mock：指定路由名抛 ModelClientException（模拟上游 5xx），
/// 其余成功。用于端到端验证 pin 失败释放 → 同档级联闭环。
/// </summary>
internal sealed class FaultableMockClient : IModelClient
{
    private readonly ConcurrentQueue<string> _calls;
    private readonly HashSet<string> _failing;

    public FaultableMockClient(ModelEndpointOptions endpoint, HashSet<string> failing, ConcurrentQueue<string> calls)
    {
        Endpoint = endpoint;
        _failing = failing;
        _calls = calls;
    }

    public ModelEndpointOptions Endpoint { get; }

    public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(Endpoint.Name);
        if (_failing.Contains(Endpoint.Name))
        {
            throw new ModelClientException(
                System.Net.HttpStatusCode.BadGateway,
                responseBody: null,
                message: $"simulated failure of {Endpoint.Name}");
        }
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

/// <summary>
/// 三档各两模型的独立工厂：Strong×2 / Medium×1 / Cheap×1。
/// 与 AutoModelRoutingEndpointTests 的共享工厂隔离，不互相影响既有断言。
/// </summary>
internal sealed class RoutingModeWebApplicationFactory : WebApplicationFactory<Program>
{
    public ConcurrentQueue<string> CalledModels { get; } = new();
    public HashSet<string> FailingModels { get; } = new(StringComparer.Ordinal);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            services.RemoveBackgroundServices();
            services.UseFixedTenantKey("routing-mode-test-key");
            services.Configure<RouterOptions>(options =>
            {
                options.Models.Clear();
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "strong-a", Id = "m-strong-a", BaseUrl = "https://api.a.com/v1",
                    ApiKey = "k", Tier = ModelTier.Strong, MaxContextTokens = 128000, Enabled = true
                });
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "strong-b", Id = "m-strong-b", BaseUrl = "https://api.b.com/v1",
                    ApiKey = "k", Tier = ModelTier.Strong, MaxContextTokens = 128000, Enabled = true
                });
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "medium-a", Id = "m-medium-a", BaseUrl = "https://api.c.com/v1",
                    ApiKey = "k", Tier = ModelTier.Medium, MaxContextTokens = 64000, Enabled = true
                });
                options.Models.Add(new ModelEndpointOptions
                {
                    Name = "cheap-a", Id = "m-cheap-a", BaseUrl = "https://api.d.com/v1",
                    ApiKey = "k", Tier = ModelTier.Cheap, MaxContextTokens = 32000, Enabled = true
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
                    m => (IModelClient)new FaultableMockClient(m, FailingModels, CalledModels));
                return new AutoRoutingClientProvider(clients);
            });
        });
    }
}

public class RoutingModeEndpointTests
{
    private static async Task<HttpResponseMessage> PostModelAsync(HttpClient client, string modelJson)
    {
        var content = new StringContent(
            $$"""{"model":{{modelJson}},"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    private static HttpClient CreateClient(RoutingModeWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "routing-mode-test-key");
        return client;
    }

    [Fact]
    public async Task AutoCost_RoutesToCheapTier()
    {
        using var factory = new RoutingModeWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostModelAsync(client, "\"auto:cost\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("cheap-a", Assert.Single(factory.CalledModels.ToList()));
    }

    [Fact]
    public async Task AutoBalanced_RoutesToMediumTier()
    {
        using var factory = new RoutingModeWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostModelAsync(client, "\"auto:balanced\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("medium-a", Assert.Single(factory.CalledModels.ToList()));
    }

    [Fact]
    public async Task AutoIntel_SimpleRequestStillRoutesToStrongTier()
    {
        // 设计验证点：intel 模式对简单请求也优先质量档，不因请求简单而降档。
        using var factory = new RoutingModeWebApplicationFactory();
        var client = CreateClient(factory);

        var response = await PostModelAsync(client, "\"auto:intel\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = Assert.Single(factory.CalledModels.ToList());
        Assert.Contains(first, new[] { "strong-a", "strong-b" });
    }

    [Fact]
    public async Task AutoCost_TargetTierEmpty_FallsBackInsteadOf404()
    {
        // 兜底语义：目标档（Cheap）失败后不 503，跨档下沉到可用档位。
        using var factory = new RoutingModeWebApplicationFactory();
        factory.FailingModels.Add("cheap-a");
        var client = CreateClient(factory);

        var response = await PostModelAsync(client, "\"auto:cost\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "cheap-a", "medium-a" }, factory.CalledModels.ToList());
    }

    [Fact]
    public async Task PinnedStrongFails_CascadesToSameTierStrong()
    {
        // 核心闭环：pin strong-a → 上游 502 → 不直接报错，同档级联切 strong-b。
        using var factory = new RoutingModeWebApplicationFactory();
        factory.FailingModels.Add("strong-a");
        var client = CreateClient(factory);

        var response = await PostModelAsync(client, "\"strong-a\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "strong-a", "strong-b" }, factory.CalledModels.ToList());
    }

    [Fact]
    public async Task StrongTierAllFail_DegradesToMediumNotError()
    {
        // 设计验证点：Strong 档全部失败 → 跨档下沉 Medium，请求仍成功。
        using var factory = new RoutingModeWebApplicationFactory();
        factory.FailingModels.Add("strong-a");
        factory.FailingModels.Add("strong-b");
        var client = CreateClient(factory);

        var response = await PostModelAsync(client, "\"auto:intel\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var called = factory.CalledModels.ToList();
        Assert.Contains("strong-a", called);
        Assert.Contains("strong-b", called);
        Assert.Equal("medium-a", called.Last());
    }
}

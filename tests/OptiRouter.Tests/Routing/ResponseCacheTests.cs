using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;
using TestModelClient = OptiRouter.Tests.Endpoints.MockModelClient;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 响应缓存集成测试：相同非流式请求命中缓存（上游只调一次），不同请求/流式不缓存。
/// </summary>
public class ResponseCacheTests
{
    private sealed class CacheFactory : WebApplicationFactory<Program>
    {
        public const string Key = "cache-test-key";
        public Dictionary<string, IModelClient> MockClients { get; } = new();

        /// <summary>启用 regenerate 负反馈（默认关，保持既有缓存测试语义）。</summary>
        public bool EnableRegenerateFeedback { get; init; }

        /// <summary>regenerate 惩罚 reward（默认 0.1，与 RoutingOptions 一致）。</summary>
        public double RegeneratePenaltyReward { get; init; } = 0.1;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "m1", BaseUrl = "https://example.com", ApiKey = "k",
                        Tier = ModelTier.Medium, MaxContextTokens = 8192,
                        InputPricePerMillion = 1m, OutputPricePerMillion = 2m, Enabled = true
                    });
                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = true;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableFusionMode = false;
                    opt.Routing.EnableHealthProbe = false;
                    opt.Routing.EnableResponseCache = true;
                    opt.Routing.ResponseCacheTtlSeconds = 60;
                    opt.Routing.EnableRegenerateFeedback = EnableRegenerateFeedback;
                    opt.Routing.RegeneratePenaltyReward = RegeneratePenaltyReward;
                    opt.Routing.RegenerateFeedbackWindowSeconds = 60;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    private sealed class TestProvider : IModelClientProvider
    {
        private readonly Dictionary<string, IModelClient> _clients;
        public TestProvider(Dictionary<string, IModelClient> clients) => _clients = clients;
        public IModelClient GetClient(ModelEndpointOptions endpoint) => _clients[endpoint.Name];
    }

    private static RawChatResponse MakeResponse() => new(
        JsonSerializer.Serialize(new
        {
            id = "x",
            model = "m1",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "cached-answer" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 5, completion_tokens = 3, total_tokens = 8 }
        }),
        new ChatUsage { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 });

    private static ChatRequest BuildRequest(string content) => new()
    {
        Model = "auto",
        Messages = new List<ChatMessage> { ChatMessage.FromText("user", content) },
        Stream = false
    };

    [Fact]
    public void MemoryCache_SetGet_RoundTrip()
    {
        // 独立 MemoryCache（无 SizeLimit），useSize 默认 false。
        var mc = new MemoryCache(new MemoryCacheOptions());
        var cache = new MemoryResponseCache(mc, 10);
        var resp = MakeResponse();
        cache.Set("k", resp, TimeSpan.FromMinutes(1));
        Assert.True(cache.TryGet("k", out var got));
        Assert.NotNull(got);
        Assert.Same(resp, got);
    }

    [Fact]
    public async Task SameRequest_SecondCall_HitsCache_DoesNotCallUpstream()
    {
        using var factory = new CacheFactory();
        int calls = 0;
        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            (req, ct) => { calls++; return Task.FromResult(MakeResponse()); });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CacheFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest("hello"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var c1 = new StringContent(json, Encoding.UTF8, "application/json");
        using var c2 = new StringContent(json, Encoding.UTF8, "application/json");
        var r1 = await client.PostAsync("/v1/chat/completions", c1, cts.Token);
        var r2 = await client.PostAsync("/v1/chat/completions", c2, cts.Token);

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        var b1 = await r1.Content.ReadAsStringAsync(cts.Token);
        var b2 = await r2.Content.ReadAsStringAsync(cts.Token);
        Assert.Equal(b1, b2); // 命中缓存返回相同响应
        Assert.Equal(1, calls); // 上游只调一次，第二次命中缓存
    }

    [Fact]
    public async Task CacheHit_ConsumesRegenerateSignal_PenalizesPreviousModel()
    {
        // 回归：EnableResponseCache 与 EnableRegenerateFeedback 同开时，缓存命中路径必须消费
        // regenerate 信号并惩罚上次模型——否则用户重发同一请求拿到相同缓存答案，信号被缓存短路屏蔽。
        using var factory = new CacheFactory { EnableRegenerateFeedback = true };
        int calls = 0;
        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            (req, ct) => { calls++; return Task.FromResult(MakeResponse()); });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CacheFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest("hello"));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var c1 = new StringContent(json, Encoding.UTF8, "application/json");
        using var c2 = new StringContent(json, Encoding.UTF8, "application/json");
        var r1 = await client.PostAsync("/v1/chat/completions", c1, cts.Token);

        // 第一次请求（上游成功）已写入 regenerate 条目（success=true）并记录 Thompson 反馈。
        var tsStore = factory.Services.GetRequiredService<ThompsonStateStore>();
        double betaBeforeCacheHit = tsStore.GetOrAdd("m1").Beta;

        var r2 = await client.PostAsync("/v1/chat/completions", c2, cts.Token);

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(1, calls); // 第二次命中缓存，未调上游

        // 第二次（缓存命中）应消费 regenerate 条目并惩罚 m1：reward=0.1 → 无折扣增量 0.9。
        // beta = betaOld*0.95 + 0.9，增量 = 0.9 - 0.05*betaOld；betaOld ≤ 1.25（首次延迟成功 reward ≥ 0.7）
        // → 增量 ≥ 0.8375。若缓存命中不消费信号，第二次后 beta 不变，增量 = 0。
        double betaAfterCacheHit = tsStore.GetOrAdd("m1").Beta;
        double delta = betaAfterCacheHit - betaBeforeCacheHit;
        Assert.True(delta >= 0.8, $"缓存命中后 m1 Beta 增量应 ≥0.8（regenerate 惩罚），实际 {delta:F4}");
    }

    [Fact]
    public async Task DifferentRequest_DoesNotHitCache()
    {
        using var factory = new CacheFactory();
        int calls = 0;
        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            (req, ct) => { calls++; return Task.FromResult(MakeResponse()); });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CacheFactory.Key);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var c1 = new StringContent(JsonSerializer.Serialize(BuildRequest("hello")), Encoding.UTF8, "application/json");
        using var c2 = new StringContent(JsonSerializer.Serialize(BuildRequest("world")), Encoding.UTF8, "application/json");
        await client.PostAsync("/v1/chat/completions", c1, cts.Token);
        await client.PostAsync("/v1/chat/completions", c2, cts.Token);

        Assert.Equal(2, calls); // 不同请求各调一次上游，未命中
    }

    [Fact]
    public async Task StreamingRequest_NotCached()
    {
        using var factory = new CacheFactory();
        int calls = 0;
        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            streamRawFunc: (req, ct) => StreamOnce(() => calls++));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CacheFactory.Key);

        var req = new ChatRequest { Model = "auto", Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") }, Stream = true };
        var json = JsonSerializer.Serialize(req);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var c1 = new StringContent(json, Encoding.UTF8, "application/json");
        using var c2 = new StringContent(json, Encoding.UTF8, "application/json");
        var r1 = await client.PostAsync("/v1/chat/completions", c1, cts.Token);
        var r2 = await client.PostAsync("/v1/chat/completions", c2, cts.Token);
        await r1.Content.ReadAsStringAsync(cts.Token);
        await r2.Content.ReadAsStringAsync(cts.Token);

        Assert.Equal(2, calls); // 流式不缓存，各调一次
    }

    private static async IAsyncEnumerable<RawStreamLine> StreamOnce(Action onFirst, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        onFirst();
        yield return new RawStreamLine("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}", null, null);
        yield return new RawStreamLine("data: [DONE]", null, null);
        await Task.CompletedTask;
    }
}

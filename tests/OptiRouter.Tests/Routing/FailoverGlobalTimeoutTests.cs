using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;
using TestModelClient = OptiRouter.Tests.Endpoints.MockModelClient;

namespace OptiRouter.Tests.Routing;

public class FailoverGlobalTimeoutTests
{
    private sealed class TimeoutFactory : WebApplicationFactory<Program>
    {
        public const string Key = "timeout-test-key";
        public Dictionary<string, IModelClient> MockClients { get; } = new();

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
                        Name = "m1",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Cheap,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "m2",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Cheap,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });

                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = true;
                    opt.Routing.FailoverGlobalTimeoutSeconds = 1; // 1s 全局 Failover 超时
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableFusionMode = false;
                    opt.Routing.EnableHealthProbe = false;
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

    private static RouterOptions BuildValidOptions()
    {
        var options = TestHelpers.BuildOptions(("gpt-4o", ModelTier.Strong, 128000, 5m));
        foreach (var m in options.Models)
        {
            m.BaseUrl = "https://example.com";
        }
        return options;
    }

    [Fact]
    public void Validator_NegativeGlobalTimeout_Fails()
    {
        var validator = new RouterOptionsValidator();
        var options = BuildValidOptions();
        options.Routing.FailoverGlobalTimeoutSeconds = -1;

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Routing.FailoverGlobalTimeoutSeconds 不能为负数。", result.FailureMessage);
    }

    [Fact]
    public void Validator_ZeroOrPositiveGlobalTimeout_Succeeds()
    {
        var validator = new RouterOptionsValidator();
        var options = BuildValidOptions();

        options.Routing.FailoverGlobalTimeoutSeconds = 0;
        Assert.False(validator.Validate(null, options).Failed);

        options.Routing.FailoverGlobalTimeoutSeconds = 60;
        Assert.False(validator.Validate(null, options).Failed);
    }

    [Fact]
    public async Task SendAsync_GlobalFailoverTimeout_Returns503AllCandidatesFailed()
    {
        using var factory = new TimeoutFactory();

        // 两个模型均延迟 5 秒（远超 1 秒 Failover 全局超时）
        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            async (req, cancellationToken) =>
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                return new RawChatResponse("{}", new ChatUsage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }, null);
            });
        factory.MockClients["m2"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m2", BaseUrl = "https://example.com" },
            async (req, cancellationToken) =>
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                return new RawChatResponse("{}", new ChatUsage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }, null);
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TimeoutFactory.Key);

        var json = JsonSerializer.Serialize(new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") },
            Stream = false
        });
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("ALL_CANDIDATES_FAILED", error.GetProperty("code").GetString());
        Assert.Equal("all_candidates_failed", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task StreamAsync_GlobalFailoverTimeout_ReturnsSseErrorStream()
    {
        using var factory = new TimeoutFactory();

        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            streamRawFunc: (req, cancellationToken) => StreamDelay(5000, cancellationToken));
        factory.MockClients["m2"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m2", BaseUrl = "https://example.com" },
            streamRawFunc: (req, cancellationToken) => StreamDelay(5000, cancellationToken));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TimeoutFactory.Key);

        var json = JsonSerializer.Serialize(new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") },
            Stream = true
        });
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("ALL_CANDIDATES_FAILED", body);
    }

    /// <summary>
    /// TTFT fixture：两个 Cheap 候选，启用 Failover + 流式首字节专项超时（500ms），不依赖全局超时。
    /// </summary>
    private sealed class TtftFactory : WebApplicationFactory<Program>
    {
        public const string Key = "ttft-test-key";
        public Dictionary<string, IModelClient> MockClients { get; } = new();

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
                        Tier = ModelTier.Cheap, MaxContextTokens = 8192,
                        InputPricePerMillion = 1m, OutputPricePerMillion = 2m, Enabled = true
                    });
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "m2", BaseUrl = "https://example.com", ApiKey = "k",
                        Tier = ModelTier.Cheap, MaxContextTokens = 8192,
                        InputPricePerMillion = 1m, OutputPricePerMillion = 2m, Enabled = true
                    });

                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = true;
                    opt.Routing.FailoverGlobalTimeoutSeconds = 0; // 不依赖全局超时，仅 TTFT
                    opt.Routing.StreamFirstTokenTimeoutMs = 500; // 500ms 首字节超时
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableFusionMode = false;
                    opt.Routing.EnableHealthProbe = false;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    [Fact]
    public async Task StreamAsync_FirstTokenTimeout_FailsOverToFastCandidate()
    {
        using var factory = new TtftFactory();
        // m1 首字节延迟 3s（远超 500ms TTFT）→ TTFT 超时 → 记断路器失败 + failover 到 m2。
        factory.MockClients["m1"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m1", BaseUrl = "https://example.com" },
            streamRawFunc: (req, ct) => StreamDelay(3000, ct));
        // m2 首字节立即 yield 标记内容，应被采纳。
        factory.MockClients["m2"] = new TestModelClient(
            new ModelEndpointOptions { Name = "m2", BaseUrl = "https://example.com" },
            streamRawFunc: (req, ct) => StreamFromFast());

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TtftFactory.Key);

        var json = JsonSerializer.Serialize(new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") },
            Stream = true
        });
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("fast-m2", body); // failover 到 m2，其内容到达客户端
        Assert.DoesNotContain("ALL_CANDIDATES_FAILED", body);
    }

    private static async IAsyncEnumerable<RawStreamLine> StreamFromFast([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new RawStreamLine("data: {\"from\":\"fast-m2\"}", null, null);
        yield return new RawStreamLine("data: [DONE]", null, null);
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<RawStreamLine> StreamDelay(int delayMs, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        yield return new RawStreamLine("data: {}", null, null);
    }
}

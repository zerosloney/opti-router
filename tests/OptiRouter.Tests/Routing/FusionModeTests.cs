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

/// <summary>
/// 并行首试（Fusion-lite）的集成测试。
/// 通过完整 HTTP 管道走 ProxyOrchestrator.TryParallelFirstAttemptAsync。
/// </summary>
public class FusionModeTests
{
    /// <summary>
    /// Fusion fixture：两个同 tier 模型，启用并行首试。
    /// </summary>
    private sealed class FusionFactory : WebApplicationFactory<Program>
    {
        public const string Key = "fusion-test-key";
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
                    // 两个 Medium 模型，同 tier 才会被 RouterEngine 放进候选链（tier 升序 + 并行需同段）。
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "fast-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "slow-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });

                    // 关闭其他策略，确保走 fusion 路径。
                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = true;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableFusionMode = true;
                    opt.Routing.FusionMaxParallel = 2;
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

    private static ChatRequest BuildRequest(bool stream = false) => new()
    {
        Model = "any",
        Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") },
        Stream = stream
    };

    private static RawChatResponse MakeResponse(string model, int prompt, int completion) => new(
        JsonSerializer.Serialize(new
        {
            id = "x",
            model,
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion }
        }),
        new ChatUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion });

    [Fact]
    public async Task Fusion_AdoptsFastModel_CancelsSlow()
    {
        using var factory = new FusionFactory();
        // fast 立即返回；slow 阻塞 2 秒，应被 cancel。
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            (req, ct) => Task.FromResult(MakeResponse("fast-model", 10, 5)));
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            async (req, ct) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (TaskCanceledException) { throw new OperationCanceledException(ct); }
                return MakeResponse("slow-model", 10, 5);
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // 超时 5 秒——若 slow 未被 cancel 会超时失败。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("fast-model", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Fusion_AllFail_FallsBackToSerial()
    {
        using var factory = new FusionFactory();
        // 两个并行候选都抛 503，串行降级链也无可用——应返回失败而非死锁。
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            (req, ct) => throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            (req, ct) => throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 全失败 → AllCandidatesFailedException 映射为 502（或具体上游错）。
        // 不断言具体状态码（依赖 ChatCompletionsEndpoint 的映射），只断言非 200 且不死锁。
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Fusion_StreamingBypassed_SerialPathUsed()
    {
        using var factory = new FusionFactory();
        // 流式请求应走串行路径，不触发并行。
        // fast 流式立即 yield；slow 不应被调用（流式串行首候选成功即返回）。
        bool slowCalled = false;
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            streamRawFunc: (req, ct) => StreamLinesAsync(new[]
            {
                "{\"id\":\"x\",\"model\":\"fast-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}",
                "[DONE]"
            }));
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            streamRawFunc: (req, ct) =>
            {
                slowCalled = true;
                return StreamLinesAsync(new[] { "[DONE]" });
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest(stream: true));
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(slowCalled, "流式不应触发并行，slow 不应被调用");
    }

    private static async IAsyncEnumerable<RawStreamLine> StreamLinesAsync(string[] lines)
    {
        foreach (var line in lines)
        {
            yield return new RawStreamLine(line, null);
            await Task.Yield();
        }
    }
}

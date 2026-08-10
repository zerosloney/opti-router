using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 级联自校验（Cheap→Strong）成本入账的集成测试。
/// 验证修复：自校验复核调用的 token 成本必须进入 CostLedger，不能悄悄漂移。
/// 通过完整 HTTP 管道走 ProxyOrchestrator.TryCascadeUpgradeAsync。
/// </summary>
public class CascadeCostAccountingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class CascadeFactory : WebApplicationFactory<Program>
    {
        public const string Key = "cascade-test-key";
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
                    // Cheap 首选（被 RuleClassifier 命中 simple-qa），Strong 作为级联升级目标。
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "cheap-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Cheap,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 0.1m,
                        OutputPricePerMillion = 0.2m,
                        Enabled = true
                    });
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "strong-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Strong,
                        MaxContextTokens = 128000,
                        InputPricePerMillion = 5m,
                        OutputPricePerMillion = 10m,
                        Enabled = true
                    });

                    opt.Routing.EnableRuleClassifier = true;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = false;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableCascadeUpgrade = true;
                    opt.Routing.CascadeUpgradeSampleRate = 1.0;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    /// <summary>
    /// 只有 Cheap 模型、无 Strong 可升级的 fixture。用于验证 upgradeTarget is null 兜底。
    /// </summary>
    private sealed class NoStrongFactory : WebApplicationFactory<Program>
    {
        public const string Key = "no-strong-test-key";
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
                        Name = "cheap-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Cheap,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 0.1m,
                        OutputPricePerMillion = 0.2m,
                        Enabled = true
                    });
                    // 无 Strong 模型。

                    opt.Routing.EnableRuleClassifier = true;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = false;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableCascadeUpgrade = true;
                    opt.Routing.CascadeUpgradeSampleRate = 1.0;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    /// <summary>
    /// 上游不返回 usage 的 fixture（cascade 关闭）。用于验证成功请求在 null-usage 下
    /// 按估算 input 成本入账并标 IsEstimated=true，而非记 0 成本。
    /// </summary>
    private sealed class NullUsageFactory : WebApplicationFactory<Program>
    {
        public const string Key = "null-usage-test-key";
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
                        Name = "cheap-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Cheap,
                        MaxContextTokens = 128000,
                        InputPricePerMillion = 0.1m,
                        OutputPricePerMillion = 0.2m,
                        Enabled = true
                    });
                    // 单模型：避免 failover/降级路径干扰成本断言。

                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = false;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableCascadeUpgrade = false;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    private sealed class TestProvider : IModelClientProvider
    {
        private readonly Dictionary<string, IModelClient> _clients;
        public TestProvider(Dictionary<string, IModelClient> clients) => _clients = clients;
        public IModelClient GetClient(ModelEndpointOptions endpoint)
        {
            if (_clients.TryGetValue(endpoint.Name, out var c)) return c;
            throw new KeyNotFoundException(endpoint.Name);
        }
    }

    private sealed class MockClient : IModelClient
    {
        private readonly ModelEndpointOptions _ep;
        private readonly Func<ChatRequest, CancellationToken, Task<RawChatResponse>> _raw;
        private readonly Func<ChatRequest, CancellationToken, Task<Clients.ChatResponse>>? _complete;
        public MockClient(ModelEndpointOptions ep,
            Func<ChatRequest, CancellationToken, Task<RawChatResponse>> raw,
            Func<ChatRequest, CancellationToken, Task<Clients.ChatResponse>>? complete = null)
        { _ep = ep; _raw = raw; _complete = complete; }
        public ModelEndpointOptions Endpoint => _ep;
        public Task<RawChatResponse> CompleteRawAsync(ChatRequest r, CancellationToken c = default) => _raw(r, c);
        public Task<Clients.ChatResponse> CompleteAsync(ChatRequest r, CancellationToken c = default)
        {
            if (_complete is null) throw new NotImplementedException();
            return _complete(r, c);
        }
        public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest r, CancellationToken c = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<Clients.ChatStreamChunk> StreamAsync(ChatRequest r, CancellationToken c = default)
            => throw new NotImplementedException();
        public Task<ModelHealthResult> ProbeAsync(CancellationToken c = default)
            => Task.FromResult(new ModelHealthResult(true, 0));
    }

    private static RawChatResponse Raw(string content, int prompt, int completion)
        => new(
            $"{{\"id\":\"1\",\"model\":\"m\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"{content}\"}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":{prompt},\"completion_tokens\":{completion},\"total_tokens\":{prompt + completion}}}}}",
            new ChatUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion });

    private static Clients.ChatResponse Parsed(string content, int prompt, int completion)
        => new()
        {
            Id = "1",
            Model = "m",
            Choices = new List<Clients.ChatChoice>
            {
                new() { Index = 0, Message = ChatMessage.FromText("assistant", content), FinishReason = "stop" }
            },
            Usage = new ChatUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion }
        };

    /// <summary>
    /// 开级联 + 采样率 1.0 + 自校验回 UNCERTAIN：真实升级到 Strong，三笔成本都入账。
    /// 验证修复：升级目标从全量启用模型选，不依赖被 RuleClassifier 砍成单 Cheap tier 的候选链。
    /// </summary>
    [Fact]
    public async Task Cascade_Uncertain_Upgrade_Accounts_All_Three_Calls()
    {
        using var factory = new CascadeFactory();
        var cheapEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "cheap-model");
        var strongEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "strong-model");

        factory.MockClients["cheap-model"] = new MockClient(cheapEp,
            raw: (r, c) => Task.FromResult(Raw("cheap answer", 10, 5)),
            complete: (r, c) => Task.FromResult(Parsed("UNCERTAIN", 20, 1)));
        // Strong 会被调用（升级目标从全量模型选，绕过候选链的单 tier 过滤）。
        factory.MockClients["strong-model"] = new MockClient(strongEp,
            raw: (r, c) => Task.FromResult(Raw("strong corrected answer", 12, 8)));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CascadeFactory.Key);

        var req = new ChatRequest
        {
            Model = "any",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") }, // 单条短消息 → RuleClassifier simple-qa → Cheap
            Stream = false
        };
        using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 响应应是 Strong 的答案，不是 Cheap 的。
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("strong corrected answer",
            doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        decimal daily = ledger.GetDailySpend();

        // Cheap 主答: (10*0.1 + 5*0.2)/1e6
        // 自校验:    (20*0.1 + 1*0.2)/1e6
        // Strong 重答: (12*5 + 8*10)/1e6
        decimal cheapCost = (10m * 0.1m + 5m * 0.2m) / 1_000_000m;
        decimal verifyCost = (20m * 0.1m + 1m * 0.2m) / 1_000_000m;
        decimal strongCost = (12m * 5m + 8m * 10m) / 1_000_000m;

        Assert.True(daily > cheapCost + verifyCost,
            $"Daily spend {daily} must include strong upgrade cost. Expected {cheapCost + verifyCost + strongCost}.");
        Assert.Equal(cheapCost + verifyCost + strongCost, daily);
    }

    /// <summary>
    /// 自校验 UNCERTAIN 但无可用 Strong（全禁用）→ 不升级，返回原 Cheap 答案，不抛。
    /// 验证 upgradeTarget is null 的兜底路径。
    /// </summary>
    [Fact]
    public async Task Cascade_Uncertain_No_Strong_Available_Returns_Cheap_Answer()
    {
        using var factory = new NoStrongFactory();
        var cheapEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "cheap-model");

        factory.MockClients["cheap-model"] = new MockClient(cheapEp,
            raw: (r, c) => Task.FromResult(Raw("cheap answer", 10, 5)),
            complete: (r, c) => Task.FromResult(Parsed("UNCERTAIN", 20, 1)));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NoStrongFactory.Key);

        var req = new ChatRequest
        {
            Model = "any",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
            Stream = false
        };
        using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 无 Strong 可升级 → 返回原 Cheap 答案。
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("cheap answer",
            doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        // 仍记 Cheap 主答 + 自校验两笔（升级未发生）。
        var ledger = factory.Services.GetRequiredService<CostLedger>();
        decimal daily = ledger.GetDailySpend();
        decimal cheapCost = (10m * 0.1m + 5m * 0.2m) / 1_000_000m;
        decimal verifyCost = (20m * 0.1m + 1m * 0.2m) / 1_000_000m;
        Assert.Equal(cheapCost + verifyCost, daily);
    }

    /// <summary>
    /// 自校验回 CONFIDENT：不升级，Cheap 主答 + 自校验两笔入账。
    /// </summary>
    [Fact]
    public async Task Cascade_Confident_Accounts_Cheap_And_Verify()
    {
        using var factory = new CascadeFactory();
        var cheapEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "cheap-model");
        var strongEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "strong-model");

        factory.MockClients["cheap-model"] = new MockClient(cheapEp,
            raw: (r, c) => Task.FromResult(Raw("cheap good answer", 10, 5)),
            complete: (r, c) => Task.FromResult(Parsed("CONFIDENT", 15, 1)));
        // Strong 不应被调用。
        factory.MockClients["strong-model"] = new MockClient(strongEp,
            raw: (r, c) => throw new InvalidOperationException("Strong should not be called when confident."));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CascadeFactory.Key);

        var req = new ChatRequest
        {
            Model = "any",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
            Stream = false
        };
        using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        decimal daily = ledger.GetDailySpend();

        decimal cheapCost = (10m * 0.1m + 5m * 0.2m) / 1_000_000m;
        decimal verifyCost = (15m * 0.1m + 1m * 0.2m) / 1_000_000m;

        Assert.True(daily > cheapCost,
            $"Daily spend {daily} must include verify cost beyond cheap. Expected {cheapCost + verifyCost}.");
        Assert.Equal(cheapCost + verifyCost, daily);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task Cascade_VerificationFailure_Only5xxUpdatesHealthAndThompson(
        HttpStatusCode statusCode,
        bool expectHealthFailure)
    {
        using var factory = new CascadeFactory();
        var cheapEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "cheap-model");
        var reset = DateTimeOffset.UtcNow.AddMinutes(1);
        factory.MockClients["cheap-model"] = new MockClient(
            cheapEp,
            raw: (_, _) => Task.FromResult(Raw("cheap answer", 10, 5)),
            complete: (_, _) => throw new ModelClientException(statusCode, "secret", metadata:
                statusCode == HttpStatusCode.TooManyRequests
                    ? new UpstreamResponseMetadata { RequestsRemaining = 0, RequestsResetAt = reset }
                    : null));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CascadeFactory.Key);
        var request = new ChatRequest
        {
            Model = "any",
            Messages = [ChatMessage.FromText("user", "Hi")]
        };
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        bool hasFailure = health.GetCircuitsSnapshot().TryGetValue("cheap-model", out var circuit)
            && circuit.FailureCount > 0;
        Assert.Equal(expectHealthFailure, hasFailure);
        double beta = factory.Services.GetRequiredService<ThompsonStateStore>()
            .GetOrAdd("cheap-model").Beta;
        Assert.Equal(expectHealthFailure, beta > 1.0);
        if (!expectHealthFailure)
        {
            Assert.True(factory.Services.GetRequiredService<UpstreamQuotaStateStore>()
                .GetSnapshot("cheap-model")!.IsExhausted(DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// 上游成功但未返回 usage：成本按估算 input 入账并标 IsEstimated=true，
    /// 而非记 0 成本（避免成功请求导致日/会话预算低估）。
    /// </summary>
    [Fact]
    public async Task NullUsage_Success_Records_Estimated_Cost()
    {
        using var factory = new NullUsageFactory();
        var cheapEp = factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue
            .Models.First(m => m.Name == "cheap-model");

        // 成功响应但 usage = null（部分兼容上游省略 usage）。
        factory.MockClients["cheap-model"] = new MockClient(cheapEp,
            raw: (r, c) => Task.FromResult(new RawChatResponse(
                """{"id":"1","model":"cheap-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""",
                null)));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", NullUsageFactory.Key);

        var req = new ChatRequest
        {
            Model = "any",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
            Stream = false
        };
        using var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 账本计入估算 input 成本（非 0）。"Hi" → 4 tokens × 0.1/1e6。
        decimal estCost = 4m * 0.1m / 1_000_000m;
        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetDailySpend() > 0m,
            $"Null-usage success must record estimated cost, got {ledger.GetDailySpend()}");

        // 审计记录标 IsEstimated=true（区别于真实成本）。
        var audit = factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(10)
            .Single(r => r.Model == "cheap-model");
        Assert.True(audit.IsEstimated);
        Assert.True(audit.Success);
        Assert.Equal(estCost, audit.Cost);
    }
}

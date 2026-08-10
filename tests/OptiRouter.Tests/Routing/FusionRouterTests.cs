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
/// 融合路由（OpenRouter Fusion 式 quality router）集成测试。
/// 通过完整 HTTP 管道走 ProxyOrchestrator.TryFusionRouterAsync。
/// </summary>
public class FusionRouterTests
{
    /// <summary>
    /// Fusion router fixture：三个同 tier 模型，启用融合路由。
    /// </summary>
    private sealed class FusionRouterFactory : WebApplicationFactory<Program>
    {
        public const string Key = "fusion-router-test-key";
        public Dictionary<string, IModelClient> MockClients { get; } = new();
        public bool EnableFusionMode { get; set; }
        public double FusionRouterTemperature { get; set; }
        public double? FusionRouterPanelTemperature { get; set; }
        public RequestComplexity FusionRouterMinComplexity { get; set; } = RequestComplexity.Unknown;
        public bool EnableRuleClassifierInFactory { get; set; }
        public int FusionRouterPanelSize { get; set; } = 3;
        public int FusionRouterPanelTimeoutSeconds { get; set; } = 0;
        public string? FusionRouterAnalystPrompt { get; set; }
        public string? CascadeUpgradeSelfVerifyPrompt { get; set; }

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
                    // 三个同 tier 模型，RouterEngine 按 MaxContextTokens 降序排。
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "model-a",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 16384,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "model-b",
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
                        Name = "model-c",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 4096,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });

                    // 关闭其他策略，确保走 fusion router 路径。
                    opt.Routing.EnableRuleClassifier = EnableRuleClassifierInFactory;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = true;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableFusionMode = EnableFusionMode;
                    opt.Routing.EnableFusionRouter = true;
                    opt.Routing.FusionRouterPanelSize = FusionRouterPanelSize;
                    opt.Routing.FusionRouterPanelTimeoutSeconds = FusionRouterPanelTimeoutSeconds;
                    opt.Routing.FusionRouterTemperature = FusionRouterTemperature;
                    opt.Routing.FusionRouterPanelTemperature = FusionRouterPanelTemperature;
                    opt.Routing.FusionRouterMinComplexity = FusionRouterMinComplexity;
                    opt.Routing.FusionRouterAnalystPrompt = FusionRouterAnalystPrompt;
                    if (CascadeUpgradeSelfVerifyPrompt is not null)
                        opt.Routing.CascadeUpgradeSelfVerifyPrompt = CascadeUpgradeSelfVerifyPrompt;
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

    private static RawChatResponse MakeResponse(string model, int prompt, int completion, string content = "ok")
    {
        return new RawChatResponse(
            JsonSerializer.Serialize(new
            {
                id = "x",
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
                usage = new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion }
            }),
            new ChatUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion });
    }

    /// <summary>
    /// 构造 analyst 响应（Body 内嵌结构化 JSON）。
    /// </summary>
    private static RawChatResponse MakeAnalystResponse(string model, int prompt, int completion, string analysisJson)
    {
        return new RawChatResponse(
            JsonSerializer.Serialize(new
            {
                id = "x",
                model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = analysisJson }, finish_reason = "stop" } },
                usage = new { prompt_tokens = prompt, completion_tokens = completion, total_tokens = prompt + completion }
            }),
            new ChatUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion });
    }

    /// <summary>
    /// 标准 analyst JSON 分析内容。
    /// </summary>
    private const string AnalystJson =
        "{\"consensus\":\"多数模型同意基本结论\",\"contradictions\":\"模型 B 与 C 对细节有分歧\",\"gaps\":\"未覆盖性能基准数据\",\"unique_insights\":\"模型 A 提供了独特视角\",\"recommendation\":\"综合各模型优点给出最终答案\"}";

    [Fact]
    public async Task FusionRouter_WhenRaceAlsoEnabled_RunsQualityRouterFirst()
    {
        using var factory = new FusionRouterFactory { EnableFusionMode = true };
        int modelACalls = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => Task.FromResult(++modelACalls switch
            {
                1 => MakeResponse("model-a", 10, 5, "panel-a"),
                2 => MakeAnalystResponse("model-a", 10, 5, AnalystJson),
                _ => MakeResponse("model-a", 10, 5, "quality-final")
            }));
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b")));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
        using var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, modelACalls);
        Assert.Contains("quality-final", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 0.7)]
    [InlineData(1.25, 1.25)]
    public async Task FusionRouter_PanelTemperature_UsesDefaultOnlyWhenRequestOmitsValue(
        double? requestTemperature,
        double expectedTemperature)
    {
        using var factory = new FusionRouterFactory { FusionRouterTemperature = 0.7 };
        double? capturedPanelTemperature = null;
        int modelACalls = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => Task.FromResult(++modelACalls switch
            {
                1 => MakeResponse("model-a", 10, 5, "panel-a"),
                2 => MakeAnalystResponse("model-a", 10, 5, AnalystJson),
                _ => MakeResponse("model-a", 10, 5, "final")
            }));
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) =>
            {
                capturedPanelTemperature = req.Temperature;
                return Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b"));
            });
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
        var request = BuildRequest() with { Temperature = requestTemperature };
        using var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedTemperature, capturedPanelTemperature);
    }

    [Fact]
    public void BuildOuterRequest_PreservesOpenAiExtensionData()
    {
        var request = BuildRequest() with
        {
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["tools"] = JsonSerializer.SerializeToElement(new[] { new { type = "function" } }),
                ["tool_choice"] = JsonSerializer.SerializeToElement("auto"),
                ["response_format"] = JsonSerializer.SerializeToElement(new { type = "json_object" }),
                ["top_p"] = JsonSerializer.SerializeToElement(0.9)
            }
        };

        var outer = FusionSynthesis.BuildOuterRequest(
            request, new FusionAnalysis { Consensus = "ok" }, FusionSynthesis.DefaultOuterPrompt, 1000);

        Assert.NotSame(request.ExtensionData, outer.ExtensionData);
        Assert.Equal(request.ExtensionData!.Keys.Order(), outer.ExtensionData!.Keys.Order());
        Assert.Equal("auto", outer.ExtensionData["tool_choice"].GetString());
        Assert.Equal("json_object", outer.ExtensionData["response_format"].GetProperty("type").GetString());
        Assert.Equal(0.9, outer.ExtensionData["top_p"].GetDouble());
    }

    [Fact]
    public async Task FusionRouter_ExternalCancellation_ReleasesProbesWithoutPenalties()
    {
        using var factory = new FusionRouterFactory();
        foreach (string name in new[] { "model-a", "model-b", "model-c" })
        {
            factory.MockClients[name] = new TestModelClient(
                new ModelEndpointOptions { Name = name },
                async (req, ct) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("unreachable");
                });
        }

        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        var thompson = factory.Services.GetRequiredService<ThompsonStateStore>();
        foreach (string name in factory.MockClients.Keys)
        {
            health.RecordFailure(name, threshold: 1, cooldownSeconds: 0);
            Assert.Equal(CircuitState.HalfOpen, health.GetState(name));
            _ = thompson.GetOrAdd(name);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.SendAsync(BuildRequest(), cts.Token));

        var circuits = health.GetCircuitsSnapshot();
        foreach (string name in factory.MockClients.Keys)
        {
            Assert.Equal(CircuitState.HalfOpen, circuits[name].State);
            Assert.Equal(0, circuits[name].FailureCount);
            Assert.Equal(0, circuits[name].ActiveProbes);
            var stats = thompson.GetOrAdd(name);
            Assert.Equal(1.0, stats.Alpha);
            Assert.Equal(1.0, stats.Beta);
        }
    }

    [Fact]
    public async Task FusionRouter_UsesDedicatedAnalystPrompt_NotCascadePrompt()
    {
        const string dedicatedPrompt = "DEDICATED_FUSION_JSON_PROMPT";
        using var factory = new FusionRouterFactory
        {
            FusionRouterAnalystPrompt = dedicatedPrompt,
            CascadeUpgradeSelfVerifyPrompt = "只输出 CONFIDENT 或 UNCERTAIN"
        };
        ChatRequest? analystRequest = null;
        int modelACalls = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) =>
            {
                modelACalls++;
                if (modelACalls == 2)
                {
                    analystRequest = req;
                    return Task.FromResult(MakeAnalystResponse("model-a", 10, 5, AnalystJson));
                }
                return Task.FromResult(MakeResponse("model-a", 10, 5, modelACalls == 1 ? "panel-a" : "final"));
            });
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b")));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
        using var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(analystRequest);
        Assert.Contains(dedicatedPrompt, analystRequest.Messages[^1].GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIDENT", analystRequest.Messages[^1].GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FusionRouter_FullFlow_PanelAnalystOuter()
    {
        // 验证：panel 并行收集 → analyst 分析 → outer 写最终答案，全部调用了正确的模型。
        using var factory = new FusionRouterFactory();

        // model-a: panel 调用 + analyst 调用 + outer 调用（默认 primary = model-a）
        int callCountA = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) =>
            {
                callCountA++;
                if (callCountA == 1) return Task.FromResult(MakeResponse("model-a", 10, 5, "panel-A-answer"));
                if (callCountA == 2) return Task.FromResult(MakeAnalystResponse("model-a", 50, 30, AnalystJson));
                return Task.FromResult(MakeResponse("model-a", 100, 20, "final-answer"));
            });
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-B-answer")));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-C-answer")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        // outer 返回的模型是 model-a
        Assert.Equal("model-a", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("final-answer", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        // model-a 被调用了 3 次（panel + analyst + outer）
        Assert.Equal(3, callCountA);
    }

    [Fact]
    public async Task FusionRouter_OccupiedHalfOpenCandidate_ScansToFillPanel()
    {
        using var factory = new FusionRouterFactory { FusionRouterPanelSize = 2 };
        int callsA = 0, callsB = 0, callsC = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) =>
            {
                callsA++;
                return Task.FromResult(callsA switch
                {
                    1 => MakeResponse("model-a", 10, 5, "panel-A-answer"),
                    2 => MakeAnalystResponse("model-a", 50, 30, AnalystJson),
                    _ => MakeResponse("model-a", 100, 20, "final-answer")
                });
            });
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) =>
            {
                callsB++;
                return Task.FromResult(MakeResponse("model-b", 10, 5));
            });
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) =>
            {
                callsC++;
                return Task.FromResult(MakeResponse("model-c", 10, 5, "panel-C-answer"));
            });

        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        health.RecordFailure("model-b", threshold: 1, cooldownSeconds: 0);
        Assert.True(health.TryBeginProbe("model-b", 1));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
        using var content = new StringContent(
            JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, callsA);
        Assert.Equal(0, callsB);
        Assert.Equal(1, callsC);
        health.ReleaseProbe("model-b");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task FusionRouter_PanelFailure_Only5xxUpdatesHealthAndThompson(
        HttpStatusCode statusCode,
        bool expectHealthFailure)
    {
        using var factory = new FusionRouterFactory();
        int callsA = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (_, _) => Task.FromResult(++callsA switch
            {
                1 => MakeResponse("model-a", 10, 5, "panel-a"),
                2 => MakeAnalystResponse("model-a", 10, 5, AnalystJson),
                _ => MakeResponse("model-a", 10, 5, "final")
            }));
        var reset = DateTimeOffset.UtcNow.AddMinutes(1);
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (_, _) => throw new ModelClientException(statusCode, "secret", metadata:
                statusCode == HttpStatusCode.TooManyRequests
                    ? new UpstreamResponseMetadata { RequestsRemaining = 0, RequestsResetAt = reset }
                    : null));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (_, _) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
        using var content = new StringContent(
            JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        bool hasFailure = health.GetCircuitsSnapshot().TryGetValue("model-b", out var circuit)
            && circuit.FailureCount > 0;
        Assert.Equal(expectHealthFailure, hasFailure);
        Assert.Equal(expectHealthFailure,
            factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-b").Beta > 1.0);
        if (!expectHealthFailure)
        {
            Assert.True(factory.Services.GetRequiredService<UpstreamQuotaStateStore>()
                .GetSnapshot("model-b")!.IsExhausted(DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public async Task FusionRouter_CostAccounting()
    {
        // 验证：N panel + analyst + outer 的全部真实成本都入账。
        using var factory = new FusionRouterFactory();

        int callCountA = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) =>
            {
                callCountA++;
                if (callCountA == 1) return Task.FromResult(MakeResponse("model-a", 10, 5, "panel-A-answer"));
                if (callCountA == 2) return Task.FromResult(MakeAnalystResponse("model-a", 50, 30, AnalystJson));
                return Task.FromResult(MakeResponse("model-a", 100, 20, "final-answer"));
            });
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-B-answer")));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-C-answer")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await Task.Delay(300); // 等审计落库

        // 手动计算预期成本：3 panel + 1 analyst + 1 outer
        // panel: 每个 10*1 + 5*2 = 20/1e6 = 0.00002
        // analyst: 50*1 + 30*2 = 110/1e6 = 0.00011
        // outer: 100*1 + 20*2 = 140/1e6 = 0.00014
        // 总计: 3*0.00002 + 0.00011 + 0.00014 = 0.00031
        decimal expectedPanelCost = 20m / 1_000_000m;
        decimal expectedAnalystCost = 110m / 1_000_000m;
        decimal expectedOuterCost = 140m / 1_000_000m;
        decimal expectedTotalCost = 3 * expectedPanelCost + expectedAnalystCost + expectedOuterCost;

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        var (daily, total) = ledger.GetSpend();
        Assert.Equal(expectedTotalCost, total);
        Assert.Equal(expectedTotalCost, daily);
    }

    [Fact]
    public async Task FusionRouter_AuditRecords_FusionRole()
    {
        // 验证：审计记录有正确的 FusionRole 值（panel/analyst/outer）。
        using var factory = new FusionRouterFactory();

        int callCountA = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) =>
            {
                callCountA++;
                if (callCountA == 1) return Task.FromResult(MakeResponse("model-a", 10, 5, "panel-A-answer"));
                if (callCountA == 2) return Task.FromResult(MakeAnalystResponse("model-a", 50, 30, AnalystJson));
                return Task.FromResult(MakeResponse("model-a", 100, 20, "final-answer"));
            });
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-B-answer")));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-C-answer")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PostAsync("/v1/chat/completions", content, cts.Token);
        await Task.Delay(300);

        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        var recent = auditStore.GetRecent(10);

        // 应有 5 条记录：3 panel + 1 analyst + 1 outer
        var fusionRecords = recent.Where(r => !string.IsNullOrEmpty(r.FusionRole)).ToList();
        Assert.Equal(5, fusionRecords.Count);

        var panelRecords = fusionRecords.Where(r => r.FusionRole == "panel").ToList();
        var analystRecords = fusionRecords.Where(r => r.FusionRole == "analyst").ToList();
        var outerRecords = fusionRecords.Where(r => r.FusionRole == "outer").ToList();

        Assert.Equal(3, panelRecords.Count);
        Assert.Single(analystRecords);
        Assert.Single(outerRecords);

        // 所有同组 ParallelGroupId 一致
        var groupIds = fusionRecords.Select(r => r.ParallelGroupId).Distinct();
        Assert.Single(groupIds);
        Assert.NotNull(groupIds.First());

        // 仅 outer 被采纳
        Assert.All(panelRecords, r => Assert.False(r.IsAdopted));
        Assert.False(analystRecords[0].IsAdopted);
        Assert.True(outerRecords[0].IsAdopted);

        // 所有 panel 和 analyst 成功
        Assert.All(panelRecords, r => Assert.True(r.Success));
        Assert.True(analystRecords[0].Success);
        Assert.True(outerRecords[0].Success);
    }

    [Fact]
    public async Task FusionRouter_AllPanelFail_FallsBackToSerial()
    {
        // 所有 panel 模型失败 → 回退串行。
        using var factory = new FusionRouterFactory();

        // 所有 panel 模型抛 503
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 全部失败 → 非 200（不该死锁）
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FusionRouter_AnalystFail_FallsBackToSerial()
    {
        // panel 成功但 analyst 失败 → 回退串行。串行路径也全部失败 → 最终返回非 200。
        using var factory = new FusionRouterFactory();

        // panel 成功，但模型在 analyst 之后不再可用（串行路径也失败）
        bool panelAcallDone = false, panelBcallDone = false, panelCcallDone = false;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) =>
            {
                // 第一次调用 = panel，成功
                if (!panelAcallDone) { panelAcallDone = true; return Task.FromResult(MakeResponse("model-a", 10, 5, "panel-A-answer")); }
                // 第二次及以后 = analyst 或串行 → 失败
                throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down");
            });
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) =>
            {
                if (!panelBcallDone) { panelBcallDone = true; return Task.FromResult(MakeResponse("model-b", 10, 5, "panel-B-answer")); }
                throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down");
            });
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) =>
            {
                if (!panelCcallDone) { panelCcallDone = true; return Task.FromResult(MakeResponse("model-c", 10, 5, "panel-C-answer")); }
                throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down");
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 回退到串行后全部失败 → 非 200
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FusionRouter_StreamingBypassed()
    {
        // 流式请求应绕过融合路由，走串行路径。
        using var factory = new FusionRouterFactory();

        bool outerCalled = false;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            streamRawFunc: (req, ct) => StreamLinesAsync(new[]
            {
                "{\"id\":\"x\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}",
                "[DONE]"
            }));
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            streamRawFunc: (req, ct) =>
            {
                outerCalled = true;
                return StreamLinesAsync(new[] { "[DONE]" });
            });
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            streamRawFunc: (req, ct) =>
            {
                outerCalled = true;
                return StreamLinesAsync(new[] { "[DONE]" });
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest(stream: true));
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(outerCalled, "流式不应触发融合路由");
    }

    [Fact]
    public async Task FusionRouter_PanelLessThan2_FallsBackToSerial()
    {
        // 候选链不足 2 个 panel 候选（<2），回退串行。
        // 通过设置 FusionRouterPanelSize=2 但只留 1 个模型（或让 1 个模型被熔断）来触发。
        // 这里用最简单的方法：只配置 1 个模型。
        using var factory = new FusionRouterFactory();

        // 覆盖模型列表：只留 1 个
        factory.MockClients.Clear();
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => Task.FromResult(MakeResponse("model-a", 10, 5, "only-answer")));

        // 但由于 factory 的 ConfigureServices 已设置模型列表，需在测试中重新配置。
        // 不如直接修改配置——但 factory 已建好。用另一个方式：让 model-b,model-c 被熔断打开。
        // 再建一个特殊工厂。
        using var factory2 = new SingleModelFusionRouterFactory();
        factory2.MockClientsDictionary["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => Task.FromResult(MakeResponse("model-a", 10, 5, "only-answer")));

        using var client = factory2.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 串行路径应成功（1 个模型可用）
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("model-a", doc.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// 单模型工厂：只有 1 个模型，触发 panel < 2 回退路径。
    /// </summary>
    private sealed class SingleModelFusionRouterFactory : WebApplicationFactory<Program>
    {
        public Dictionary<string, IModelClient> MockClientsDictionary { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", FusionRouterFactory.Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "model-a",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 16384,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    });

                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = true;
                    opt.Routing.EnableSemanticRouter = false;
                    opt.Routing.EnableSessionAffinity = false;
                    opt.Routing.EnableLoadBalance = false;
                    opt.Routing.EnableFusionMode = false;
                    opt.Routing.EnableFusionRouter = true;
                    opt.Routing.FusionRouterPanelSize = 3;
                    opt.Routing.EnableHealthProbe = false;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClientsDictionary));
            });
        }
    }

    private static async IAsyncEnumerable<RawStreamLine> StreamLinesAsync(string[] lines)
    {
        foreach (var line in lines)
        {
            yield return new RawStreamLine(line, null);
            await Task.Yield();
        }
    }

    /// <summary>
    /// 配置 panel 超时 + 1 个慢 panel：慢 panel 在超时前未完成，应被记失败并释放，
    /// 其余 panel 正常进入 analyst，最终 outer 返回 200。整测试在 15s 安全窗内完成（防回归死锁）。
    /// </summary>
    [Fact]
    public async Task FusionRouter_PanelTimeout_SlowPanelDoesNotBlockAnalyst()
    {
        using var factory = new FusionRouterFactory { FusionRouterPanelTimeoutSeconds = 1 };
        int modelACalls = 0;
        // model-a：panel 调用 await Delay(10s, ct)，会被 panel 超时 cts 在 1s 时取消；后续 analyst/outer 正常返回。
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            async (req, ct) =>
            {
                int call = ++modelACalls;
                if (call == 1)
                {
                    // panel 槽：故意慢，应被 panel 超时取消（抛 OperationCanceledException）。
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    return MakeResponse("model-a", 10, 5, "panel-a-unreachable");
                }
                return call == 2
                    ? MakeAnalystResponse("model-a", 10, 5, AnalystJson)
                    : MakeResponse("model-a", 10, 5, "quality-final");
            });
        // model-b/c：panel 立即返回；且在 model-a 超时被剔除后，会被 PickUnfailedFallback 选为
        // analyst/outer（候选链按 MaxContextTokens 降序，model-b 在 model-a 之后）。
        int modelBCalls = 0;
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => Task.FromResult(++modelBCalls switch
            {
                1 => MakeResponse("model-b", 10, 5, "panel-b"),
                2 => MakeAnalystResponse("model-b", 10, 5, AnalystJson),
                _ => MakeResponse("model-b", 10, 5, "quality-final")
            }));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 慢 panel 超时，但其余 2 个 panel 成功，analyst/outer 正常推进。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("quality-final", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 全部 panel 都慢且都被 panel 超时取消：panelAnswers.Count==0 → 回退串行；串行路径也失败 → 非 200。
    /// 关键：测试本身在 15s 内结束，证明 panel 超时确实生效（无超时则会卡到 Task.Delay 的 10s×N）。
    /// </summary>
    [Fact]
    public async Task FusionRouter_PanelTimeout_AllTimeoutFallsBackToSerial()
    {
        using var factory = new FusionRouterFactory { FusionRouterPanelTimeoutSeconds = 1 };
        // panel 调用（第 1 次）故意慢 10s，应被 1s 的 panel 超时取消；
        // 串行路径调用（第 2 次起）立即抛 503，避免串行路径也被 Task.Delay 拖住。
        int callsA = 0, callsB = 0, callsC = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => ++callsA == 1
                ? DelayThenThrow(TimeSpan.FromSeconds(10), ct)
                : throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            (req, ct) => ++callsB == 1
                ? DelayThenThrow(TimeSpan.FromSeconds(10), ct)
                : throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => ++callsC == 1
                ? DelayThenThrow(TimeSpan.FromSeconds(10), ct)
                : throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 全部 panel 超时 → 回退串行 → 串行也立即失败 → 非 200。
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// 模拟慢 panel：等待 delay，若被 panel 超时 cts 取消则抛 OperationCanceledException，
    /// 否则抛 503。两者均被 InvokePanelAsync 的 catch 转为失败元组。
    /// </summary>
    private static async Task<RawChatResponse> DelayThenThrow(TimeSpan delay, CancellationToken ct)
    {
        await Task.Delay(delay, ct);
        throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "slow-then-fail");
    }

    /// <summary>
    /// 默认 FusionRouterPanelTimeoutSeconds=0（关闭 panel 级超时）：WhenAll 等待全部 panel 完成。
    /// 一个稍慢（短延迟）的 panel 仍会被等待，最终正常返回。证明默认行为向后兼容。
    /// </summary>
    [Fact]
    public async Task FusionRouter_PanelTimeout_ZeroKeepsBackwardCompatible()
    {
        // 默认 FusionRouterPanelTimeoutSeconds=0。
        using var factory = new FusionRouterFactory();
        int modelACalls = 0;
        factory.MockClients["model-a"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-a" },
            (req, ct) => Task.FromResult(++modelACalls switch
            {
                1 => MakeResponse("model-a", 10, 5, "panel-a"),
                2 => MakeAnalystResponse("model-a", 10, 5, AnalystJson),
                _ => MakeResponse("model-a", 10, 5, "final")
            }));
        // model-b 稍慢（200ms），但 0=不启用 panel 超时，应被 WhenAll 等到。
        factory.MockClients["model-b"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-b" },
            async (req, ct) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                return MakeResponse("model-b", 10, 5, "panel-b");
            });
        factory.MockClients["model-c"] = new TestModelClient(
            new ModelEndpointOptions { Name = "model-c" },
            (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // 关闭 panel 超时：稍慢的 model-b 仍被等待，全部 panel 成功 → 200。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("final", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
        public async Task FusionRouter_PanelTemperature_UsesPanelTempForPanels_AnalystUsesFusionTemp()
        {
            // P1：panel 用 FusionRouterPanelTemperature，analyst 仍用 FusionRouterTemperature。
            using var factory = new FusionRouterFactory
            {
                FusionRouterTemperature = 0.7,
                FusionRouterPanelTemperature = 1.1
            };
            double? capturedPanelTemp = null;
            double? capturedAnalystTemp = null;
            int modelACalls = 0;
            factory.MockClients["model-a"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-a" },
                (req, ct) =>
                {
                    switch (++modelACalls)
                    {
                        case 1:
                            return Task.FromResult(MakeResponse("model-a", 10, 5, "panel-a"));
                        case 2:
                            capturedAnalystTemp = req.Temperature;
                            return Task.FromResult(MakeAnalystResponse("model-a", 10, 5, AnalystJson));
                        default:
                            return Task.FromResult(MakeResponse("model-a", 10, 5, "final"));
                    }
                });
            factory.MockClients["model-b"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-b" },
                (req, ct) =>
                {
                    capturedPanelTemp = req.Temperature;
                    return Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b"));
                });
            factory.MockClients["model-c"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-c" },
                (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
            using var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1.1, capturedPanelTemp);
            Assert.Equal(0.7, capturedAnalystTemp);
        }

        [Fact]
        public async Task FusionRouter_AnalystBrokenJson_RetriesWithJsonFormat_ThenSoftDegrades()
        {
            // P2：analyst 首次返回损坏 JSON → response_format 重试 → 重试仍坏 → 软降级用原始文本，不回退串行。
            using var factory = new FusionRouterFactory();
            const string brokenJson = "这不是 JSON";
            int modelACalls = 0;
            int analystRetries = 0;
            bool retryHadJsonFormat = false;
            factory.MockClients["model-a"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-a" },
                (req, ct) =>
                {
                    var callNum = ++modelACalls;
                    if (callNum == 1)
                    {
                        // panel call
                        return Task.FromResult(MakeResponse("model-a", 10, 5, "panel-a"));
                    }
                    else if (callNum == 2)
                    {
                        // first analyst call (broken JSON)
                        return Task.FromResult(MakeAnalystResponse("model-a", 10, 5, brokenJson));
                    }
                    else if (callNum == 3)
                    {
                        // analyst retry (should have response_format)
                        analystRetries++;
                        retryHadJsonFormat = req.ExtensionData is not null
                            && req.ExtensionData.TryGetValue("response_format", out var rf)
                            && rf.ValueKind == System.Text.Json.JsonValueKind.Object
                            && rf.GetProperty("type").GetString() == "json_object";
                        return Task.FromResult(MakeAnalystResponse("model-a", 10, 5, brokenJson));
                    }
                    else
                    {
                        // outer call
                        return Task.FromResult(MakeResponse("model-a", 10, 5, "final"));
                    }
                });
            factory.MockClients["model-b"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-b" },
                (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b")));
            factory.MockClients["model-c"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-c" },
                (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
            using var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, analystRetries);
            Assert.True(retryHadJsonFormat, "analyst retry should carry response_format=json_object");
            // 软降级：outer 仍产出最终答案（未回退串行）。
            Assert.Contains("final", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FusionRouter_AnalystBrokenJson_RetrySucceeds_UsesRetryResult()
        {
            // P2：analyst 首次损坏 → response_format 重试 → 重试返回合法 JSON，用重试结果。
            using var factory = new FusionRouterFactory();
            int modelACalls = 0;
            factory.MockClients["model-a"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-a" },
                (req, ct) => Task.FromResult(++modelACalls switch
                {
                    1 => MakeResponse("model-a", 10, 5, "panel-a"),
                    2 => MakeAnalystResponse("model-a", 10, 5, "这不是 JSON"),
                    _ => MakeResponse("model-a", 10, 5, "final")
                }));
            factory.MockClients["model-b"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-b" },
                (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b")));
            factory.MockClients["model-c"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-c" },
                (req, ct) => Task.FromResult(MakeAnalystResponse("model-c", 10, 5, AnalystJson)));

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
            using var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("final", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FusionRouter_SimpleRequest_SkipsFusion_WhenMinComplexityStandard()
        {
            // P3：MinComplexity=Standard + RuleClassifier 开启 → 单条短消息（Simple）不触发融合，走串行。
            using var factory = new FusionRouterFactory
            {
                FusionRouterMinComplexity = RequestComplexity.Standard,
                EnableRuleClassifierInFactory = true
            };
            int modelACalls = 0;
            factory.MockClients["model-a"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-a" },
                (req, ct) => Task.FromResult(++modelACalls switch
                {
                    1 => MakeResponse("model-a", 10, 5, "serial-answer"),
                    _ => MakeResponse("model-a", 10, 5, "unexpected")
                }));
            factory.MockClients["model-b"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-b" },
                (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b")));
            factory.MockClients["model-c"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-c" },
                (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
            // 单条短消息 → RuleClassifier 判 Simple。
            var request = BuildRequest() with { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") } };
            using var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // 未走融合：model-a 只被串行调用一次（无 panel/analyst/outer 多段调用）。
            Assert.Equal(1, modelACalls);
            Assert.Contains("serial-answer", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task FusionRouter_UnknownComplexity_TriggersFusion_ByDefault()
        {
            // P3：默认 MinComplexity=Unknown（无门控）→ RuleClassifier 关（复杂度 Unknown）仍触发融合，向后兼容。
            using var factory = new FusionRouterFactory();
            int modelACalls = 0;
            factory.MockClients["model-a"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-a" },
                (req, ct) => Task.FromResult(++modelACalls switch
                {
                    1 => MakeResponse("model-a", 10, 5, "panel-a"),
                    2 => MakeAnalystResponse("model-a", 10, 5, AnalystJson),
                    _ => MakeResponse("model-a", 10, 5, "final")
                }));
            factory.MockClients["model-b"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-b" },
                (req, ct) => Task.FromResult(MakeResponse("model-b", 10, 5, "panel-b")));
            factory.MockClients["model-c"] = new TestModelClient(
                new ModelEndpointOptions { Name = "model-c" },
                (req, ct) => Task.FromResult(MakeResponse("model-c", 10, 5, "panel-c")));

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionRouterFactory.Key);
            using var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(BuildRequest()), System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // 融合触发：model-a 被调 3 次（panel + analyst + outer）。
            Assert.Equal(3, modelACalls);
        }
}

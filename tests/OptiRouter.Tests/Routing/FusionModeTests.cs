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
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("fusion-test-key");
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
        Model = "auto",
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
    public async Task Fusion_CancelledAttempt_RecordsEstimatedCost()
    {
        // 修复验证：被取消的并行尝试必须记预估成本（上游已计费），且审计标注 IsEstimated=true。
        using var factory = new FusionFactory();
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 等取消的 slow task 收尾（fusion 内部 WhenAll 已等，这里给一点缓冲确保审计落库）。
        await Task.Delay(200);

        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        var recent = auditStore.GetRecent(10);

        // 两条记录：fast（采纳，真实成本）+ slow（被取消，预估成本）。
        var fast = recent.FirstOrDefault(r => r.Model == "fast-model");
        var slow = recent.FirstOrDefault(r => r.Model == "slow-model");
        Assert.NotNull(fast);
        Assert.NotNull(slow);
        Assert.True(fast.IsAdopted);
        Assert.False(fast.IsEstimated); // 真实成本
        Assert.False(slow.IsAdopted);
        Assert.True(slow.IsEstimated); // 预估成本
        Assert.Equal(fast.ParallelGroupId, slow.ParallelGroupId); // 同组
    }

    [Fact]
    public async Task Fusion_FailedAttempt_RecordsEstimatedCost()
    {
        // 真实失败的并行尝试也记预估成本。
        using var factory = new FusionFactory();
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            (req, ct) => Task.FromResult(MakeResponse("fast-model", 10, 5)));
        // slow 抛真实失败（非取消）——延迟确保 fast 先成功，slow 在收到 cancel 前抛错。
        // 用 503 模拟上游错误，fast 采纳后 slow 被 cancel，但若 slow 先抛 503 则计入失败。
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            (req, ct) => throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        // fast 采纳成功（slow 失败不影响采纳）。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        var recent = auditStore.GetRecent(10);
        var slow = recent.FirstOrDefault(r => r.Model == "slow-model" && !r.IsAdopted);
        Assert.NotNull(slow);
        Assert.True(slow.IsEstimated); // 失败也记预估
    }

    [Fact]
    public async Task Fusion_AuditGroup_OnlyOneAdoptedPerGroup()
    {
        // 同一并行组内仅采纳者 IsAdopted=true，其余 IsAdopted=false。
        using var factory = new FusionFactory();
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.PostAsync("/v1/chat/completions", content, cts.Token);
        await Task.Delay(300); // 等审计落库

        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        var recent = auditStore.GetRecent(20);

        // 找到 fusion 组记录（有 ParallelGroupId 的）。
        var groupRecords = recent.Where(r => !string.IsNullOrEmpty(r.ParallelGroupId)).ToList();
        Assert.True(groupRecords.Count >= 2, "应有至少 2 条并行组记录");

        // 同组内仅一条 IsAdopted=true。
        var groups = groupRecords.GroupBy(r => r.ParallelGroupId);
        foreach (var grp in groups)
        {
            int adopted = grp.Count(r => r.IsAdopted);
            Assert.True(adopted <= 1, $"组 {grp.Key} 内 IsAdopted=true 的记录应 ≤1，实际 {adopted}");
            // 采纳的那条 Success=true，被取消的 Success=false。
            var adoptedRec = grp.FirstOrDefault(r => r.IsAdopted);
            if (adoptedRec is not null)
                Assert.True(adoptedRec.Success);
        }
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

    /// <summary>
    /// Fusion-lite 凑不齐并行探测时的回归夹具：slow-model 预载为 HalfOpen 且半开探测槽位已被预占，
    /// 使 TryBeginProbe 拒绝它，但 FailoverPolicy 仍保留 HalfOpen 凑足 ≥2 候选
    /// ——从而精确复现 Race admitted&lt;2 回退串行、且不写入 failedInThisRequest 的路径。
    /// </summary>
    private sealed class FusionProbeShortfallFactory : WebApplicationFactory<Program>
    {
        public const string Key = "fusion-probe-key";
        public Dictionary<string, IModelClient> MockClients { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("fusion-probe-key");
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
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

                // 替换 ModelHealthTracker：用可变时钟先把 slow-model 熔断到 Open，再推进时钟使其过期转 HalfOpen，
                // 并预占其半开探测槽位（FailoverHalfOpenMaxProbes 默认 1）——模拟并发请求在途占满槽位的稳态。
                // FailoverPolicy 不排除 HalfOpen → 候选链仍含 slow-model 凑足 ≥2；但 Race/串行的 TryBeginProbe
                // 因槽位已满拒绝它 → admitted<2 回退串行。
                services.AddSingleton<ModelHealthTracker>(sp =>
                {
                    var now = DateTime.UtcNow;
                    var tracker = new ModelHealthTracker(() => now);
                    for (int i = 0; i < 5; i++)
                        tracker.RecordFailure("slow-model", 3, 60); // 连续失败达阈值 → Open
                    now = now.AddMinutes(5);                        // 推进时钟 → 下次 GetState 转 HalfOpen
                    tracker.TryBeginProbe("slow-model", 1);         // 占满半开槽位 → 后续 TryBeginProbe 被拒
                    return tracker;
                });

                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    [Fact]
    public async Task Fusion_ProbeShortfall_FallsBackToSerial_NoInfiniteLoop()
    {
        // 回归：候选存在但凑不齐并行探测（admitted<2）时，Fusion-lite 必须每请求最多触发一次，随后落串行降级。
        // 修复前：Race admitted<2 返回 null 且不写 failedInThisRequest，SendAsync 的 continue 无限重入本块
        //         （默认无全局超时，admitted<2 同步返回不观察 ct，客户端断开也无法打破 → 满 CPU 自旋）。
        using var factory = new FusionProbeShortfallFactory();
        // fast 串行命中即返回；slow 处于 HalfOpen 且槽位已满，Race 凑不齐数回退，串行也不会调用它。
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            (req, ct) => Task.FromResult(MakeResponse("fast-model", 10, 5)));
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            (req, ct) => Task.FromResult(MakeResponse("slow-model", 10, 5)));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FusionProbeShortfallFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        // 5 秒超时——修复前会因无限重入 Fusion-lite 块而挂死超时。
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("fast-model", doc.RootElement.GetProperty("model").GetString());
    }

    /// <summary>
    /// Hedging fixture：两个同 tier 模型，启用并行首试 + 延迟 hedging（主立即，hedged 延迟 500ms 才启动）。
    /// </summary>
    private sealed class HedgeFactory : WebApplicationFactory<Program>
    {
        public const string Key = "hedge-test-key";
        public Dictionary<string, IModelClient> MockClients { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("hedge-test-key");
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "fast-model", BaseUrl = "https://example.com", ApiKey = "k",
                        Tier = ModelTier.Medium, MaxContextTokens = 8192,
                        InputPricePerMillion = 1m, OutputPricePerMillion = 2m, Enabled = true
                    });
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "slow-model", BaseUrl = "https://example.com", ApiKey = "k",
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
                    opt.Routing.EnableFusionMode = true;
                    opt.Routing.FusionMaxParallel = 2;
                    opt.Routing.FusionHedgeDelayMs = 500; // 主立即，hedged 延迟 500ms
                    opt.Routing.EnableHealthProbe = false;
                });
                services.AddSingleton<IModelClientProvider>(new TestProvider(MockClients));
            });
        }
    }

    [Fact]
    public async Task Fusion_Hedge_PrimaryFast_HedgedNotLaunched()
    {
        // 主（fast-model，admitted[0]）立即成功（< HedgeDelayMs 500）→ raceCts 取消 → hedged 不启动（1× 成本）。
        using var factory = new HedgeFactory();
        bool slowCalled = false;
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            (req, ct) => Task.FromResult(MakeResponse("fast-model", 10, 5)));
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            (req, ct) => { slowCalled = true; return Task.FromResult(MakeResponse("slow-model", 10, 5)); });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HedgeFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("fast-model", doc.RootElement.GetProperty("model").GetString());
        Assert.False(slowCalled, "主请求快成功时 hedged 不应启动（不应调用 slow 上游）");
    }

    [Fact]
    public async Task Fusion_Hedge_PrimarySlow_HedgedLaunchedAndAdopted()
    {
        // 主（fast-model）慢：1s 后才返回（> HedgeDelayMs 500）→ hedged（slow-model）延迟到期启动并采纳。
        using var factory = new HedgeFactory();
        factory.MockClients["fast-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "fast-model" },
            async (req, ct) =>
            {
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) { throw new OperationCanceledException(ct); }
                return MakeResponse("fast-model", 10, 5);
            });
        // hedged（slow-model）延迟 500ms 后启动，立即返回 → 应被采纳。
        factory.MockClients["slow-model"] = new TestModelClient(
            new ModelEndpointOptions { Name = "slow-model" },
            (req, ct) => Task.FromResult(MakeResponse("slow-model", 10, 5)));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HedgeFactory.Key);

        var json = JsonSerializer.Serialize(BuildRequest());
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await client.PostAsync("/v1/chat/completions", content, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("slow-model", doc.RootElement.GetProperty("model").GetString());
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

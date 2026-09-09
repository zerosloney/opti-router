using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OptiRouter.Clients;
using OptiRouter.Compliance;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// P1-2 回归：最终响应输出审核必须覆盖全部非流式成功出口。
/// 修复前 Fusion（quality router）、Fusion-lite（race）、Cascade 升级三个提前返回点
/// 绕过串行分支的输出审核，开启 Block 策略时违规答案直达客户端。
/// </summary>
public sealed class OutputModerationExitTests
{
    private const string ViolatingText = "forbidden-phrase-answer";
    private const string SafeText = "safe answer";

    public enum ExitMode
    {
        Serial,
        FusionRouter,
        FusionLiteRace,
        CascadeUpgrade
    }

    /// <summary>确定性 fake 审核器：Output 方向含标记文本判违规，Input 恒放行。</summary>
    private sealed class FakeModerator : IContentModerator
    {
        public string Name => "fake";
        public int InputCalls;
        public int OutputCalls;

        public Task<ModerationResult> ModerateTextAsync(string text, ModerationDirection direction, CancellationToken ct = default)
        {
            if (direction == ModerationDirection.Input)
            {
                Interlocked.Increment(ref InputCalls);
                return Task.FromResult(new ModerationResult(false, null, 0.01, "input ok"));
            }

            Interlocked.Increment(ref OutputCalls);
            bool violation = text?.Contains("forbidden-phrase", StringComparison.OrdinalIgnoreCase) == true;
            return Task.FromResult(new ModerationResult(violation, violation ? "violence" : null, violation ? 0.95 : 0.01, "fake"));
        }
    }

    /// <summary>自包含测试宿主（同 ContentModerationIntegrationTests.ModerationFactory 惯例）。</summary>
    private sealed class ExitModeFactory : WebApplicationFactory<Program>
    {
        public const string TestKey = "moderation-exit-test-key";
        public FakeModerator Moderator { get; } = new();
        public int CheapVerifications;
        public int StrongRawCalls;
        public ExitMode Mode { get; set; }
        public bool Violating { get; set; }

        public new HttpClient CreateClient()
        {
            var client = base.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestKey);
            return client;
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:AdminApiKey", TestKey);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "6000");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.UseSetting("OptiRouter:EnableSingleInstanceGuard", "false");
            builder.UseSetting("OptiRouter:ConfigDbPath",
                Path.Combine(Path.GetTempPath(), "optirouter-config-test-" + Guid.NewGuid().ToString("N") + ".db"));
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey(TestKey);
                services.RemoveAll<OptiRouter.Compliance.IContentModerator>();
                services.AddSingleton<OptiRouter.Compliance.IContentModerator>(Moderator);
                services.AddSingleton<IModelClientProvider>(new TestModelClientProvider(BuildClients()));
                services.Configure<RouterOptions>(ConfigureRouting);
            });
        }

        /// <summary>按执行模式返回路由开关配置（模型定义与 CascadeHardConstraintTests 同源：cheap 为首选候选）。</summary>
        private void ConfigureRouting(RouterOptions options)
        {
            options.Models.Clear();
            options.Models.Add(new ModelEndpointOptions
            {
                Name = "local-cheap", BaseUrl = "http://localhost/v1", ApiKey = "test-only",
                Tier = ModelTier.Cheap, MaxContextTokens = 8192, Enabled = true,
                IsLocalOrPrivate = true, InputPricePerMillion = 1m, OutputPricePerMillion = 1m
            });
            options.Models.Add(new ModelEndpointOptions
            {
                Name = "cloud-strong", BaseUrl = "https://example.com/v1", ApiKey = "test-only",
                Tier = ModelTier.Strong, MaxContextTokens = 128000, Enabled = true,
                InputPricePerMillion = 2m, OutputPricePerMillion = 2m
            });

            // 级联模式对齐 CascadeHardConstraintTests 的已证配置：classifier 开启让 Simple 请求
            // 目标档位为 Cheap（cheap 排首选），failover 关闭避免降序排序把 Strong 排到首选。
            options.Routing.EnableRuleClassifier = Mode == ExitMode.CascadeUpgrade;
            options.Routing.EnableTokenEstimator = false;
            options.Routing.EnableBudgetGuard = false;
            options.Routing.EnableSemanticRouter = false;
            options.Routing.EnableSessionAffinity = false;
            options.Routing.EnableLoadBalance = false;
            options.Routing.EnableResponseCache = false;
            options.Routing.EnableSemanticCache = false;
            // 级联模式关 failover（同 CascadeHardConstraintTests 惯例）：failover 开启时候选按
            // MaxContextTokens 降序，Strong 会排到首选，级联升级路径无法触发。
            options.Routing.EnableFailover = Mode != ExitMode.CascadeUpgrade;
            options.Routing.EnableFusionRouter = Mode == ExitMode.FusionRouter;
            options.Routing.FusionRouterPanelSize = 2;
            options.Routing.EnableFusionMode = Mode == ExitMode.FusionLiteRace;
            options.Routing.EnableCascadeUpgrade = Mode == ExitMode.CascadeUpgrade;
            options.Routing.CascadeUpgradeSampleRate = 1;
            options.Routing.CascadeUpgradeVerifierModel = null;

            options.Routing.EnableContentModeration = true;
            options.Routing.ModerationInputAction = OptiRouter.Compliance.ModerationAction.Block;
            options.Routing.ModerationOutputAction = OptiRouter.Compliance.ModerationAction.Block;
            options.Routing.ModerationSampleRate = 1.0;
        }

        private Dictionary<string, IModelClient> BuildClients()
        {
            // 串行/并行模式不依赖候选顺序：违规场景两个模型的答案都携带违规文本；
            // Cascade 场景 Cheap 首选答案必须安全（先过审才轮到升级），违规文本由升级出的 Strong 答案携带。
            string primary = Violating && Mode != ExitMode.CascadeUpgrade ? ViolatingText : SafeText;
            string secondary = Violating ? ViolatingText : SafeText;
            var cheap = new ModelEndpointOptions
            {
                Name = "local-cheap", BaseUrl = "http://localhost/v1", ApiKey = "test-only",
                Tier = ModelTier.Cheap, MaxContextTokens = 8192, Enabled = true,
                IsLocalOrPrivate = true, InputPricePerMillion = 1m, OutputPricePerMillion = 1m
            };
            var strong = new ModelEndpointOptions
            {
                Name = "cloud-strong", BaseUrl = "https://example.com/v1", ApiKey = "test-only",
                Tier = ModelTier.Strong, MaxContextTokens = 128000, Enabled = true,
                InputPricePerMillion = 2m, OutputPricePerMillion = 2m
            };
            var usage = new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 };
            RawChatResponse Answer(string text) => new(
                JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = text } } } }), usage);
            ChatResponse Uncertain() => new()
            {
                Choices = [new() { Index = 0, Message = ChatMessage.FromText("assistant", "UNCERTAIN"), FinishReason = "stop" }],
                Usage = usage
            };

            return new Dictionary<string, IModelClient>
            {
                [cheap.Name] = new MockModelClient(cheap,
                    completeRawFunc: (_, _) => Task.FromResult(Answer(primary)),
                    completeFunc: (_, _) => { Interlocked.Increment(ref CheapVerifications); return Task.FromResult(Uncertain()); }),
                [strong.Name] = new MockModelClient(strong,
                    completeRawFunc: (_, _) => { Interlocked.Increment(ref StrongRawCalls); return Task.FromResult(Answer(secondary)); },
                    completeFunc: (_, _) => Task.FromResult(Uncertain()))
            };
        }
    }

    private static (ExitModeFactory Factory, string PrimaryText) Build(ExitMode mode, bool violating)
    {
        string primary = violating && mode != ExitMode.CascadeUpgrade ? ViolatingText : SafeText;
        var factory = new ExitModeFactory { Mode = mode, Violating = violating };
        return (factory, primary);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostChatAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/v1/chat/completions",
            new StringContent(
                JsonSerializer.Serialize(new ChatRequest { Model = "auto", Messages = [ChatMessage.FromText("user", "Hi")] }),
                System.Text.Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>所有执行模式 × 违规输出：违规文本不得送达客户端（400 CONTENT_MODERATED）。</summary>
    [Theory]
    [InlineData(ExitMode.Serial)]
    [InlineData(ExitMode.FusionRouter)]
    [InlineData(ExitMode.FusionLiteRace)]
    [InlineData(ExitMode.CascadeUpgrade)]
    public async Task ViolatingOutput_Blocked_InEveryExecutionMode(ExitMode mode)
    {
        var (factory, _) = Build(mode, violating: true);
        using var client = factory.CreateClient();

        var (status, body) = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("CONTENT_MODERATED", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(ViolatingText, body);
        Assert.True(factory.Moderator.OutputCalls >= 1, $"{mode}: output moderation never ran");

        // Cascade 场景必须真实发生过升级（cheap 校验 + strong 原始调用），否则测的不是升级出口。
        if (mode == ExitMode.CascadeUpgrade)
        {
            Assert.Equal(1, factory.CheapVerifications);
            Assert.Equal(1, factory.StrongRawCalls);
        }

        // Block 只拦截下发，不取消已发生的真实调用记账（替代模式在各自路径计费）。
        if (mode != ExitMode.Serial)
        {
            Assert.True(factory.Services.GetRequiredService<CostLedger>().GetDailySpend() > 0m,
                $"{mode}: blocked response must still bill the real upstream calls");
        }
    }

    /// <summary>所有执行模式 × 安全输出：放行且答案完整（审核不误伤）。</summary>
    [Theory]
    [InlineData(ExitMode.Serial)]
    [InlineData(ExitMode.FusionRouter)]
    [InlineData(ExitMode.FusionLiteRace)]
    [InlineData(ExitMode.CascadeUpgrade)]
    public async Task SafeOutput_Passes_InEveryExecutionMode(ExitMode mode)
    {
        var (factory, primary) = Build(mode, violating: false);
        using var client = factory.CreateClient();

        var (status, body) = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(primary, body);
        Assert.DoesNotContain(ViolatingText, body);
        Assert.True(factory.Moderator.OutputCalls >= 1, $"{mode}: output moderation never ran");
    }
}

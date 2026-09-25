using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 请求审计可观测性字段的端到端回归：
/// - UpstreamStatusCode：失败尝试的上游 HTTP 状态码独立成列（此前只藏在 ErrorMessage 字符串里，无法聚合）；
/// - RequestParams：输入参数快照（temp/max_tokens/msgs/tools/stream），与 RequestContent 同受 AuditStoreRequestContent 开关；
/// - RequestContent 兜底脱敏：用户文本中粘贴的密钥不原样落库（与上游 PII 脱敏开关无关）。
/// </summary>
public sealed class RequestAuditObservabilityTests
{
    private const string ModelName = "audit-model";

    private static ModelEndpointOptions CreateEndpoint() => new()
    {
        Name = ModelName,
        BaseUrl = "http://localhost/v1",
        ApiKey = "test-only",
        Tier = ModelTier.Medium,
        MaxContextTokens = 8192,
        Enabled = true,
        InputPricePerMillion = 1m,
        OutputPricePerMillion = 2m
    };

    private static void ConfigureOptions(RouterOptions options, bool contentAudit)
    {
        options.Models.Clear();
        options.Models.Add(CreateEndpoint());
        options.Routing.EnableRuleClassifier = false;
        options.Routing.EnableTokenEstimator = false;
        options.Routing.EnableBudgetGuard = false;
        options.Routing.EnableFailover = true;
        options.Routing.EnableSemanticRouter = false;
        options.Routing.EnableSessionAffinity = false;
        options.Routing.EnableLoadBalance = false;
        options.Routing.EnableResponseCache = false;
        options.Routing.EnableSemanticCache = false;
        options.Routing.EnableHealthProbe = false;
        options.Routing.EnableLatencyAware = false;
        options.Routing.EnablePiiAnonymization = false; // 兜底脱敏必须独立于该开关生效
        options.Routing.AuditStoreRequestContent = contentAudit;
    }

    private static ChatRequest RequestWith(string text) => new()
    {
        Model = ModelName,
        Messages = new List<ChatMessage> { ChatMessage.FromText("user", text) },
        Temperature = 0.7,
        MaxTokens = 512
    };

    private static string SuccessBody() => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content = "ok" }, finish_reason = "stop" } }
    });

    /// <summary>审计经后台批量写落库，轮询等待可见（与 MariaDB 集成测试同口径）。</summary>
    private static async Task<RequestAuditRecord> WaitForAuditAsync(
        IRequestAuditStore store, Func<RequestAuditRecord, bool> predicate)
    {
        for (int i = 0; i < 100; i++)
        {
            var row = store.GetRecent(50).FirstOrDefault(predicate);
            if (row is not null)
                return row;
            await Task.Delay(100);
        }

        var dump = string.Join(" | ", store.GetRecent(50)
            .Select(r => $"Model={r.Model} Success={r.Success} Err={r.ErrorMessage} Status={r.UpstreamStatusCode?.ToString() ?? "null"}"));
        Assert.Fail($"audit row did not become visible within 10s. Rows: {dump}");
        return null;
    }

    [Fact]
    public async Task SuccessAudit_MasksSecrets_AndRecordsRequestParams()
    {
        using var factory = new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(o => ConfigureOptions(o, contentAudit: true)),
            MockClients =
            {
                [ModelName] = new MockModelClient(CreateEndpoint(),
                    completeRawFunc: (_, _) => Task.FromResult(new RawChatResponse(SuccessBody(), Usage: null)))
            }
        };
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();

        var request = RequestWith("帮我处理这个 sk-abcdefgh12345678WXYZ-_ 密钥");
        await orchestrator.SendAsync(request, CancellationToken.None);

        var row = await WaitForAuditAsync(auditStore, r => r.Success && r.Model == ModelName);
        Assert.NotNull(row.RequestContent);
        Assert.Contains("sk-***", row.RequestContent, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh", row.RequestContent, StringComparison.Ordinal);

        // 输入参数快照与内容摘要同开关；采样参数与请求形态可检索。
        Assert.NotNull(row.RequestParams);
        Assert.Contains("\"temp\":0.7", row.RequestParams, StringComparison.Ordinal);
        Assert.Contains("\"max_tokens\":512", row.RequestParams, StringComparison.Ordinal);
        Assert.Contains("\"msgs\":1", row.RequestParams, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", row.RequestParams, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentAuditOff_ParamsAndContent_AreNull()
    {
        using var factory = new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(o => ConfigureOptions(o, contentAudit: false)),
            MockClients =
            {
                [ModelName] = new MockModelClient(CreateEndpoint(),
                    completeRawFunc: (_, _) => Task.FromResult(new RawChatResponse(SuccessBody(), Usage: null)))
            }
        };
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();

        await orchestrator.SendAsync(RequestWith("包含 sk-abcdefgh12345678WXYZ-_ 的请求"), CancellationToken.None);

        var row = await WaitForAuditAsync(auditStore, r => r.Success && r.Model == ModelName);
        Assert.Null(row.RequestContent);
        Assert.Null(row.RequestParams);
    }

    [Fact]
    public async Task FailureAudit_RecordsUpstreamStatusCode()
    {
        using var factory = new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(o => ConfigureOptions(o, contentAudit: true)),
            MockClients =
            {
                [ModelName] = new MockModelClient(CreateEndpoint(),
                    completeRawFunc: (_, _) => throw new ModelClientException(HttpStatusCode.Forbidden, "api key invalid"))
            }
        };
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();

        // 单候选 403：无其他候选可降级 → 原异常透传。
        await Assert.ThrowsAsync<ModelClientException>(
            () => orchestrator.SendAsync(RequestWith("hi"), CancellationToken.None));

        var row = await WaitForAuditAsync(auditStore, r => !r.Success && r.Model == ModelName);
        Assert.Equal(403, row.UpstreamStatusCode);
        Assert.Contains("upstream-status-403", row.ErrorMessage, StringComparison.Ordinal);
    }
}

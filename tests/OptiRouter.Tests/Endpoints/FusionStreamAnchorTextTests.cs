using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 回归：流式融合路由的 anchor 回答曾以原始 SSE JSON 行（choices/delta 块）拼接后喂给 analyst，
/// 而 secondary panel 是 ExtractAssistantText 的纯文本——analyst 的"多模型对比"建立在失真输入上。
/// 修复后 anchor 与 secondary 同为提取后的 delta 纯文本。
/// </summary>
public sealed class FusionStreamAnchorTextTests
{
    private const string AnchorText = "锚点正文";
    private const string SecondaryText = "次级panel正文";

    private static ModelEndpointOptions Endpoint(string name) => new()
    {
        Name = name,
        Id = name,
        BaseUrl = "http://localhost/v1",
        ApiKey = "test-only",
        Tier = ModelTier.Medium,
        MaxContextTokens = 8192,
        Enabled = true,
        InputPricePerMillion = 1m,
        OutputPricePerMillion = 2m
    };

    private static RawStreamLine DeltaLine(string text) => new(
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }),
        Usage: null, Metadata: null);

    private static async IAsyncEnumerable<RawStreamLine> AnchorStream(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return DeltaLine(AnchorText);
        yield return DeltaLine("结尾");
        yield return new RawStreamLine("[DONE]", null, null);
    }

    [Fact]
    public async Task StreamingFusion_FeedsAnchorPlainText_ToAnalyst()
    {
        var anchor = Endpoint("fusion-anchor");
        var second = Endpoint("fusion-second");
        var analyst = Endpoint("fusion-analyst");

        var analystRequests = new List<ChatRequest>();
        var clients = new Dictionary<string, IModelClient>
        {
            [anchor.Name] = new MockModelClient(anchor, streamRawFunc: (_, _) => AnchorStream()),
            [second.Name] = new MockModelClient(second, completeRawFunc: (_, _) =>
                Task.FromResult(new RawChatResponse(
                    JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = SecondaryText } } } }),
                    Usage: null))),
            [analyst.Name] = new MockModelClient(analyst, completeRawFunc: (request, _) =>
            {
                analystRequests.Add(request);
                return Task.FromResult(AnalystResponse());
            }),
        };

        var options = new RouterOptions();
        options.Models.Add(anchor);
        options.Models.Add(second);
        options.Models.Add(analyst);
        options.Routing.FusionRouterPanelSize = 3;
        options.Routing.FusionRouterAnalystModel = analyst.Name;
        options.Routing.EnableStreamingFusionQuorumGrace = false;

        var fusion = new FusionRouter(
            new TestModelClientProvider(clients),
            new ModelHealthTracker(),
            CreateRecorder(options),
            new FusionPanelSelector(),
            NullLogger<FusionRouter>.Instance);

        var decision = new RouterDecision
        {
            Candidates = new[] { anchor, second, analyst },
            Reason = "test",
            EstimatedInputTokens = 100,
            RequestComplexity = RequestComplexity.Standard,
            RequestIsStreaming = true
        };
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "问题") }
        };

        var lines = new List<RawStreamLine>();
        await foreach (var line in fusion.ExecuteStreamAsync(
            request, options, decision, estimatedTokens: 100, ModelTier.Medium,
            sessionId: null,
            failedInThisRequest: new HashSet<string>(),
            attemptedModels: new List<string>(),
            ct: CancellationToken.None))
        {
            lines.Add(line);
        }

        // 流正常收尾：anchor 两行 + 融合 patch + [DONE]。
        Assert.Equal("[DONE]", lines[^1].Data);
        Assert.Equal(4, lines.Count);
        Assert.Contains(lines, l => l.Data is not null && l.Data.Contains("fusion-patch", StringComparison.Ordinal));

        // analyst 收到的 panel 区必须是纯文本：anchor 与 secondary 均为可见正文，且不含原始 SSE JSON。
        // panelSize=3 时 analyst 端点也可能同时作为 secondary panel 被调用（合法复用），
        // 按 BuildAnalystRequest 的 "## Panel 回答" 段标记定位真正的 analyst 请求。
        var analystRequest = Assert.Single(analystRequests,
            r => r.Messages[^1].GetText().Contains("## Panel", StringComparison.Ordinal));
        string instruction = analystRequest.Messages[^1].GetText();
        Assert.Contains(AnchorText, instruction);
        Assert.Contains(SecondaryText, instruction);
        Assert.DoesNotContain("\"delta\"", instruction);
        Assert.DoesNotContain("choices", instruction);
    }

    private static RawChatResponse AnalystResponse()
    {
        // content 字段内嵌结构化分析 JSON（ParseAnalysis 解析目标）。
        string analysisJson = JsonSerializer.Serialize(new
        {
            consensus = "一致",
            contradictions = "",
            gaps = "",
            unique_insights = "独到见解",
            recommendation = "建议方向"
        });
        string body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content = analysisJson } } }
        });
        return new RawChatResponse(body, Usage: null);
    }

    private static OutcomeRecorder CreateRecorder(RouterOptions options) => new(
        auditStore: new InMemoryRequestAuditStore(),
        metrics: null!,
        ledger: new CostLedger(),
        options: new FakeRouterOptionsMonitor(options),
        affinityCache: new MemoryCache(new MemoryCacheOptions()),
        tsStore: new ThompsonStateStore(),
        promptAffinityStore: null!,
        quotaStore: new UpstreamQuotaStateStore(),
        logger: NullLogger<OutcomeRecorder>.Instance);

    private sealed class FakeRouterOptionsMonitor(RouterOptions current) : IOptionsMonitor<RouterOptions>
    {
        public RouterOptions CurrentValue => current;
        public RouterOptions Get(string? name) => current;
        public IDisposable? OnChange(Action<RouterOptions, string?> listener) => null;
    }
}

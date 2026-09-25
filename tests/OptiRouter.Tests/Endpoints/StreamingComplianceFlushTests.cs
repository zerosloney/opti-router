using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Compliance;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 回归：流式合规过滤器（Redact）的暂存尾部下发时序与跨 chunk 拼接完整性。
/// Redact 模式每块扣住末尾 maxKeywordLength-1 字符防跨 chunk 敏感词前缀泄漏：
/// 1) ProcessCompliance 曾在未命中块丢弃过滤器的 emit 文本——前缀原样发往客户端、下一块补发时重复；
/// 2) 补发曾排在 [DONE] 之后（融合路径则完全不补发）——SSE 客户端读到 [DONE] 即停止读取，尾部整段丢失。
/// </summary>
public sealed class StreamingComplianceFlushTests
{
    private const string ModelName = "redact-model";

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

    private static void ConfigureOptions(RouterOptions options)
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
        options.Routing.EnableStreamingComplianceFilter = true;
        options.Routing.StreamingComplianceAction = ComplianceAction.Redact;
        options.Routing.StreamingSensitiveKeywords = new List<string> { "秘密" };
        options.Routing.StreamingComplianceReplacementMask = "***";
    }

    private static RawStreamLine Delta(string text) => new(
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }),
        Usage: null, Metadata: null);

    /// <summary>敏感词"秘密"被拆到两个 chunk：前缀在 chunk1 末尾，后半在 chunk2 开头。</summary>
    private static Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>> SplitKeywordStream()
    {
        return (request, ct) => Iterate();

        async IAsyncEnumerable<RawStreamLine> Iterate([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return Delta("这是秘");
            yield return Delta("密内容");
            yield return new RawStreamLine("[DONE]", null, null);
        }
    }

    private static string? ExtractDeltaContent(string? data)
    {
        if (string.IsNullOrWhiteSpace(data) || data.Trim() == "[DONE]")
            return null;
        using var doc = JsonDocument.Parse(data);
        return doc.RootElement.GetProperty("choices")[0]
            .GetProperty("delta").GetProperty("content").GetString();
    }

    [Fact]
    public async Task Streaming_Redact_MasksCrossChunkKeyword_AndFlushesTailBeforeDone()
    {
        using var factory = new TestWebApplicationFactory
        {
            ConfigureTestServicesAction = services => services.Configure<RouterOptions>(ConfigureOptions),
            MockClients = { [ModelName] = new MockModelClient(CreateEndpoint(), streamRawFunc: SplitKeywordStream()) }
        };
        var orchestrator = factory.Services.GetRequiredService<ProxyOrchestrator>();
        var request = new ChatRequest { Model = ModelName, Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") } };

        var lines = new List<RawStreamLine>();
        await foreach (var line in orchestrator.StreamAsync(request, CancellationToken.None))
            lines.Add(line);

        Assert.Equal("[DONE]", lines[^1].Data);
        var contents = lines
            .Select(l => ExtractDeltaContent(l.Data))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        // 跨 chunk 敏感词完整脱敏：前缀"秘"不泄漏（曾随未命中块原样下发），
        // 暂存尾部"容"不丢失（曾排在 [DONE] 之后被客户端丢弃）。
        Assert.Equal(new[] { "这是", "***内", "容" }, contents);
        Assert.Equal("容", ExtractDeltaContent(lines[^2].Data));
    }
}

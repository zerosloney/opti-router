using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 协议流式外壳（<see cref="ProtocolStreamPipeline"/>）的故障分类口径钉扎：
/// 三个翻译型入口共用外壳，但错误帧词汇与"首帧前 vs 流中"的可重试口径留在各端点。
/// 重构只许搬外壳，动到这些断言就是行为变更。
/// </summary>
public sealed class ProtocolStreamFaultClassificationTests
{
    private const string PartialDelta =
        "{\"id\":\"chatcmpl-1\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}";

    private static ModelEndpointOptions CreateEndpoint() => new()
    {
        Name = "model-a",
        BaseUrl = "https://api.example.com",
        ApiKey = "sk-test",
        Tier = ModelTier.Medium,
        MaxContextTokens = 8192,
        InputPricePerMillion = 1m,
        OutputPricePerMillion = 2m,
        Enabled = true
    };

    private static TestWebApplicationFactory CreateFactory(
        Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>> streamRaw)
    {
        var endpoint = CreateEndpoint();
        var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(options =>
            {
                options.Models.Clear();
                options.Models.Add(endpoint);
                options.Routing.EnableRuleClassifier = false;
                options.Routing.EnableTokenEstimator = false;
                options.Routing.EnableBudgetGuard = false;
                options.Routing.EnableFailover = false;
            });
        };
        factory.MockClients["model-a"] = new MockModelClient(
            endpoint,
            completeRawFunc: null,
            streamRawFunc: streamRaw);
        return factory;
    }

    private static async Task<string> PostStreamAsync(TestWebApplicationFactory factory, string url, object body)
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadAsStringAsync();
    }

    private static AnthropicBody AnthropicStream() => new();

    private sealed record AnthropicBody
    {
        public string model { get; init; } = "auto";
        public int max_tokens { get; init; } = 64;
        public bool stream { get; init; } = true;
        public object[] messages { get; init; } = [new { role = "user", content = "Hi" }];
    }

    /// <summary>首帧之前即失败：枚举器第一次 MoveNextAsync 抛出。</summary>
    private static async IAsyncEnumerable<RawStreamLine> FailsBeforeFirst(Exception ex)
    {
        await Task.Yield();
        if (ex is not null)
        {
            throw ex;
        }
        yield break;
    }

    /// <summary>流中失败：已经吐出增量内容之后抛出（客户端已收到部分正文）。</summary>
    private static async IAsyncEnumerable<RawStreamLine> FailsMidStream(Exception ex)
    {
        yield return new RawStreamLine(PartialDelta, null);
        await Task.Yield();
        throw ex;
    }

    // ===== Anthropic /v1/messages =====

    /// <summary>
    /// 首帧之前的候选失败由编排器包装成 AllCandidatesFailedException（failover 关闭时单候选即全灭），
    /// 端点收到的是它的分类帧——钉扎外壳的"首帧前"分支与各协议词汇。
    /// </summary>
    [Fact]
    public async Task Anthropic_CandidateFailureBeforeFirstEvent_EmitsApiErrorFrame()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsBeforeFirst(new ModelClientException(System.Net.HttpStatusCode.BadGateway, "upstream said no"))),
            "/v1/messages",
            AnthropicStream());

        Assert.Contains("event: error", body);
        Assert.Contains("\"type\":\"api_error\"", body);
        Assert.Contains("all model candidates failed", body);
        Assert.DoesNotContain("upstream said no", body);
    }

    [Fact]
    public async Task Anthropic_UnexpectedMidStream_KeepsInternalDetailOutOfResponse()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsMidStream(new InvalidOperationException("internal plumbing detail"))),
            "/v1/messages",
            AnthropicStream());

        Assert.Contains("partial", body);
        Assert.Contains("\"type\":\"api_error\"", body);
        Assert.Contains(ProtocolErrorHelper.InternalErrorMessage, body);
        Assert.DoesNotContain("internal plumbing detail", body);
    }

    // ===== Gemini /v1beta =====

    /// <summary>首帧前的候选失败（编排器包装为 AllCandidatesFailed）在 Gemini 侧是 503 UNAVAILABLE。</summary>
    [Fact]
    public async Task Gemini_CandidateFailureBeforeFirstEvent_EmitsUnavailableFrame()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsBeforeFirst(new ModelClientException(System.Net.HttpStatusCode.BadGateway, "upstream said no"))),
            "/v1beta/models/auto:streamGenerateContent?alt=sse",
            new { contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } } });

        Assert.Contains("\"code\":503", body);
        Assert.Contains("\"status\":\"UNAVAILABLE\"", body);
        Assert.Contains("all model candidates failed", body);
        Assert.DoesNotContain("upstream said no", body);
    }

    /// <summary>首帧前的取消同样落编排器包装路径：503 UNAVAILABLE 而非 DEADLINE_EXCEEDED。</summary>
    [Fact]
    public async Task Gemini_CancellationBeforeFirstEvent_EmitsUnavailableFrame()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsBeforeFirst(new OperationCanceledException("deadline raced"))),
            "/v1beta/models/auto:streamGenerateContent?alt=sse",
            new { contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } } });

        Assert.Contains("\"code\":503", body);
        Assert.Contains("\"status\":\"UNAVAILABLE\"", body);
        Assert.DoesNotContain("deadline raced", body);
    }

    /// <summary>
    /// 钉扎既有不对称：Gemini 只在首帧前把取消当 DEADLINE_EXCEEDED，流中取消落 INTERNAL 兜底桶
    /// （与 Anthropic/Responses 的 timeout_error 不同）。外壳下沉时不许顺手"抹平"它。
    /// </summary>
    [Fact]
    public async Task Gemini_CanceledMidStream_FallsIntoInternalBucket()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsMidStream(new OperationCanceledException("deadline raced"))),
            "/v1beta/models/auto:streamGenerateContent?alt=sse",
            new { contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } } });

        Assert.Contains("partial", body);
        Assert.Contains("\"status\":\"INTERNAL\"", body);
        Assert.DoesNotContain("DEADLINE_EXCEEDED", body);
        Assert.DoesNotContain("deadline raced", body);
    }

    // ===== OpenAI Responses /v1/responses =====

    [Fact]
    public async Task Responses_BudgetExhaustedBeforeFirstEvent_EmitsRateLimitErrorFrame()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsBeforeFirst(new BudgetExhaustedException("daily budget spent"))),
            "/v1/responses",
            new { model = "auto", input = "Hi", stream = true });

        Assert.Contains("event: error", body);
        Assert.Contains("\"code\":\"rate_limit_error\"", body);
        Assert.Contains("budget exhausted", body);
    }

    [Fact]
    public async Task Responses_UpstreamIoFaultMidStream_CarriesRealReason()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsMidStream(new IOException("upstream connection reset"))),
            "/v1/responses",
            new { model = "auto", input = "Hi", stream = true });

        // 流中已经吐出的正文保持可见，故障原因外发（可重试信号）
        Assert.Contains("response.output_text.delta", body);
        Assert.Contains("event: error", body);
        Assert.Contains("\"code\":\"api_error\"", body);
        Assert.Contains("upstream connection reset", body);
    }

    [Fact]
    public async Task Responses_UnexpectedMidStream_EmitsEventErrorFrameWithoutLeak()
    {
        var body = await PostStreamAsync(
            CreateFactory((_, _) => FailsMidStream(new InvalidOperationException("internal plumbing detail"))),
            "/v1/responses",
            new { model = "auto", input = "Hi", stream = true });

        // Responses 的流内错误是独立 event: error 帧，不是 chat 的 data: {error} 兜底
        Assert.Contains("event: error", body);
        Assert.Contains(ProtocolErrorHelper.InternalErrorMessage, body);
        Assert.DoesNotContain("internal plumbing detail", body);
    }
}

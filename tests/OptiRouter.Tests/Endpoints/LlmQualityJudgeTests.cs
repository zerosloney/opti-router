using System.Collections.Concurrent;
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

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// LLM-as-judge 采样打分测试。
/// 集成断言只看"judge 调用发生 + 审计落痕"，不比 α 值——主路径 RecordThompsonOutcome
/// 与回灌共享同一 Beta 状态，α 差值无法把两者解耦，避免脆弱断言。
/// </summary>
public class LlmQualityJudgeTests
{
    private const string ScoreJson = "{\"score\":0.75,\"reason\":\"correct but terse\"}";

    private static string AssistantBody(string content)
        => $"{{\"model\":\"stub\",\"choices\":[{{\"message\":{{\"role\":\"assistant\",\"content\":{JsonSerializer.Serialize(content)}}}}}]}}";

    /// <summary>按模型名定制的 stub：target 回 "ok"，judge 回固定 JSON（或违约文本/异常）。</summary>
    private sealed class JudgeStubClient : IModelClient
    {
        private readonly ConcurrentQueue<string> _calls;
        private readonly TaskCompletionSource<bool>? _judgeRelease;
        private readonly ConcurrentQueue<string>? _completedCalls;

        public JudgeStubClient(ModelEndpointOptions endpoint, ConcurrentQueue<string> calls,
            string judgeContent = ScoreJson, bool failJudge = false,
            TaskCompletionSource<bool>? judgeRelease = null,
            ConcurrentQueue<string>? completedCalls = null)
        {
            Endpoint = endpoint;
            _calls = calls;
            _judgeRelease = judgeRelease;
            _completedCalls = completedCalls;
            _content = endpoint.Name == "judge-x" ? judgeContent : "ok";
            _failJudge = failJudge && endpoint.Name == "judge-x";
        }

        private readonly string _content;
        private readonly bool _failJudge;

        public ModelEndpointOptions Endpoint { get; }

        public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(Endpoint.Name);
            if (_failJudge)
            {
                throw new ModelClientException(
                    HttpStatusCode.BadGateway,
                    responseBody: null,
                    message: $"simulated judge failure of {Endpoint.Name}");
            }
            if (Endpoint.Name == "judge-x" && _judgeRelease is not null)
                return WaitForReleaseAsync(cancellationToken);
            return Task.FromResult(new RawChatResponse(AssistantBody(_content), null));
        }

        private async Task<RawChatResponse> WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            await _judgeRelease!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _completedCalls?.Enqueue(Endpoint.Name);
            return new RawChatResponse(AssistantBody(_content), null);
        }

        public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("Stream not used in these tests.");

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
            => Task.FromResult(new ModelHealthResult(true, 1));
    }

    /// <summary>双模型工厂：target-a(Medium) + judge-x(Strong)。实例属性在首次请求前设置即生效。</summary>
    private sealed class QualityJudgeFactory : WebApplicationFactory<Program>
    {
        public ConcurrentQueue<string> CalledModels { get; } = new();
        public ConcurrentQueue<string> CompletedModels { get; } = new();
        public bool EnableQualityJudge = true;
        public bool EnableDataSovereignty;
        public string QualityJudgeModel = "judge-x";
        public double SampleRate = 1.0;
        public string JudgeContent = ScoreJson;
        public bool FailJudge;
        public TaskCompletionSource<bool>? JudgeRelease;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("judge-test-key");
                services.Configure<RouterOptions>(options =>
                {
                    options.Models.Clear();
                    options.Models.Add(new ModelEndpointOptions
                    {
                        Name = "target-a", Id = "m-target", BaseUrl = "https://api.t.com/v1",
                        ApiKey = "k", Tier = ModelTier.Medium, MaxContextTokens = 64000, Enabled = true,
                        IsLocalOrPrivate = true
                    });
                    options.Models.Add(new ModelEndpointOptions
                    {
                        Name = "judge-x", Id = "m-judge", BaseUrl = "https://api.j.com/v1",
                        ApiKey = "k", Tier = ModelTier.Strong, MaxContextTokens = 128000, Enabled = true,
                        IsLocalOrPrivate = false
                    });
                    options.Routing.EnableThompsonSampling = true;
                    options.Routing.EnableQualityJudge = EnableQualityJudge;
                    options.Routing.EnableDataSovereignty = EnableDataSovereignty;
                    options.Routing.QualityJudgeSampleRate = SampleRate;
                    options.Routing.QualityJudgeModel = QualityJudgeModel;
                });

                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IModelClientProvider));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IModelClientProvider>(sp =>
                {
                    var models = sp.GetRequiredService<IOptions<RouterOptions>>().Value.Models;
                    var clients = models.ToDictionary(
                        m => m.Name,
                        m => (IModelClient)new JudgeStubClient(
                            m, CalledModels, JudgeContent, FailJudge, JudgeRelease, CompletedModels));
                    return new AutoRoutingClientProvider(clients);
                });
            });
        }
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    private static HttpClient CreateClient(QualityJudgeFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "judge-test-key");
        return client;
    }

    private static async Task<HttpResponseMessage> PostChatAsync(HttpClient client)
    {
        var content = new StringContent(
            """{"model":"target-a","messages":[{"role":"user","content":"what is rust ownership"}]}""",
            Encoding.UTF8,
            "application/json");
        return await client.PostAsync("/v1/chat/completions", content);
    }

    [Fact]
    public async Task HappyPath_JudgeCalledAndAuditRecorded()
    {
        using var factory = new QualityJudgeFactory();
        var client = CreateClient(factory);

        var response = await PostChatAsync(client);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.True(await UntilAsync(() => factory.CalledModels.Contains("judge-x")),
            "judge model should be called once sampled");

        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        Assert.True(await UntilAsync(() =>
            auditStore.GetRecent(50).Any(r => r.Model == "judge-x"
                && r.RoutingReason.Contains("llm-judge", StringComparison.Ordinal) && r.Success)),
            "audit should carry a successful llm-judge row");
    }

    [Fact]
    public async Task JudgeOutputUnparsable_MainFlowUnaffected()
    {
        using var factory = new QualityJudgeFactory { JudgeContent = "sorry I cannot produce JSON" };
        var client = CreateClient(factory);

        var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // 主回答由 target-a 产出且不受 judge 契约违约影响；judge 自身被调用并留痕。
        Assert.True(await UntilAsync(() => factory.CalledModels.Contains("judge-x")));
    }

    [Fact]
    public async Task SelfJudgeGuard_TargetEqualsJudge_SkipsExtraCall()
    {
        using var factory = new QualityJudgeFactory { QualityJudgeModel = "target-a" };
        var client = CreateClient(factory);

        await PostChatAsync(client);
        await Task.Delay(600);

        Assert.Equal(1, factory.CalledModels.Count(m => m == "target-a"));
    }

    [Fact]
    public async Task Disabled_NeverCallsJudge()
    {
        using var factory = new QualityJudgeFactory { EnableQualityJudge = false };
        var client = CreateClient(factory);

        var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(600);
        Assert.DoesNotContain("judge-x", factory.CalledModels);
    }

    [Fact]
    public async Task DataSovereignty_ExternalJudgeNeverCalled()
    {
        using var factory = new QualityJudgeFactory { EnableDataSovereignty = true };
        var client = CreateClient(factory);

        var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(600);
        Assert.Equal(new[] { "target-a" }, factory.CalledModels.ToArray());
    }

    [Fact]
    public async Task ZeroSampleRate_NeverCallsJudge()
    {
        using var factory = new QualityJudgeFactory { SampleRate = 0.0 };
        var client = CreateClient(factory);

        var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(600);
        Assert.DoesNotContain("judge-x", factory.CalledModels);
    }

    [Fact]
    public async Task JudgeConcurrencyLimit_DropsFifthSampleWhenFourCallsAreInFlight()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new QualityJudgeFactory { JudgeRelease = release };
        using var client = CreateClient(factory);

        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostChatAsync(client)));
            try
            {
                Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            }
            finally
            {
                foreach (var response in responses)
                    response.Dispose();
            }

            Assert.True(await UntilAsync(
                () => factory.CalledModels.Count(model => model == "judge-x") == 4),
                "the first four sampled judge calls should be in flight");

            using var fifthResponse = await PostChatAsync(client);
            Assert.Equal(HttpStatusCode.OK, fifthResponse.StatusCode);

            Assert.False(await UntilAsync(
                () => factory.CalledModels.Count(model => model == "judge-x") > 4, timeoutMs: 1000),
                "the fifth sampled request must not start another judge call while the limit is full");

            release.TrySetResult(true);
            Assert.True(await UntilAsync(
                () => factory.CompletedModels.Count(model => model == "judge-x") == 4),
                "all four in-flight judge calls should complete after release");
            await Task.Delay(200);
            Assert.Equal(4, factory.CalledModels.Count(model => model == "judge-x"));
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task JudgeUpstreamFailure_NotFatalAndAudited()
    {
        using var factory = new QualityJudgeFactory { FailJudge = true };
        var client = CreateClient(factory);

        var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var auditStore = factory.Services.GetRequiredService<IRequestAuditStore>();
        Assert.True(await UntilAsync(() =>
            auditStore.GetRecent(50).Any(r => r.Model == "judge-x"
                && r.RoutingReason.Contains("llm-judge failed", StringComparison.Ordinal) && !r.Success)),
            "failed judge upstream call should be audited");
    }

    [Fact]
    public void ParseScore_ValidNumberApplied()
    {
        var resp = new RawChatResponse(AssistantBody(ScoreJson), null);
        Assert.Equal(0.75, LlmQualityJudge.ParseScore(resp));
    }

    [Fact]
    public void ParseScore_FencedJson_ToleratedByRepairer()
    {
        var resp = new RawChatResponse(AssistantBody($"```json\n{ScoreJson}\n```"), null);
        Assert.Equal(0.75, LlmQualityJudge.ParseScore(resp));
    }

    [Fact]
    public void ParseScore_OutOfRange_Clamped()
    {
        var resp = new RawChatResponse(AssistantBody("{\"score\":1.7,\"reason\":\"over\"}"), null);
        Assert.Equal(1.0, LlmQualityJudge.ParseScore(resp));
    }

    [Fact]
    public void ParseScore_MissingField_ReturnsNull()
    {
        var resp = new RawChatResponse(AssistantBody("{\"reason\":\"no score here\"}"), null);
        Assert.Null(LlmQualityJudge.ParseScore(resp));
    }

    [Fact]
    public void TruncateQuestion_KeepsOtherMessagesIntact()
    {
        var request = new ChatRequest
        {
            Model = "any",
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", new string('a', 5000)),
                ChatMessage.FromText("assistant", "short answer"),
                ChatMessage.FromText("user", "tail question")
            }
        };

        var truncated = LlmQualityJudge.TruncateQuestion(request);

        // judge prompt 只嵌入最后一个 user 消息，仅它需要截断；历史消息保持原样。
        Assert.Equal(3, truncated.Messages.Count);
        Assert.Equal(new string('a', 5000), truncated.Messages[0].GetText());
        Assert.Equal("short answer", truncated.Messages[1].GetText());
        Assert.Equal("tail question", truncated.Messages[2].GetText());
    }

    [Fact]
    public void TruncateQuestion_LongLastUserMessage_CappedAtLimit()
    {
        var request = new ChatRequest
        {
            Model = "any",
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", new string('a', LlmQualityJudge.MaxQuestionChars + 1000))
            }
        };

        var truncated = LlmQualityJudge.TruncateQuestion(request);

        Assert.Single(truncated.Messages);
        Assert.Equal(LlmQualityJudge.MaxQuestionChars, truncated.Messages[0].GetText().Length);
    }
}

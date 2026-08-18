using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 全面审查修复的回归测试：
/// #1 /v1beta 纳入限流；#2 不可重试 4xx 降级/透传语义；#4 流式 Persona 锚定；
/// #5 有界响应读取；#6 损坏配置不抛异常。
/// </summary>
public class ReviewFixTests
{
    private static ModelEndpointOptions CreateEndpoint(string name, ModelTier tier = ModelTier.Medium) => new()
    {
        Name = name,
        BaseUrl = "https://api.example.com",
        ApiKey = "sk-test",
        Tier = tier,
        MaxContextTokens = 8192,
        InputPricePerMillion = 1m,
        OutputPricePerMillion = 2m,
        Enabled = true
    };

    private static TestWebApplicationFactory CreateFactory(
        Action<RouterOptions>? configure = null,
        Dictionary<string, IModelClient>? clients = null)
    {
        var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
                configure?.Invoke(opt);
            });
        };
        if (clients is not null)
        {
            foreach (var (name, client) in clients)
            {
                factory.MockClients[name] = client;
            }
        }
        return factory;
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }

    #region #1 /v1beta 限流

    [Fact]
    public async Task V1Beta_Endpoint_IsRateLimited()
    {
        // RequestsPerMinute=1：第二次请求必须 429。修复前 /v1beta 不匹配 "/v1" 段，绕过限流。
        var endpoint = CreateEndpoint("model-a");
        var factory = CreateFactory(opt => opt.Models.Add(endpoint));
        factory.RequestsPerMinute = 1;
        factory.MockClients["model-a"] = new MockModelClient(endpoint, (req, ct) =>
            Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-rl\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
                new ChatUsage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 })));
        using var client = factory.CreateClient();

        string body = JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } }
        });

        var first = await PostJsonAsync(client, "/v1beta/models/model-a:generateContent", body);
        var second = await PostJsonAsync(client, "/v1beta/models/model-a:generateContent", body);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    #endregion

    #region #2 不可重试 4xx 降级 / 透传

    [Fact]
    public async Task AutoRouting_Upstream401_FallsBackToHealthyCandidate()
    {
        // model-a 上游 401（key 失效），model-b 健康。auto 路由必须返回 200，
        // 不得因 401 穿透降级链直接失败（修复前行为，线上已复现）。
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        int upstream401Attempts = 0;

        var factory = CreateFactory(opt =>
        {
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
        });
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (req, ct) =>
        {
            Interlocked.Increment(ref upstream401Attempts);
            throw new ModelClientException(HttpStatusCode.Unauthorized, "{\"error\":\"bad api key\"}");
        });
        factory.MockClients["model-b"] = new MockModelClient(endpointB, (req, ct) =>
            Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-b\",\"model\":\"model-b\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"from b\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
                new ChatUsage { PromptTokens = 3, CompletionTokens = 2, TotalTokens = 5 })));

        using var client = factory.CreateClient();
        string body = JsonSerializer.Serialize(new
        {
            model = "auto",
            messages = new object[] { new { role = "user", content = "Hi" } }
        });

        // 多次请求消除路由顺序随机性：任一次首选 model-a 都必须降级到 model-b 而非失败
        for (int i = 0; i < 5; i++)
        {
            var response = await PostJsonAsync(client, "/v1/chat/completions", body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // 证明 401 模型确实被尝试过（降级路径真实执行，而非从未选中）
        Assert.True(upstream401Attempts >= 1);
    }

    [Fact]
    public async Task ExplicitModel_Upstream401_PassthroughKeepsOriginalStatus()
    {
        // 显式单模型：无其他候选可降级时保持透传语义，401 原样到达客户端
        var endpointA = CreateEndpoint("model-a");
        var factory = CreateFactory(opt => opt.Models.Add(endpointA));
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (req, ct) =>
            throw new ModelClientException(HttpStatusCode.Unauthorized, "{\"error\":\"bad api key\"}"));

        using var client = factory.CreateClient();
        string body = JsonSerializer.Serialize(new
        {
            model = "model-a",
            messages = new object[] { new { role = "user", content = "Hi" } }
        });

        var response = await PostJsonAsync(client, "/v1/chat/completions", body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion

    #region #4 流式 Persona 锚定

    [Fact]
    public async Task Streaming_PersonaDriftProtection_InjectsAnchor()
    {
        // 修复前：PersonaDriftGuard 只在非流式 SendAsync 应用，流式请求静默失效
        var endpoint = CreateEndpoint("model-a");
        ChatRequest? captured = null;

        var factory = CreateFactory(opt =>
        {
            opt.Models.Add(endpoint);
            opt.Routing.EnablePersonaDriftProtection = true;
        });
        factory.MockClients["model-a"] = new MockModelClient(
            endpoint,
            streamRawFunc: (req, ct) =>
            {
                captured = req;
                return PersonaTestStream();
            });

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "auto",
                stream = true,
                messages = new object[] { new { role = "user", content = "Hi" } }
            }), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Session-Id", "session-persona-test");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        Assert.NotNull(captured);
        Assert.Contains(captured!.Messages, m =>
            m.Role == "system" && m.GetText().Contains(PersonaDriftGuard.DefaultPersonaAnchorInstruction[..10]));
    }

    private static async IAsyncEnumerable<RawStreamLine> PersonaTestStream()
    {
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-p\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"}}]}", null);
        yield return new RawStreamLine("[DONE]", null);
        await Task.Yield();
    }

    #endregion

    #region #5 有界响应读取

    [Fact]
    public async Task ReadBodyAsync_ExceedsLimit_Throws()
    {
        var content = new ByteArrayContent(new byte[BoundedResponseReader.MaxNonStreamingResponseBytes + 1]);
        await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(
            () => BoundedResponseReader.ReadBodyAsync(content, CancellationToken.None));
    }

    [Fact]
    public async Task ReadBodyAsync_WithinLimit_ReturnsBody()
    {
        byte[] payload = Encoding.UTF8.GetBytes("hello bounded world");
        var content = new ByteArrayContent(payload);
        string body = await BoundedResponseReader.ReadBodyAsync(content, CancellationToken.None);
        Assert.Equal("hello bounded world", body);
    }

    [Fact]
    public async Task ReadLinesAsync_LineExceedsLimit_Throws()
    {
        // 2 MB 无换行单行 → 必须在限值处中断，而非把整行读进内存
        var stream = new MemoryStream(new byte[BoundedResponseReader.MaxStreamLineBytes * 2]);
        await Assert.ThrowsAsync<ResponseSizeLimitExceededException>(async () =>
        {
            await foreach (var _ in BoundedResponseReader.ReadLinesAsync(stream))
            {
            }
        });
    }

    [Fact]
    public async Task ReadLinesAsync_SplitsOnLfAndCrLf()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("event: a\r\ndata: 1\ndata: 2"));
        var lines = new List<string>();
        await foreach (var line in BoundedResponseReader.ReadLinesAsync(stream))
        {
            lines.Add(line);
        }
        // \r\n 与 \n 均归一，行尾 \r 剥离；无尾换行的最后一行也要产出
        Assert.Equal(new[] { "event: a", "data: 1", "data: 2" }, lines);
    }

    #endregion

    #region #6 损坏配置

    [Fact]
    public void ModelsProvider_CorruptJson_EmptyDataWithoutThrow()
    {
        string dir = Path.Combine(Path.GetTempPath(), "optirouter-provider-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "models-config.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json !!!");
            var provider = new ModelsJsonConfigurationProvider(path);
            provider.Load();
            // Data 为 protected，经 TryGet 断言无任何模型键（空配置继续运行）
            Assert.False(provider.TryGet("OptiRouter:Models:0:Name", out _));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    #endregion
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 下游协议对齐端点集成测试：Anthropic /v1/messages 与 Gemini /v1beta 端点，
/// 走完整 HTTP 管道验证请求翻译 → 路由 → 响应回译（非流式与流式），以及原生鉴权头。
/// </summary>
public class NativeProtocolEndpointsTests
{
    private const string OpenAiResponseBody =
        "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hi there\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}";

    private const string OpenAiToolCallResponseBody =
        "{\"id\":\"chatcmpl-2\",\"object\":\"chat.completion\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_weather\",\"arguments\":\"{\\\"city\\\":\\\"SF\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":8,\"total_tokens\":13}}";

    private static ModelEndpointOptions CreateEndpoint(string name) => new()
    {
        Name = name,
        BaseUrl = "https://api.example.com",
        ApiKey = "sk-test",
        Tier = ModelTier.Medium,
        MaxContextTokens = 8192,
        InputPricePerMillion = 1m,
        OutputPricePerMillion = 2m,
        Enabled = true
    };

    private sealed class CapturedRequestContext
    {
        public ChatRequest? LastRequest;
    }

    /// <summary>创建注入 mock 模型客户端的工厂：非流式返回固定 OpenAI JSON，流式返回固定增量序列。</summary>
    private static (TestWebApplicationFactory Factory, CapturedRequestContext Captured) CreateFactory(
        string responseBody = OpenAiResponseBody,
        string modelName = "model-a")
    {
        var factory = new TestWebApplicationFactory();
        var endpoint = CreateEndpoint(modelName);
        var captured = new CapturedRequestContext();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(endpoint);
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };
        factory.MockClients[modelName] = new MockModelClient(
            endpoint,
            (req, ct) =>
            {
                captured.LastRequest = req;
                return Task.FromResult(new RawChatResponse(
                    responseBody,
                    new ChatUsage { PromptTokens = 5, CompletionTokens = 2, TotalTokens = 7 }));
            },
            (req, ct) => CreateOpenAiStream(req));
        return (factory, captured);
    }

    private static async IAsyncEnumerable<RawStreamLine> CreateOpenAiStream(ChatRequest request)
    {
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-1\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}", null);
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-1\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello \"}}]}", null);
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-1\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"world\"}}]}", null);
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-1\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}",
            new ChatUsage { PromptTokens = 5, CompletionTokens = 2, TotalTokens = 7 });
        yield return new RawStreamLine("[DONE]", null);
        await Task.Yield();
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(url, content);
    }

    #region Anthropic /v1/messages

    [Fact]
    public async Task Anthropic_NonStream_TranslatesRequestAndResponse()
    {
        var (factory, captured) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "auto",
            max_tokens = 100,
            system = "You are helpful.",
            temperature = 0.5,
            messages = new object[] { new { role = "user", content = "Hello there" } }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        var contentBlock = root.GetProperty("content")[0];
        Assert.Equal("text", contentBlock.GetProperty("type").GetString());
        Assert.Equal("Hi there", contentBlock.GetProperty("text").GetString());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("output_tokens").GetInt32());

        // 请求方向翻译：system 拆出、max_tokens/temperature 映射
        var req = Assert.IsType<ChatRequest>(captured.LastRequest);
        Assert.Equal(100, req.MaxTokens);
        Assert.Equal(0.5, req.Temperature);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("You are helpful.", req.Messages[0].GetText());
        Assert.Equal("user", req.Messages[1].Role);
        Assert.Equal("Hello there", req.Messages[1].GetText());
    }

    [Fact]
    public async Task Anthropic_ToolRoundTrip_MapsToolUseAndToolResult()
    {
        var (factory, captured) = CreateFactory(OpenAiToolCallResponseBody);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "auto",
            max_tokens = 100,
            messages = new object[]
            {
                new { role = "user", content = "weather in SF?" },
                new
                {
                    role = "assistant",
                    content = new object[] { new { type = "tool_use", id = "toolu_1", name = "get_weather", input = new { city = "SF" } } }
                },
                new
                {
                    role = "user",
                    content = new object[] { new { type = "tool_result", tool_use_id = "toolu_1", content = "72F sunny" } }
                }
            }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 请求方向：tool_use → assistant tool_calls；tool_result → tool 消息
        var req = Assert.IsType<ChatRequest>(captured.LastRequest);
        Assert.Contains(req.Messages, m => m.Role == "tool"
            && m.GetText() == "72F sunny"
            && m.ExtensionData!["tool_call_id"].GetString() == "toolu_1");
        var assistantToolCall = req.Messages.First(m => m.Role == "assistant"
            && m.ExtensionData is not null
            && m.ExtensionData.ContainsKey("tool_calls"));
        var toolCall = assistantToolCall.ExtensionData!["tool_calls"][0];
        Assert.Equal("get_weather", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("toolu_1", toolCall.GetProperty("id").GetString());

        // 响应方向：OpenAI tool_calls → Anthropic tool_use 块
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
        var toolUse = root.GetProperty("content")[0];
        Assert.Equal("tool_use", toolUse.GetProperty("type").GetString());
        Assert.Equal("get_weather", toolUse.GetProperty("name").GetString());
        // 响应方向保留上游（OpenAI）tool_call id，不做重写
        Assert.Equal("call_1", toolUse.GetProperty("id").GetString());
        Assert.Equal("SF", toolUse.GetProperty("input").GetProperty("city").GetString());
    }

    [Fact]
    public async Task Anthropic_Stream_EmitsNativeEventSequence()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "auto",
            max_tokens = 100,
            stream = true,
            messages = new object[] { new { role = "user", content = "Hi stream" } }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("event: message_start", body);
        Assert.Contains("event: content_block_start", body);
        Assert.Contains("event: content_block_delta", body);
        Assert.Contains("Hello ", body);
        Assert.Contains("world", body);
        Assert.Contains("event: content_block_stop", body);
        Assert.Contains("event: message_delta", body);
        Assert.Contains("\"stop_reason\":\"end_turn\"", body);
        Assert.Contains("\"output_tokens\":2", body);
        Assert.Contains("event: message_stop", body);
    }

    [Fact]
    public async Task Anthropic_UnknownModel_Returns404WithErrorEnvelope()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "no-such-model",
            max_tokens = 100,
            messages = new object[] { new { role = "user", content = "Hi" } }
        }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", root.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Anthropic_MissingMaxTokens_Returns400()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "auto",
            messages = new object[] { new { role = "user", content = "Hi" } }
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region Gemini /v1beta

    [Fact]
    public async Task Gemini_NonStream_TranslatesRequestAndResponse()
    {
        var (factory, captured) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/model-a:generateContent", JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new object[] { new { text = "Be brief." } } },
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hello gemini" } } } },
            generationConfig = new { maxOutputTokens = 100, temperature = 0.5 }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var candidate = root.GetProperty("candidates")[0];
        Assert.Equal("Hi there", candidate.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("model", candidate.GetProperty("content").GetProperty("role").GetString());
        Assert.Equal("STOP", candidate.GetProperty("finishReason").GetString());
        Assert.Equal(5, root.GetProperty("usageMetadata").GetProperty("promptTokenCount").GetInt32());
        Assert.Equal(2, root.GetProperty("usageMetadata").GetProperty("candidatesTokenCount").GetInt32());
        Assert.Equal("model-a", root.GetProperty("modelVersion").GetString());

        // 请求方向翻译：systemInstruction → system；generationConfig → MaxTokens/Temperature
        var req = Assert.IsType<ChatRequest>(captured.LastRequest);
        Assert.Equal("model-a", req.Model);
        Assert.Equal(100, req.MaxTokens);
        Assert.Equal(0.5, req.Temperature);
        Assert.Equal("system", req.Messages[0].Role);
        Assert.Equal("Be brief.", req.Messages[0].GetText());
        Assert.Equal("Hello gemini", req.Messages[^1].GetText());
    }

    [Fact]
    public async Task Gemini_FunctionCallRoundTrip_MapsToolMessages()
    {
        var (factory, captured) = CreateFactory(OpenAiToolCallResponseBody);
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/model-a:generateContent", JsonSerializer.Serialize(new
        {
            contents = new object[]
            {
                new { role = "user", parts = new object[] { new { text = "weather?" } } },
                new { role = "model", parts = new object[] { new { functionCall = new { name = "get_weather", args = new { city = "SF" } } } } },
                new { role = "user", parts = new object[] { new { functionResponse = new { name = "get_weather", response = new { result = "72F" } } } } }
            }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 请求方向：functionCall → assistant tool_calls；functionResponse → tool 消息
        var req = Assert.IsType<ChatRequest>(captured.LastRequest);
        Assert.Contains(req.Messages, m => m.Role == "tool"
            && m.ExtensionData!["tool_call_id"].GetString() == "get_weather");
        var assistantToolCall = req.Messages.First(m => m.Role == "assistant"
            && m.ExtensionData is not null
            && m.ExtensionData.ContainsKey("tool_calls"));
        Assert.Equal("get_weather",
            assistantToolCall.ExtensionData!["tool_calls"][0].GetProperty("function").GetProperty("name").GetString());

        // 响应方向：OpenAI tool_calls → functionCall part
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parts = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        Assert.Equal("get_weather", parts[0].GetProperty("functionCall").GetProperty("name").GetString());
        Assert.Equal("SF", parts[0].GetProperty("functionCall").GetProperty("args").GetProperty("city").GetString());
    }

    [Fact]
    public async Task Gemini_Stream_EmitsChunksWithFinishAndUsage()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/model-a:streamGenerateContent?alt=sse", JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi stream" } } } },
            generationConfig = new { maxOutputTokens = 100 }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"text\":\"Hello \"", body);
        Assert.Contains("\"text\":\"world\"", body);
        Assert.Contains("\"finishReason\":\"STOP\"", body);
        Assert.Contains("\"promptTokenCount\":5", body);
        Assert.Contains("\"candidatesTokenCount\":2", body);
        Assert.DoesNotContain("[DONE]", body);
    }

    [Fact]
    public async Task Gemini_UnknownModel_Returns404WithErrorEnvelope()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/no-such-model:generateContent", JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } }
        }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Gemini_ModelIdWithSlash_RoutesCorrectly()
    {
        // 显示 id 形如 "{供应商}/{Id}" 含斜杠：catch-all 路由必须匹配完整 id
        var (factory, captured) = CreateFactory(modelName: "provider/model-a");
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/provider/model-a:generateContent", JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Hi there", doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("provider/model-a", captured.LastRequest?.Model);
    }

    [Fact]
    public async Task Gemini_UnknownAction_Returns404()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/model-a:someUnknownAction", JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "Hi" } } } }
        }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Gemini_EmptyContents_Returns400()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient();

        var response = await PostAsync(client, "/v1beta/models/model-a:generateContent", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    #region 原生鉴权头

    [Fact]
    public async Task Auth_XApiKeyHeader_AuthorizesAnthropicEndpoint()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient(null);
        client.DefaultRequestHeaders.Add("x-api-key", TestWebApplicationFactory.TestProxyApiKey);

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "auto",
            max_tokens = 50,
            messages = new object[] { new { role = "user", content = "auth check" } }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_XApiKeyHeader_WrongKey_Returns401()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient(null);
        client.DefaultRequestHeaders.Add("x-api-key", "wrong-key");

        var response = await PostAsync(client, "/v1/messages", JsonSerializer.Serialize(new
        {
            model = "auto",
            max_tokens = 50,
            messages = new object[] { new { role = "user", content = "auth check" } }
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_GoogApiKeyHeader_AuthorizesGeminiEndpoint()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient(null);
        client.DefaultRequestHeaders.Add("x-goog-api-key", TestWebApplicationFactory.TestProxyApiKey);

        var response = await PostAsync(client, "/v1beta/models/model-a:generateContent", JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "auth check" } } } }
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_KeyQueryParameter_AuthorizesGeminiEndpoint()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient(null);

        var response = await PostAsync(client,
            $"/v1beta/models/model-a:generateContent?key={TestWebApplicationFactory.TestProxyApiKey}",
            JsonSerializer.Serialize(new
            {
                contents = new object[] { new { role = "user", parts = new object[] { new { text = "auth check" } } } }
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_GeminiWithoutKey_Returns401()
    {
        var (factory, _) = CreateFactory();
        using var client = factory.CreateClient(null);

        var response = await PostAsync(client, "/v1beta/models/model-a:generateContent", JsonSerializer.Serialize(new
        {
            contents = new object[] { new { role = "user", parts = new object[] { new { text = "auth check" } } } }
        }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    #endregion
}

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Protocols;

/// <summary>
/// 原生协议客户端端到端测试：TestServer 模拟 Anthropic/Gemini API，
/// 验证请求体翻译与响应/流式翻译回 OpenAI 契约。
/// </summary>
public sealed class NativeProtocolClientTests
{
    private static async Task<HttpClient> StartMockServerAsync(
        string path,
        Func<string, string> respond,
        Action<string>? captureRequestBody = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();

        app.MapPost(path, async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            captureRequestBody?.Invoke(body);

            if (ctx.Request.Headers.Accept.ToString().Contains("text/event-stream"))
            {
                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync(respond(body));
            }
            else
            {
                await ctx.Response.WriteAsJsonAsync(JsonDocument.Parse(respond(body)).RootElement);
            }
        });

        await app.StartAsync();
        return app.GetTestClient();
    }

    private static ModelEndpointOptions CreateEndpoint(ProviderProtocol protocol, string id)
    {
        return new ModelEndpointOptions
        {
            Name = id,
            Id = id,
            BaseUrl = "http://localhost",
            ApiKey = "sk-test",
            Tier = ModelTier.Strong,
            Protocol = protocol,
            Enabled = true
        };
    }

    private static ChatRequest CreateRequest(string userText, int? maxTokens = null)
    {
        return new ChatRequest
        {
            Model = "auto",
            MaxTokens = maxTokens,
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", "You are helpful."),
                ChatMessage.FromText("user", userText)
            }
        };
    }

    [Fact]
    public async Task AnthropicClient_CompleteRaw_ReturnsOpenAiContract()
    {
        string? capturedBody = null;
        var http = await StartMockServerAsync("/v1/messages", _ => """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-3-5-sonnet",
            "content":[{"type":"text","text":"Bonjour"}],"stop_reason":"end_turn",
            "usage":{"input_tokens":10,"output_tokens":3}}
            """, b => capturedBody = b);
        var endpoint = CreateEndpoint(ProviderProtocol.Anthropic, "claude-3-5-sonnet");
        var client = new AnthropicModelClient(endpoint, http);

        var response = await client.CompleteRawAsync(CreateRequest("Say hi"), CancellationToken.None);

        // 请求翻译校验
        Assert.NotNull(capturedBody);
        using var reqDoc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("claude-3-5-sonnet", reqDoc.RootElement.GetProperty("model").GetString());
        Assert.Equal("You are helpful.", reqDoc.RootElement.GetProperty("system").GetString());

        // 响应 OpenAI 契约
        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("Bonjour", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(10, response.Usage!.PromptTokens);
        Assert.Equal(3, response.Usage.CompletionTokens);
    }

    [Fact]
    public async Task AnthropicClient_StreamRaw_TranslatesEventsToOpenAiLines()
    {
        var http = await StartMockServerAsync("/v1/messages", _ => """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """);
        var client = new AnthropicModelClient(CreateEndpoint(ProviderProtocol.Anthropic, "claude-3-5-sonnet"), http);

        var lines = new List<RawStreamLine>();
        await foreach (var line in client.StreamRawAsync(CreateRequest("hi"), CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(4, lines.Count); // Hel / lo / finish_reason / [DONE]
        Assert.Contains("Hel", lines[0].Data);
        Assert.Contains("lo", lines[1].Data);
        Assert.Contains("\"finish_reason\":\"stop\"", lines[2].Data);
        Assert.Equal("[DONE]", lines[3].Data);
    }

    [Fact]
    public async Task AnthropicClient_StreamRaw_SendsStreamFlagSamplingParamsAndNameFallback()
    {
        // 修复回归：① 请求体必须带 stream:true（真实 Anthropic API 仅凭 Accept 头不返回 SSE）；
        // ② temperature/top_p/stop 需映射；③ Id 留空时 model 回退 Name（UpstreamModelId）。
        string? capturedBody = null;
        var http = await StartMockServerAsync("/v1/messages", _ => """
            data: {"type":"message_start","message":{"id":"msg_1"}}

            data: {"type":"message_stop"}

            """, b => capturedBody = b);
        var endpoint = new ModelEndpointOptions
        {
            Name = "claude-3-5-sonnet", // Id 留空
            BaseUrl = "http://localhost",
            ApiKey = "sk-test",
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.Anthropic,
            Enabled = true
        };
        var client = new AnthropicModelClient(endpoint, http);

        var request = new ChatRequest
        {
            Model = "auto",
            Stream = true,
            Temperature = 0.7,
            MaxTokens = 64,
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["top_p"] = JsonSerializer.SerializeToElement(0.9),
                ["stop"] = JsonSerializer.SerializeToElement("\n\n")
            }
        };

        await foreach (var _ in client.StreamRawAsync(request, CancellationToken.None)) { }

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        Assert.Equal("claude-3-5-sonnet", root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.7, root.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, root.GetProperty("top_p").GetDouble());
        Assert.Equal("\n\n", root.GetProperty("stop_sequences")[0].GetString());
    }

    [Fact]
    public async Task GeminiClient_CompleteRaw_ReturnsOpenAiContract()
    {
        string? capturedBody = null;
        var http = await StartMockServerAsync("/v1beta/models/gemini-1.5-pro:generateContent", _ => """
            {"candidates":[{"content":{"parts":[{"text":"Ciao"}],"role":"model"},"finishReason":"STOP"}],
            "usageMetadata":{"promptTokenCount":8,"candidatesTokenCount":2}}
            """, b => capturedBody = b);
        var endpoint = CreateEndpoint(ProviderProtocol.Gemini, "gemini-1.5-pro");
        var client = new GeminiModelClient(endpoint, http);

        var response = await client.CompleteRawAsync(CreateRequest("Say hi", maxTokens: 128), CancellationToken.None);

        Assert.NotNull(capturedBody);
        using var reqDoc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("user", reqDoc.RootElement.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal(128, reqDoc.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());

        using var doc = JsonDocument.Parse(response.Body);
        Assert.Equal("Ciao", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(8, response.Usage!.PromptTokens);
    }

    [Fact]
    public async Task GeminiClient_StreamRaw_TranslatesDataLines()
    {
        // 修复后流式走 :streamGenerateContent（mock 路径同步更新；打到 :generateContent 会 404）
        var http = await StartMockServerAsync("/v1beta/models/gemini-1.5-pro:streamGenerateContent", _ => """
            data: {"candidates":[{"content":{"parts":[{"text":"Hola "}],"role":"model"}}]}

            data: {"candidates":[{"content":{"parts":[{"text":"mundo"}],"role":"model"}}]}

            data: {"done":true}

            """);
        var client = new GeminiModelClient(CreateEndpoint(ProviderProtocol.Gemini, "gemini-1.5-pro"), http);

        var lines = new List<RawStreamLine>();
        await foreach (var line in client.StreamRawAsync(CreateRequest("hi"), CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.Equal(3, lines.Count);
        Assert.Contains("Hola", lines[0].Data);
        Assert.Contains("mundo", lines[1].Data);
        Assert.Equal("[DONE]", lines[^1].Data);
    }

    [Fact]
    public async Task GeminiClient_StreamRaw_UsesStreamEndpointWithAltSseAndNameFallback()
    {
        // 修复回归：流式必须请求 :streamGenerateContent?alt=sse（真实 Gemini 在 :generateContent
        // 只返回单个 JSON），且 Id 留空时路径回退 Name。mock 只注册正确路径，打错路径即 404 失败。
        string? capturedTarget = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapPost("/v1beta/models/gemini-1.5-pro:streamGenerateContent", async (HttpContext ctx) =>
        {
            capturedTarget = ctx.Request.Path + ctx.Request.QueryString;
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hola\"}],\"role\":\"model\"}}]}\n\n");
        });
        await app.StartAsync();

        var endpoint = new ModelEndpointOptions
        {
            Name = "gemini-1.5-pro", // Id 留空
            BaseUrl = "http://localhost",
            ApiKey = "sk-test",
            Tier = ModelTier.Strong,
            Protocol = ProviderProtocol.Gemini,
            Enabled = true
        };
        var client = new GeminiModelClient(endpoint, app.GetTestClient());

        var lines = new List<RawStreamLine>();
        await foreach (var line in client.StreamRawAsync(CreateRequest("hi"), CancellationToken.None))
        {
            lines.Add(line);
        }

        Assert.NotEmpty(lines);
        Assert.Contains("Hola", lines[0].Data);
        Assert.NotNull(capturedTarget);
        Assert.Contains(":streamGenerateContent", capturedTarget);
        Assert.Contains("alt=sse", capturedTarget);
    }

    [Fact]
    public async Task AnthropicClient_UpstreamError_NormalizedToModelClientException()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapPost("/v1/messages", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return ctx.Response.WriteAsync("{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}");
        });
        await app.StartAsync();

        var client = new AnthropicModelClient(CreateEndpoint(ProviderProtocol.Anthropic, "claude-3-5-sonnet"), app.GetTestClient());
        var ex = await Assert.ThrowsAsync<ModelClientException>(() => client.CompleteRawAsync(CreateRequest("hi"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Contains("rate_limit_error", ex.ResponseBody);
    }

    [Fact]
    public void ModelClientFactory_SelectsClientByProtocol()
    {
        var factory = new ModelClientFactory();

        var openAi = factory.Create(CreateEndpoint(ProviderProtocol.OpenAI, "gpt-4o"), new HttpClient());
        var anthropic = factory.Create(CreateEndpoint(ProviderProtocol.Anthropic, "claude-3-5-sonnet"), new HttpClient());
        var gemini = factory.Create(CreateEndpoint(ProviderProtocol.Gemini, "gemini-1.5-pro"), new HttpClient());

        Assert.IsType<OpenAICompatibleModelClient>(openAi);
        Assert.IsType<AnthropicModelClient>(anthropic);
        Assert.IsType<GeminiModelClient>(gemini);
    }

    [Fact]
    public void ModelClientFactory_ConfiguresProtocolSpecificAuthenticationHeaders()
    {
        var factory = new ModelClientFactory();
        using var http = new HttpClient();

        factory.Create(CreateEndpoint(ProviderProtocol.Anthropic, "claude-3-5-sonnet"), http);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.Equal("sk-test", http.DefaultRequestHeaders.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", http.DefaultRequestHeaders.GetValues("anthropic-version").Single());
        Assert.False(http.DefaultRequestHeaders.Contains("x-goog-api-key"));

        factory.Create(CreateEndpoint(ProviderProtocol.Gemini, "gemini-1.5-pro"), http);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.False(http.DefaultRequestHeaders.Contains("x-api-key"));
        Assert.False(http.DefaultRequestHeaders.Contains("anthropic-version"));
        Assert.Equal("sk-test", http.DefaultRequestHeaders.GetValues("x-goog-api-key").Single());

        factory.Create(CreateEndpoint(ProviderProtocol.OpenAI, "gpt-4o"), http);
        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("sk-test", http.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.False(http.DefaultRequestHeaders.Contains("x-api-key"));
        Assert.False(http.DefaultRequestHeaders.Contains("x-goog-api-key"));
        Assert.False(http.DefaultRequestHeaders.Contains("anthropic-version"));

        var emptyKey = CreateEndpoint(ProviderProtocol.Anthropic, "claude-3-5-sonnet");
        emptyKey.ApiKey = "  ";
        factory.Create(emptyKey, http);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.False(http.DefaultRequestHeaders.Contains("x-api-key"));
        Assert.False(http.DefaultRequestHeaders.Contains("x-goog-api-key"));
        Assert.False(http.DefaultRequestHeaders.Contains("anthropic-version"));
    }

    [Theory]
    [InlineData(ProviderProtocol.Anthropic)]
    [InlineData(ProviderProtocol.Gemini)]
    public async Task NativeClients_RetryTransientFailures_PerMaxRetries(ProviderProtocol protocol)
    {
        // 首次 500（可重试）→ 第二次成功：MaxRetries>=1 时原生协议客户端必须重试而非直接失败
        //（此前 MaxRetries 仅对 OpenAI 客户端生效，原生协议静默无重试）。
        var handler = new SequenceHandler(call => call == 0
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"transient\"}")
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(protocol == ProviderProtocol.Anthropic
                    ? """{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}"""
                    : """{"candidates":[{"content":{"parts":[{"text":"ok"}],"role":"model"},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":1}}""")
            });
        var endpoint = CreateEndpoint(protocol, "native-retry");
        endpoint.MaxRetries = 1;
        var client = new ModelClientFactory().Create(endpoint, new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var response = await client.CompleteRawAsync(CreateRequest("ping"), CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.Contains("ok", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeClients_MaxRetriesZero_DoesNotRetry()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":\"boom\"}")
        });
        var endpoint = CreateEndpoint(ProviderProtocol.Anthropic, "native-noretry");
        endpoint.MaxRetries = 0;
        var client = new ModelClientFactory().Create(endpoint, new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        await Assert.ThrowsAsync<ModelClientException>(
            () => client.CompleteRawAsync(CreateRequest("ping"), CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>按调用序号返回响应的桩 handler，记录调用次数。</summary>
    private sealed class SequenceHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Calls++;
            return Task.FromResult(respond(call));
        }
    }
}

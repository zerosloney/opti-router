using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

public sealed class ResponsesEndpointTests
{
    private static ModelEndpointOptions CreateEndpoint(string name)
    {
        return new ModelEndpointOptions
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
    }

    private static TestWebApplicationFactory CreateFactory(
        string? modelName = "model-a",
        Func<ChatRequest, CancellationToken, Task<RawChatResponse>>? completeRaw = null,
        Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>>? streamRaw = null)
    {
        var factory = new TestWebApplicationFactory();
        var endpoint = CreateEndpoint(modelName ?? "model-a");
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
        factory.MockClients[modelName ?? "model-a"] = new MockModelClient(
            endpoint,
            completeRawFunc: completeRaw,
            streamRawFunc: streamRaw);
        return factory;
    }

    private static StringContent ResponsesBody(string model, string input, bool stream = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input
        };
        if (stream) body["stream"] = true;
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task NonStreaming_ReturnsResponsesShape()
    {
        using var factory = CreateFactory(
            completeRaw: (_, _) => Task.FromResult(new RawChatResponse(
                """
                {"id":"chatcmpl-1","model":"model-a","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
                """,
                new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 })));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/responses", ResponsesBody("auto", "Hi"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("model-a", root.GetProperty("model").GetString());

        var output = root.GetProperty("output");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal("message", output[0].GetProperty("type").GetString());
        Assert.Equal("Hello", output[0].GetProperty("content")[0].GetProperty("text").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(2, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(1, usage.GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public async Task UnknownModel_Returns404ModelNotFound()
    {
        using var factory = CreateFactory(
            completeRaw: (_, _) => Task.FromResult(new RawChatResponse("{}", null)));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/responses", ResponsesBody("nope-model", "Hi"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("model_not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingInput_Returns400()
    {
        using var factory = CreateFactory(
            completeRaw: (_, _) => Task.FromResult(new RawChatResponse("{}", null)));
        using var client = factory.CreateClient();
        using var content = new StringContent("""{ "model": "auto" }""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/responses", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task EmptyBody_Returns400LikeSiblingEntries()
    {
        using var factory = CreateFactory(
            completeRaw: (_, _) => Task.FromResult(new RawChatResponse("{}", null)));
        using var client = factory.CreateClient();
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/responses", content);

        // 空/空白请求体与兄弟端点同口径：400 invalid_request_error，而非 ArgumentException 逃逸成 500。
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_request_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task BudgetExhausted_ReturnsBudgetExceededEnvelope()
    {
        // 错误信封 type/code 词汇与 chat 入口对齐：预算耗尽必须是 budget_exceeded/BUDGET_EXHAUSTED，
        // 不能落到 Anthropic 风格 code 经 OpenAiErrorType 映射出的 server_error。
        var endpoint = CreateEndpoint("model-a");
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(options =>
            {
                options.Models.Clear();
                options.Models.Add(endpoint);
                options.Budget.DailyBudgetUsd = 10m;
                options.Budget.EnforceOnExhausted = BudgetExhaustionMode.Reject;
                options.Routing.EnableBudgetGuard = true;
                options.Routing.EnableRuleClassifier = false;
                options.Routing.EnableTokenEstimator = false;
                options.Routing.EnableFailover = false;
            });
        };
        factory.MockClients["model-a"] = new MockModelClient(
            endpoint,
            completeRawFunc: (_, _) => Task.FromResult(new RawChatResponse("{}", null)));

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        ledger.Record(20m);

        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/responses", ResponsesBody("model-a", "Hi"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("budget_exceeded", error.GetProperty("type").GetString());
        Assert.Equal("BUDGET_EXHAUSTED", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Streaming_ReturnsFramedSseEventSequence()
    {
        using var factory = CreateFactory(
            streamRaw: (_, ct) => CreateStreamChunks("Hello world", ct));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/responses", ResponsesBody("auto", "Hi", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        string sse = await response.Content.ReadAsStringAsync();
        var frames = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(frames);

        // 每帧都必须是 event:/data: 双行——子串断言放过不成帧的实现，这里逐帧校验结构
        foreach (var frame in frames)
        {
            var lines = frame.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("event: ", lines[0]);
            Assert.StartsWith("data: ", lines[1]);
        }

        var eventTypes = frames.Select(f => f.Split('\n')[0]["event: ".Length..]).ToArray();
        Assert.Equal("response.created", eventTypes[0]);
        Assert.Equal("response.completed", eventTypes[^1]);
        Assert.Contains("response.output_item.added", eventTypes);
        Assert.Contains("response.content_part.added", eventTypes);
        Assert.Contains("response.output_text.delta", eventTypes);
        Assert.Contains("response.output_text.done", eventTypes);
        Assert.Contains("response.output_item.done", eventTypes);

        // 官方 SDK 依赖 data 行的 delta 字段拼装文本
        var deltas = new List<string>();
        foreach (var frame in frames.Where(f => f.StartsWith("event: response.output_text.delta")))
        {
            using var doc = JsonDocument.Parse(frame.Split('\n')[1]["data: ".Length..]);
            deltas.Add(doc.RootElement.GetProperty("delta").GetString() ?? string.Empty);
        }
        Assert.Equal("Hello world", string.Concat(deltas));

        using var completed = JsonDocument.Parse(frames[^1].Split('\n')[1]["data: ".Length..]);
        var output = completed.RootElement.GetProperty("response").GetProperty("output");
        Assert.Equal("Hello world", output[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Streaming_ToolCallFragments_EmitFunctionCallItems()
    {
        using var factory = CreateFactory(streamRaw: (_, ct) => CreateToolCallChunks(ct));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/responses", ResponsesBody("auto", "天气", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string sse = await response.Content.ReadAsStringAsync();
        var frames = sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var eventTypes = frames.Select(f => f.Split('\n')[0]["event: ".Length..]).ToArray();

        Assert.Contains("response.function_call_arguments.delta", eventTypes);
        Assert.Contains("response.function_call_arguments.done", eventTypes);
        Assert.DoesNotContain("response.output_text.delta", eventTypes);
        Assert.Equal("response.completed", eventTypes[^1]);

        using var completed = JsonDocument.Parse(frames[^1].Split('\n')[1]["data: ".Length..]);
        var output = completed.RootElement.GetProperty("response").GetProperty("output");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal("function_call", output[0].GetProperty("type").GetString());
        Assert.Equal("get_weather", output[0].GetProperty("name").GetString());
        Assert.Equal("call_1", output[0].GetProperty("call_id").GetString());
        Assert.Equal("""{"city":"Beijing"}""", output[0].GetProperty("arguments").GetString());
    }

    private static async IAsyncEnumerable<RawStreamLine> CreateToolCallChunks(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new RawStreamLine(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Bei"}}]}}]}""",
            null);
        await Task.Yield();
        yield return new RawStreamLine(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"jing\"}"}}]}}]}""",
            null);
        await Task.Yield();
        yield return new RawStreamLine(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""",
            null);
    }

    private static async IAsyncEnumerable<RawStreamLine> CreateStreamChunks(
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            var delta = i == 0 ? words[i] : " " + words[i];
            yield return new RawStreamLine(
                $"{{\"id\":\"chatcmpl-1\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{delta}\"}}}}]}}",
                null);
        }
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new RawStreamLine(
            """{"id":"chatcmpl-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            null);
    }
}
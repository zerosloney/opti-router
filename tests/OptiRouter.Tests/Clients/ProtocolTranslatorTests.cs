using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Clients.Protocols;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Protocols;

public sealed class AnthropicTranslatorsTests
{
    private static ModelEndpointOptions CreateEndpoint() => new()
    {
        Name = "claude",
        Id = "claude-3-5-sonnet",
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "sk-ant",
        Tier = ModelTier.Strong,
        Enabled = true
    };

    [Fact]
    public void BuildRequestBody_SplitsSystemAndMapsRoles()
    {
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", "You are a helpful assistant."),
                ChatMessage.FromText("user", "Hello"),
                ChatMessage.FromText("assistant", "Hi there"),
                ChatMessage.FromText("user", "How are you?")
            }
        };

        var body = AnthropicTranslators.BuildRequestBody(request, CreateEndpoint());
        using var doc = JsonDocument.Parse(body);

        Assert.Equal("claude-3-5-sonnet", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("You are a helpful assistant.", doc.RootElement.GetProperty("system").GetString());

        var messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("Hello", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void BuildRequestBody_TranslatesTools()
    {
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Use a tool") },
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["tools"] = JsonSerializer.Deserialize<JsonElement>(
                    """[{"type":"function","function":{"name":"get_weather","description":"Get weather","parameters":{"type":"object","properties":{"city":{"type":"string"}}}}}]""")
            }
        };

        var body = AnthropicTranslators.BuildRequestBody(request, CreateEndpoint());
        using var doc = JsonDocument.Parse(body);

        var tool = doc.RootElement.GetProperty("tools")[0];
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.Equal("Get weather", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildRequestBody_TranslatesAssistantToolCallsAndToolResults()
    {
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "weather?"),
                new ChatMessage
                {
                    Role = "assistant",
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["tool_calls"] = JsonSerializer.Deserialize<JsonElement>(
                            """[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Beijing\"}"}}]""")
                    }
                },
                new ChatMessage
                {
                    Role = "tool",
                    Content = JsonSerializer.SerializeToElement("Sunny, 24C"),
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["tool_call_id"] = JsonSerializer.SerializeToElement("call_1")
                    }
                }
            }
        };

        var body = AnthropicTranslators.BuildRequestBody(request, CreateEndpoint());
        using var doc = JsonDocument.Parse(body);

        var messages = doc.RootElement.GetProperty("messages");
        var assistantContent = messages[1].GetProperty("content")[0];
        Assert.Equal("tool_use", assistantContent.GetProperty("type").GetString());
        Assert.Equal("call_1", assistantContent.GetProperty("id").GetString());
        Assert.Equal("get_weather", assistantContent.GetProperty("name").GetString());
        Assert.Equal("Beijing", assistantContent.GetProperty("input").GetProperty("city").GetString());

        var toolResult = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("call_1", toolResult.GetProperty("tool_use_id").GetString());
        Assert.Equal("Sunny, 24C", toolResult.GetProperty("content").GetString());
    }

    [Fact]
    public void ToOpenAiJson_MapsTextAndUsage()
    {
        const string anthropic = """
            {"id":"msg_01","type":"message","role":"assistant","model":"claude-3-5-sonnet",
            "content":[{"type":"text","text":"Hello from Claude"}],
            "stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":5}}
            """;

        var openAi = AnthropicTranslators.ToOpenAiJson(anthropic);
        using var doc = JsonDocument.Parse(openAi);

        Assert.Equal("msg_01", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("Hello from Claude", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        var usage = doc.RootElement.GetProperty("usage");
        Assert.Equal(12, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(5, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(17, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void ToOpenAiJson_MapsToolUseToToolCalls()
    {
        const string anthropic = """
            {"id":"msg_02","type":"message","model":"claude-3-5-sonnet",
            "content":[{"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Beijing"}}],
            "stop_reason":"tool_use","usage":{"input_tokens":10,"output_tokens":8}}
            """;

        var openAi = AnthropicTranslators.ToOpenAiJson(anthropic);
        using var doc = JsonDocument.Parse(openAi);

        var message = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal("tool_calls", doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        var call = message.GetProperty("tool_calls")[0];
        Assert.Equal("toolu_1", call.GetProperty("id").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Contains("Beijing", call.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void TranslateStreamEvent_MapsDeltaAndDone()
    {
        string? delta = AnthropicTranslators.TranslateStreamEvent("content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""");
        Assert.NotNull(delta);
        using var deltaDoc = JsonDocument.Parse(delta);
        Assert.Equal("Hello", deltaDoc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        string? finish = AnthropicTranslators.TranslateStreamEvent("message_delta",
            """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""");
        Assert.NotNull(finish);
        using var finishDoc = JsonDocument.Parse(finish);
        Assert.Equal("stop", finishDoc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        Assert.Equal("[DONE]", AnthropicTranslators.TranslateStreamEvent("message_stop", """{"type":"message_stop"}"""));
        Assert.Null(AnthropicTranslators.TranslateStreamEvent("message_start", """{"type":"message_start"}"""));
    }

    [Fact]
    public void StreamEventTranslator_UpstreamToolUse_FullLifecycle()
    {
        // 上行方向：Anthropic SSE 的 tool_use 块（含参数分片）→ OpenAI tool_calls 增量。
        // 此前 input_json_delta 被跳过，流式工具参数全部丢失。
        var translator = new AnthropicTranslators.StreamEventTranslator();

        // text 块的 start 不产生输出
        Assert.Null(translator.Translate("content_block_start",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""));

        // tool_use 块 start → tool_calls 首片（index 映射：块 1 → tool 0）
        string? first = translator.Translate("content_block_start",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_weather"}}""");
        Assert.NotNull(first);
        using (var doc = JsonDocument.Parse(first!))
        {
            var tc = doc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
            Assert.Equal(0, tc.GetProperty("index").GetInt32());
            Assert.Equal("toolu_1", tc.GetProperty("id").GetString());
            Assert.Equal("get_weather", tc.GetProperty("function").GetProperty("name").GetString());
            Assert.Equal(string.Empty, tc.GetProperty("function").GetProperty("arguments").GetString());
        }

        // 参数分片逐段转发（两段拼成完整 JSON）
        string? frag1 = translator.Translate("content_block_delta",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""");
        Assert.NotNull(frag1);
        Assert.Equal("{\"city\":", JsonDocument.Parse(frag1!).RootElement
            .GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("arguments").GetString());

        string? frag2 = translator.Translate("content_block_delta",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Beijing\"}"}}""");
        Assert.NotNull(frag2);

        // text_delta 仍走静态路径
        string? text = translator.Translate("content_block_delta",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""");
        Assert.NotNull(text);
        Assert.Equal("hi", JsonDocument.Parse(text!).RootElement
            .GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        // stop_reason=tool_use → finish_reason=tool_calls
        string? finish = translator.Translate("message_delta",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""");
        Assert.NotNull(finish);
        Assert.Equal("tool_calls", JsonDocument.Parse(finish!).RootElement
            .GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        Assert.Equal("[DONE]", translator.Translate("message_stop", """{"type":"message_stop"}"""));
    }

    [Fact]
    public void FromAnthropicJson_AssistantTextAndToolUse_SingleMessage()
    {
        // text + tool_use 混合的 assistant 轮次 → 单条消息（content 与 tool_calls 同存），
        // 不再拆成两条连续 assistant。
        const string body = """
            {"model":"claude-3-5-sonnet","max_tokens":100,
            "messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":[
                {"type":"text","text":"Let me check."},
                {"type":"tool_use","id":"toolu_1","name":"get_weather","input":{"city":"Beijing"}}
              ]}
            ]}
            """;

        var request = AnthropicTranslators.FromAnthropicJson(body);

        Assert.Equal(2, request.Messages.Count);
        var assistant = request.Messages[1];
        Assert.Equal("assistant", assistant.Role);
        Assert.NotNull(assistant.ExtensionData);
        Assert.True(assistant.ExtensionData!.ContainsKey("tool_calls"));
        using var contentDoc = JsonDocument.Parse(assistant.Content!.Value.GetRawText());
        Assert.Equal("Let me check.", contentDoc.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public void AnthropicStreamTranslator_DownstreamToolCalls_FlushedAsToolUseBlocks()
    {
        // 下行方向：OpenAI 流式 tool_calls 分片 → 收尾输出完整 tool_use 块（Anthropic 客户端可执行工具）。
        var translator = new AnthropicTranslators.AnthropicStreamTranslator("claude-3-5-sonnet");

        translator.OnData("""{"choices":[{"index":0,"delta":{"role":"assistant","content":"查一下"}}]}""");
        translator.OnData("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]}}]}""");
        translator.OnData("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":\"Beijing\"}"}}]}}]}""");
        translator.OnData("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");

        var finalBlocks = translator.OnData("[DONE]");
        string all = string.Join(string.Empty, finalBlocks);

        Assert.Contains("\"type\":\"tool_use\",\"id\":\"call_1\",\"name\":\"get_weather\"", all, StringComparison.Ordinal);
        // partial_json 的完整参数值经解析断言——默认 JSON 编码器会把字符串内的引号转义为 \u0022
        var deltaEvent = finalBlocks.First(b => b.Contains("input_json_delta", StringComparison.Ordinal));
        int dataStart = deltaEvent.IndexOf("data: ", StringComparison.Ordinal) + "data: ".Length;
        using var deltaDoc = JsonDocument.Parse(deltaEvent[dataStart..].Trim());
        Assert.Equal("""{"city":"Beijing"}""",
            deltaDoc.RootElement.GetProperty("delta").GetProperty("partial_json").GetString());
        Assert.Contains("\"stop_reason\":\"tool_use\"", all, StringComparison.Ordinal);
        // 工具块序号在文本块（index 0）之后
        Assert.Contains("\"type\":\"content_block_start\",\"index\":1", all, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"content_block_stop\",\"index\":1", all, StringComparison.Ordinal);
        Assert.Contains("message_stop", all, StringComparison.Ordinal);
    }
}

public sealed class GeminiTranslatorsTests
{
    private static ModelEndpointOptions CreateEndpoint() => new()
    {
        Name = "gemini",
        Id = "gemini-1.5-pro",
        BaseUrl = "https://generativelanguage.googleapis.com",
        ApiKey = "g-key",
        Tier = ModelTier.Strong,
        Enabled = true
    };

    [Fact]
    public void BuildRequestBody_MapsRolesAndSystemInstruction()
    {
        var request = new ChatRequest
        {
            Model = "auto",
            MaxTokens = 256,
            Temperature = 0.5,
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("system", "Be concise."),
                ChatMessage.FromText("user", "Hello"),
                ChatMessage.FromText("assistant", "Hi")
            }
        };

        var body = GeminiTranslators.BuildRequestBody(request, CreateEndpoint());
        using var doc = JsonDocument.Parse(body);

        var contents = doc.RootElement.GetProperty("contents");
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("Be concise.", doc.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(256, doc.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(0.5, doc.RootElement.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void ToOpenAiJson_MapsCandidateAndUsage()
    {
        const string gemini = """
            {"candidates":[{"content":{"parts":[{"text":"Hello from Gemini"}],"role":"model"},"finishReason":"STOP"}],
            "usageMetadata":{"promptTokenCount":9,"candidatesTokenCount":4,"totalTokenCount":13}}
            """;

        var openAi = GeminiTranslators.ToOpenAiJson(gemini);
        using var doc = JsonDocument.Parse(openAi);

        Assert.Equal("Hello from Gemini", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(9, doc.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(4, doc.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
    }

    [Fact]
    public void TranslateStreamLine_MapsTextAndDone()
    {
        string? delta = GeminiTranslators.TranslateStreamLine(
            """{"candidates":[{"content":{"parts":[{"text":"Hello "}],"role":"model"}}]}""");
        Assert.NotNull(delta);
        using var deltaDoc = JsonDocument.Parse(delta);
        Assert.Equal("Hello ", deltaDoc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        Assert.Equal("[DONE]", GeminiTranslators.TranslateStreamLine("""{"done":true}"""));
    }

    [Fact]
    public void StreamLineTranslator_UpstreamFunctionCall_MapsToToolCalls()
    {
        // 上行方向：Gemini 流式 functionCall part → OpenAI tool_calls；此前被静默跳过。
        var translator = new GeminiTranslators.StreamLineTranslator();

        var lines = translator.Translate(
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"get_weather","args":{"city":"Beijing"}}}],"role":"model"},"index":0}]}""");
        Assert.Single(lines);
        using (var doc = JsonDocument.Parse(lines[0]))
        {
            var tc = doc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("tool_calls")[0];
            Assert.Equal(0, tc.GetProperty("index").GetInt32());
            Assert.Equal("call_0", tc.GetProperty("id").GetString());
            Assert.Equal("get_weather", tc.GetProperty("function").GetProperty("name").GetString());
            // arguments 是 JSON 编码字符串（OpenAI 契约），需二次解析
            Assert.Equal("Beijing", JsonSerializer.Deserialize<JsonElement>(
                tc.GetProperty("function").GetProperty("arguments").GetString()!)
                .GetProperty("city").GetString());
        }

        // 终结：出现工具调用后 done 行前补 finish_reason=tool_calls
        var done = translator.Translate("""{"done":true}""");
        Assert.Equal(2, done.Count);
        Assert.Equal("tool_calls", JsonDocument.Parse(done[0]).RootElement
            .GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal("[DONE]", done[1]);
    }

    [Fact]
    public void StreamLineTranslator_UpstreamMixedParts_EmitsToolThenText()
    {
        var translator = new GeminiTranslators.StreamLineTranslator();
        var lines = translator.Translate(
            """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"f","args":{}}},{"text":"hello"}],"role":"model"},"index":0}]}""");
        Assert.Equal(2, lines.Count);
        Assert.Contains("tool_calls", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"content\":\"hello\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void FromGeminiJson_ModelTextAndFunctionCall_SingleMessage()
    {
        const string body = """
            {"contents":[
              {"role":"user","parts":[{"text":"weather?"}]},
              {"role":"model","parts":[{"text":"Let me check."},{"functionCall":{"name":"get_weather","args":{"city":"Beijing"}}}]}
            ]}
            """;

        var request = GeminiTranslators.FromGeminiJson(body, "gemini-1.5-pro");

        Assert.Equal(2, request.Messages.Count);
        var assistant = request.Messages[1];
        Assert.Equal("assistant", assistant.Role);
        Assert.NotNull(assistant.ExtensionData);
        Assert.True(assistant.ExtensionData!.ContainsKey("tool_calls"));
        using var contentDoc = JsonDocument.Parse(assistant.Content!.Value.GetRawText());
        Assert.Equal("Let me check.", contentDoc.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public void GeminiStreamTranslator_DownstreamToolCalls_FlushedAsFunctionCallParts()
    {
        // 下行方向：OpenAI 流式 tool_calls 分片 → 收尾输出完整 functionCall part（Gemini 客户端可执行工具）。
        var translator = new GeminiTranslators.GeminiStreamTranslator("gemini-1.5-pro");

        translator.OnData("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"get_weather","arguments":"{\"city\":"}}]}}]}""");
        translator.OnData("""{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Beijing\"}"}}]}}]}""");
        translator.OnData("""{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");

        var blocks = translator.OnData("[DONE]");
        string all = string.Join(string.Empty, blocks);

        int idx = all.IndexOf("\"functionCall\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, "functionCall part missing");
        Assert.Contains("\"name\":\"get_weather\"", all, StringComparison.Ordinal);
        Assert.Contains("\"city\":\"Beijing\"", all, StringComparison.Ordinal);
        Assert.Contains("finishReason", all, StringComparison.Ordinal);
    }
}

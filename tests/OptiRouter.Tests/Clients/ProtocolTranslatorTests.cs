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
}

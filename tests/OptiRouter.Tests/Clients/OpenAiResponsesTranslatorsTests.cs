using System.Text.Json;
using OptiRouter.Clients;
using OptiRouter.Clients.Protocols;
using Xunit;

namespace OptiRouter.Tests.Protocols;

public sealed class OpenAiResponsesTranslatorsTests
{
    [Fact]
    public void FromResponsesJson_StringInput_MapsToUserMessage()
    {
        const string json = """
            {
              "model": "auto",
              "input": "Hello there"
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        Assert.Equal("auto", request.Model);
        Assert.Single(request.Messages);
        Assert.Equal("user", request.Messages[0].Role);
        Assert.Equal("Hello there", request.Messages[0].GetText());
    }

    [Fact]
    public void FromResponsesJson_Instructions_MapsToSystemMessage()
    {
        const string json = """
            {
              "input": "Hi",
              "instructions": "You are a helpful assistant."
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("You are a helpful assistant.", request.Messages[0].GetText());
        Assert.Equal("user", request.Messages[1].Role);
    }

    [Fact]
    public void FromResponsesJson_MaxOutputTokens_MapsToMaxTokens()
    {
        const string json = """
            {
              "input": "Hi",
              "max_output_tokens": 512,
              "temperature": 0.7
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        Assert.Equal(512, request.MaxTokens);
        Assert.Equal(0.7, request.Temperature);
    }

    [Fact]
    public void FromResponsesJson_MessageArray_PreservesRoles()
    {
        const string json = """
            {
              "input": [
                { "role": "user", "content": [ { "type": "input_text", "text": "Hello" } ] },
                { "role": "assistant", "content": [ { "type": "output_text", "text": "Hi" } ] },
                { "role": "user", "content": [ { "type": "input_text", "text": "How are you?" } ] }
              ]
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        Assert.Equal(3, request.Messages.Count);
        Assert.Equal("user", request.Messages[0].Role);
        Assert.Equal("Hello", request.Messages[0].GetText());
        Assert.Equal("assistant", request.Messages[1].Role);
        Assert.Equal("Hi", request.Messages[1].GetText());
        Assert.Equal("user", request.Messages[2].Role);
    }

    [Fact]
    public void FromResponsesJson_ToolMessages_PreserveToolCallId()
    {
        const string json = """
            {
              "input": [
                { "role": "user", "content": "weather?" },
                { "type": "function_call", "name": "get_weather", "arguments": "{\"city\":\"Beijing\"}", "call_id": "call_1" },
                { "type": "function_call_output", "call_id": "call_1", "output": "Sunny, 24C" }
              ]
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        Assert.Equal(3, request.Messages.Count);

        // function_call → assistant tool_calls
        Assert.Equal("assistant", request.Messages[1].Role);
        var toolCalls = request.Messages[1].ExtensionData!["tool_calls"];
        Assert.Equal("get_weather", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call_1", toolCalls[0].GetProperty("id").GetString());

        // function_call_output → tool 消息
        Assert.Equal("tool", request.Messages[2].Role);
        Assert.Equal("Sunny, 24C", request.Messages[2].GetText());
        Assert.Equal("call_1", request.Messages[2].ExtensionData!["tool_call_id"].GetString());
    }

    [Fact]
    public void FromResponsesJson_MissingInput_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => OpenAiResponsesTranslators.FromResponsesJson("""{ "model": "auto" }"""));
    }

    [Fact]
    public void FromResponsesJson_InvalidInputType_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => OpenAiResponsesTranslators.FromResponsesJson("""{ "input": 42 }"""));
    }

    [Fact]
    public void ToResponsesJson_MapsChatCompletionToResponseShape()
    {
        const string upstream = """
            {
              "id": "chatcmpl-123",
              "model": "deepseek/deepseek-chat",
              "choices": [
                {
                  "index": 0,
                  "message": { "role": "assistant", "content": "Hello from upstream" },
                  "finish_reason": "stop"
                }
              ],
              "usage": { "prompt_tokens": 5, "completion_tokens": 3, "total_tokens": 8 }
            }
            """;

        string responsesJson = OpenAiResponsesTranslators.ToResponsesJson(upstream, "resp_test");
        using var doc = JsonDocument.Parse(responsesJson);
        var root = doc.RootElement;

        Assert.Equal("resp_test", root.GetProperty("id").GetString());
        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("deepseek/deepseek-chat", root.GetProperty("model").GetString());
        // Responses 契约没有顶层 finish_reason
        Assert.False(root.TryGetProperty("finish_reason", out _));

        var output = root.GetProperty("output");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal("message", output[0].GetProperty("type").GetString());
        Assert.Equal("assistant", output[0].GetProperty("role").GetString());
        var content = output[0].GetProperty("content");
        Assert.Equal(1, content.GetArrayLength());
        Assert.Equal("output_text", content[0].GetProperty("type").GetString());
        Assert.Equal("Hello from upstream", content[0].GetProperty("text").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(5, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(3, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(8, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void ToResponsesJson_MapsToolCallsToFunctionCallOutput()
    {
        const string upstream = """
            {
              "id": "chatcmpl-456",
              "model": "m",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      { "id": "call_1", "type": "function", "function": { "name": "get_weather", "arguments": "{\"city\":\"Beijing\"}" } }
                    ]
                  },
                  "finish_reason": "tool_calls"
                }
              ]
            }
            """;

        string responsesJson = OpenAiResponsesTranslators.ToResponsesJson(upstream, "resp_test");
        using var doc = JsonDocument.Parse(responsesJson);
        var root = doc.RootElement;

        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("finish_reason", out _));

        var output = root.GetProperty("output");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal("function_call", output[0].GetProperty("type").GetString());
        Assert.Equal("get_weather", output[0].GetProperty("name").GetString());
        Assert.Equal("{\"city\":\"Beijing\"}", output[0].GetProperty("arguments").GetString());
    }

    [Fact]
    public void StreamTranslator_EmitsFramedOutputTextLifecycle()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_stream");

        var first = ParseFrames(translator.OnData(
            """{"model":"m","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}"""));

        // 首段文本之前必须补齐 created → output_item.added → content_part.added
        Assert.Equal(new[]
        {
            "response.created", "response.output_item.added", "response.content_part.added", "response.output_text.delta"
        }, first.Select(f => f.Type).ToArray());

        using (var deltaDoc = JsonDocument.Parse(first[^1].Data))
        {
            var delta = deltaDoc.RootElement;
            Assert.Equal("response.output_text.delta", delta.GetProperty("type").GetString());
            Assert.StartsWith("msg_", delta.GetProperty("item_id").GetString());
            Assert.Equal("Hello", delta.GetProperty("delta").GetString());
        }

        var closing = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""));
        Assert.Equal(new[]
        {
            "response.output_text.done", "response.output_item.done", "response.completed"
        }, closing.Select(f => f.Type).ToArray());

        using var doneDoc = JsonDocument.Parse(closing[^1].Data);
        var response = doneDoc.RootElement.GetProperty("response");
        Assert.Equal("resp_stream", response.GetProperty("id").GetString());
        Assert.Equal("completed", response.GetProperty("status").GetString());
        var output = response.GetProperty("output");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal("message", output[0].GetProperty("type").GetString());
        Assert.Equal("Hello", output[0].GetProperty("content")[0].GetProperty("text").GetString());

        // 收尾后不再输出任何事件
        Assert.Empty(translator.OnData("[DONE]"));
    }

    [Fact]
    public void StreamTranslator_LengthTruncation_MarksIncomplete()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_len");
        translator.OnData("""{"choices":[{"index":0,"delta":{"content":"partial"}}]}""");
        var closing = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"length"}]}"""));

        using var doc = JsonDocument.Parse(closing[^1].Data);
        var response = doc.RootElement.GetProperty("response");
        Assert.Equal("incomplete", response.GetProperty("status").GetString());
        Assert.Equal("max_output_tokens", response.GetProperty("incomplete_details").GetProperty("reason").GetString());
        Assert.False(response.TryGetProperty("finish_reason", out _));
    }

    [Fact]
    public void StreamTranslator_UsageInChunk_CarriedIntoCompleted()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_usage");
        translator.OnData("""{"choices":[{"index":0,"delta":{"content":"Hi"}}]}""");
        translator.OnData("""{"choices":[],"usage":{"prompt_tokens":4,"completion_tokens":2}}""");
        var closing = ParseFrames(translator.OnData("[DONE]"));

        using var doc = JsonDocument.Parse(closing[^1].Data);
        var usage = doc.RootElement.GetProperty("response").GetProperty("usage");
        Assert.Equal(4, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(6, usage.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public void StreamTranslator_EmptyStream_EmitsCreatedThenCompleted()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_empty");
        var frames = ParseFrames(translator.OnData("[DONE]"));

        Assert.Equal(new[] { "response.created", "response.completed" }, frames.Select(f => f.Type).ToArray());
        using var doc = JsonDocument.Parse(frames[^1].Data);
        var response = doc.RootElement.GetProperty("response");
        Assert.Equal("completed", response.GetProperty("status").GetString());
        Assert.Equal(0, response.GetProperty("output").GetArrayLength());
        // 上游未回 usage 时不编造 usage
        Assert.False(response.TryGetProperty("usage", out _));
    }

    [Fact]
    public void FromResponsesJson_FunctionCallRecord_PrefersCallIdOverRecordId()
    {
        const string json = """
            {
              "input": [
                { "type": "function_call", "id": "fc_9", "call_id": "call_9", "name": "get_weather", "arguments": "{}" },
                { "type": "function_call_output", "call_id": "call_9", "output": "ok" }
              ]
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        // tool_calls[].id 必须与 tool 消息的 tool_call_id 一致，否则严格上游 400
        var toolCallId = request.Messages[0].ExtensionData!["tool_calls"][0].GetProperty("id").GetString();
        Assert.Equal("call_9", toolCallId);
        Assert.Equal(toolCallId, request.Messages[1].ExtensionData!["tool_call_id"].GetString());
    }

    [Fact]
    public void FromResponsesJson_ToolOutputNonString_KeptAsText()
    {
        const string json = """
            {
              "input": [
                { "type": "function_call_output", "call_id": "c1", "output": { "temp": 24 } },
                { "type": "function_call_output", "call_id": "c2", "output": [ { "type": "input_text", "text": "sunny" } ] }
              ]
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);

        using var doc = JsonDocument.Parse(request.Messages[0].GetText());
        Assert.Equal(24, doc.RootElement.GetProperty("temp").GetInt32());
        Assert.Equal("sunny", request.Messages[1].GetText());
    }

    [Fact]
    public void FromResponsesJson_InputContentBlocks_NormalizedToChatContract()
    {
        const string json = """
            {
              "input": [
                { "role": "user", "content": [
                    { "type": "input_text", "text": "图里是什么" },
                    { "type": "input_image", "image_url": "https://example.com/a.png", "detail": "low" }
                ] }
              ]
            }
            """;

        var request = OpenAiResponsesTranslators.FromResponsesJson(json);
        var content = request.Messages[0].Content!.Value;

        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("图里是什么", content[0].GetProperty("text").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("https://example.com/a.png", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("low", content[1].GetProperty("image_url").GetProperty("detail").GetString());
        Assert.Equal("图里是什么", request.Messages[0].GetText());
    }

    [Fact]
    public void FromResponsesJson_SamplingFields_PassedThroughWithoutStreamOptions()
    {
        var request = OpenAiResponsesTranslators.FromResponsesJson(
            """{ "input": "Hi", "top_p": 0.8, "parallel_tool_calls": true, "stream_options": { "include_usage": true } }""");

        var extension = request.ExtensionData!;
        Assert.Equal(0.8, extension["top_p"].GetDouble());
        Assert.True(extension["parallel_tool_calls"].GetBoolean());
        // stream_options 是 chat 专有字段，Responses 请求体没有这个概念，不透传
        Assert.False(extension.ContainsKey("stream_options"));
    }

    [Fact]
    public void StreamTranslator_ToolCallFragments_EmitFunctionCallLifecycle()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_tool");

        // chat 流按 index 分片下发工具调用：id/name 在首片，arguments 是增量
        var first = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Bei"}}]}}]}"""));
        var second = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"jing\"}"}}]}}]}"""));
        var closing = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""));

        Assert.Equal(new[]
        {
            "response.created",
            "response.output_item.added",
            "response.function_call_arguments.delta",
            "response.function_call_arguments.delta",
            "response.function_call_arguments.done",
            "response.output_item.done",
            "response.completed"
        }, first.Concat(second).Concat(closing).Select(f => f.Type).ToArray());

        using (var addedDoc = JsonDocument.Parse(first[1].Data))
        {
            var item = addedDoc.RootElement.GetProperty("item");
            Assert.Equal("function_call", item.GetProperty("type").GetString());
            Assert.StartsWith("fc_", item.GetProperty("id").GetString());
            Assert.Equal("call_1", item.GetProperty("call_id").GetString());
            Assert.Equal("get_weather", item.GetProperty("name").GetString());
        }

        using (var deltaDoc = JsonDocument.Parse(first[2].Data))
        {
            Assert.Equal("""{"city":"Bei""", deltaDoc.RootElement.GetProperty("delta").GetString());
        }

        using (var deltaDoc = JsonDocument.Parse(second[0].Data))
        {
            Assert.Equal("jing\"}", deltaDoc.RootElement.GetProperty("delta").GetString());
        }

        using var doneDoc = JsonDocument.Parse(closing[^1].Data);
        var response = doneDoc.RootElement.GetProperty("response");
        Assert.Equal("completed", response.GetProperty("status").GetString());
        var output = response.GetProperty("output");
        Assert.Equal(1, output.GetArrayLength());
        Assert.Equal("function_call", output[0].GetProperty("type").GetString());
        Assert.Equal("call_1", output[0].GetProperty("call_id").GetString());
        using var args = JsonDocument.Parse(output[0].GetProperty("arguments").GetString()!);
        Assert.Equal("Beijing", args.RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public void StreamTranslator_ToolCallWithoutId_OpensItemAtClose()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_noid");

        // 上游没给 call_id：早期分片只累积，收尾时补开项并合成 id，保证 added/done/completed 一致
        Assert.Empty(translator.OnData(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"get_weather","arguments":"{}"}}]}}]}"""));
        var closing = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""));

        Assert.Equal(new[]
        {
            "response.created",
            "response.output_item.added",
            "response.function_call_arguments.delta",
            "response.function_call_arguments.done",
            "response.output_item.done",
            "response.completed"
        }, closing.Select(f => f.Type).ToArray());

        using var doc = JsonDocument.Parse(closing[^1].Data);
        var item = doc.RootElement.GetProperty("response").GetProperty("output")[0];
        Assert.StartsWith("call_", item.GetProperty("call_id").GetString());
        Assert.Equal("{}", item.GetProperty("arguments").GetString());
    }

    [Fact]
    public void StreamTranslator_TextThenToolCall_AssignsOutputIndexes()
    {
        var translator = new OpenAiResponsesTranslators.ResponsesStreamTranslator("resp_mixed");
        translator.OnData("""{"choices":[{"index":0,"delta":{"content":"查一下"}}]}""");
        var tool = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_7","function":{"name":"now","arguments":"{}"}}]}}]}"""));
        var closing = ParseFrames(translator.OnData(
            """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""));

        // 工具项的 added 帧在 OnData 返回列表中按类型定位（消息项已先行开项，added 不在首位）
        var addedFrame = tool.Single(f => f.Type == "response.output_item.added");
        using (var addedDoc = JsonDocument.Parse(addedFrame.Data))
        {
            Assert.Equal(1, addedDoc.RootElement.GetProperty("output_index").GetInt32());
            Assert.Equal("call_7", addedDoc.RootElement.GetProperty("item").GetProperty("call_id").GetString());
        }

        using var doneDoc = JsonDocument.Parse(closing[^1].Data);
        var output = doneDoc.RootElement.GetProperty("response").GetProperty("output");
        Assert.Equal(2, output.GetArrayLength());
        Assert.Equal("message", output[0].GetProperty("type").GetString());
        Assert.Equal("查一下", output[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("function_call", output[1].GetProperty("type").GetString());
    }

    /// <summary>
    /// 解析 SSE 帧：<c>event: {type}\ndata: {json}\n\n</c>。不合规的帧在这里就炸，
    /// 避免测试用子串断言放过不成帧的实现。
    /// </summary>
    private static IReadOnlyList<(string Type, string Data)> ParseFrames(IReadOnlyList<string> frames)
        => frames.Select(frame =>
        {
            Assert.EndsWith("\n\n", frame);
            var lines = frame.TrimEnd('\n').Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("event: ", lines[0]);
            Assert.StartsWith("data: ", lines[1]);
            return (Type: lines[0]["event: ".Length..], Data: lines[1]["data: ".Length..]);
        }).ToList();
}
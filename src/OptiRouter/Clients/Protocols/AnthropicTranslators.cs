using System.Text.Json;
using System.Text.Json.Nodes;
using OptiRouter.Configuration;

namespace OptiRouter.Clients.Protocols;

/// <summary>
/// Anthropic Messages API 请求/响应/流式事件翻译器。
/// 双向翻译 OpenAI 兼容契约与 Anthropic 原生协议（system 拆分、tool_use/tool_result、
/// SSE 事件流、usage 字段映射），对外保持 OpenAI JSON 不变。
/// </summary>
public static class AnthropicTranslators
{
    /// <summary>
    /// 把 OpenAI 兼容 ChatRequest 翻译为 Anthropic Messages API 请求体 JSON。
    /// </summary>
    public static string BuildRequestBody(ChatRequest request, ModelEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemParts = new List<string>();
        var messages = new List<object>();

        foreach (var msg in request.Messages ?? [])
        {
            switch (msg.Role.ToLowerInvariant())
            {
                case "system":
                    string systemText = msg.GetText();
                    if (!string.IsNullOrWhiteSpace(systemText))
                    {
                        systemParts.Add(systemText);
                    }
                    break;

                case "user":
                    messages.Add(new { role = "user", content = BuildUserContent(msg) });
                    break;

                case "assistant":
                    messages.Add(new { role = "assistant", content = BuildAssistantContent(msg) });
                    break;

                case "tool":
                    // Anthropic 规范：tool_result 必须包裹在 user 消息中。
                    messages.Add(new { role = "user", content = new object[] { BuildToolResult(msg) } });
                    break;
            }
        }

        var body = new Dictionary<string, object?>
        {
            // UpstreamModelId：Id 留空（仅配置 Name）时回退 Name，与 OpenAI 客户端语义一致
            ["model"] = endpoint.UpstreamModelId,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["messages"] = messages
        };

        // Anthropic 流式开关在请求体内（仅 Accept 头不够）：stream=true 才会返回 SSE
        if (request.Stream)
        {
            body["stream"] = true;
        }
        if (request.Temperature is not null)
        {
            body["temperature"] = request.Temperature;
        }
        // OpenAI 的 top_p / stop 经 ExtensionData 透传，映射到 Anthropic 同义字段
        if (request.ExtensionData is not null)
        {
            if (request.ExtensionData.TryGetValue("top_p", out var topP) && topP.ValueKind == JsonValueKind.Number)
            {
                body["top_p"] = topP.GetDouble();
            }
            if (request.ExtensionData.TryGetValue("stop", out var stop))
            {
                // OpenAI stop 允许 string 或 string[]；Anthropic stop_sequences 只收数组
                if (stop.ValueKind == JsonValueKind.String)
                {
                    body["stop_sequences"] = new[] { stop.GetString() };
                }
                else if (stop.ValueKind == JsonValueKind.Array)
                {
                    body["stop_sequences"] = JsonSerializer.SerializeToElement(stop);
                }
            }
        }

        if (systemParts.Count > 0)
        {
            body["system"] = systemParts.Count == 1 ? systemParts[0] : string.Join("\n\n", systemParts);
        }

        // tools：OpenAI 格式 {type:"function", function:{name, description, parameters}} → Anthropic {name, description, input_schema}
        if (request.ExtensionData is not null
            && request.ExtensionData.TryGetValue("tools", out var toolsEl)
            && toolsEl.ValueKind == JsonValueKind.Array
            && toolsEl.GetArrayLength() > 0)
        {
            var tools = new List<object>();
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (!tool.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                tools.Add(new
                {
                    name,
                    description = fn.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                    input_schema = fn.TryGetProperty("parameters", out var paramsEl)
                        ? paramsEl
                        : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                });
            }
            if (tools.Count > 0)
            {
                body["tools"] = tools;
            }
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// 把 Anthropic 非流式响应 JSON 翻译为 OpenAI 兼容响应 JSON。
    /// </summary>
    public static string ToOpenAiJson(string anthropicBody)
    {
        using var doc = JsonDocument.Parse(anthropicBody);
        var root = doc.RootElement;

        string id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        string model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? string.Empty : string.Empty;

        // content：拼接 text，收集 tool_use
        var textParts = new List<string>();
        var toolCalls = new List<object>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl)) continue;
                string type = typeEl.GetString() ?? string.Empty;
                if (type == "text" && block.TryGetProperty("text", out var textEl))
                {
                    textParts.Add(textEl.GetString() ?? string.Empty);
                }
                else if (type == "tool_use")
                {
                    string toolId = block.TryGetProperty("id", out var toolIdEl) ? toolIdEl.GetString() ?? string.Empty : string.Empty;
                    string toolName = block.TryGetProperty("name", out var toolNameEl) ? toolNameEl.GetString() ?? string.Empty : string.Empty;
                    var input = block.TryGetProperty("input", out var inputEl) ? inputEl : default;
                    toolCalls.Add(new
                    {
                        id = toolId,
                        type = "function",
                        function = new
                        {
                            name = toolName,
                            arguments = input.ValueKind == JsonValueKind.Object || input.ValueKind == JsonValueKind.Array
                                ? input.GetRawText()
                                : "{}"
                        }
                    });
                }
            }
        }

        // usage：input_tokens → prompt_tokens，output_tokens → completion_tokens
        int promptTokens = 0;
        int completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("input_tokens", out var pt) ? pt.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("output_tokens", out var ct) ? ct.GetInt32() : 0;
        }

        // stop_reason → finish_reason
        string finishReason = "stop";
        if (root.TryGetProperty("stop_reason", out var stopReasonEl))
        {
            finishReason = stopReasonEl.GetString() switch
            {
                "tool_use" => "tool_calls",
                "max_tokens" => "length",
                "stop_sequence" => "stop",
                _ => "stop"
            };
        }

        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = textParts.Count > 0 ? string.Join(string.Empty, textParts) : null
        };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        return JsonSerializer.Serialize(new
        {
            id = string.IsNullOrEmpty(id) ? $"msg-{Guid.NewGuid():N}" : id,
            model,
            choices = new object[]
            {
                new
                {
                    index = 0,
                    message,
                    finish_reason = finishReason
                }
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens
            }
        });
    }

    /// <summary>
    /// 把 Anthropic 流式 SSE 事件翻译为 OpenAI 兼容 data 行；返回 null 表示跳过该事件。
    /// </summary>
    /// <param name="eventType">Anthropic 事件类型（message_start / content_block_delta 等）。</param>
    /// <param name="dataJson">事件 data JSON。</param>
    public static string? TranslateStreamEvent(string eventType, string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;

            switch (eventType)
            {
                case "content_block_delta":
                    if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }
                    string deltaType = delta.TryGetProperty("type", out var deltaTypeEl) ? deltaTypeEl.GetString() ?? string.Empty : string.Empty;
                    if (deltaType == "text_delta"
                        && delta.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            choices = new object[]
                            {
                                new { index = 0, delta = new { content = textEl.GetString() } }
                            }
                        });
                    }
                    // input_json_delta（工具参数增量）在流式下跳过，工具调用结果以非流式形态返回。
                    return null;

                case "message_delta":
                    string? finishReason = null;
                    if (root.TryGetProperty("delta", out var msgDelta)
                        && msgDelta.TryGetProperty("stop_reason", out var stopReasonEl))
                    {
                        finishReason = stopReasonEl.GetString() switch
                        {
                            "tool_use" => "tool_calls",
                            "max_tokens" => "length",
                            _ => "stop"
                        };
                    }
                    return JsonSerializer.Serialize(new
                    {
                        choices = new object[]
                        {
                            new { index = 0, delta = new { }, finish_reason = finishReason ?? "stop" }
                        }
                    });

                case "message_stop":
                    return "[DONE]";

                default:
                    // message_start / content_block_start / ping 等事件不产生下游可见内容。
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把 Anthropic Messages API 请求体 JSON 翻译为 OpenAI 兼容 ChatRequest（下游入口方向）。
    /// system 拆分、tool_use/tool_result 块、image source（base64/url）与 tools/tool_choice/stop_sequences 均映射。
    /// </summary>
    public static ChatRequest FromAnthropicJson(string anthropicBody)
    {
        using var doc = JsonDocument.Parse(anthropicBody);
        var root = doc.RootElement;

        string model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString() ?? string.Empty
            : string.Empty;
        var messages = new List<ChatMessage>();

        // system：string 或 text blocks 数组
        if (root.TryGetProperty("system", out var systemEl))
        {
            string? systemText = ExtractTextLike(systemEl);
            if (!string.IsNullOrWhiteSpace(systemText))
            {
                messages.Add(ChatMessage.FromText("system", systemText));
            }
        }

        if (root.TryGetProperty("messages", out var msgsEl) && msgsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in msgsEl.EnumerateArray())
            {
                string role = msg.TryGetProperty("role", out var roleEl) ? roleEl.GetString() ?? "user" : "user";
                bool isAssistant = role.Equals("assistant", StringComparison.OrdinalIgnoreCase);

                if (!msg.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.String)
                {
                    string text = content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : string.Empty;
                    messages.Add(new ChatMessage
                    {
                        Role = isAssistant ? "assistant" : "user",
                        Content = JsonSerializer.SerializeToElement(text)
                    });
                    continue;
                }

                if (content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                // 块数组：text/image 归入正文；tool_use → assistant tool_calls；tool_result → tool 消息
                var contentBlocks = new List<object>();
                var toolCalls = new List<object>();
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    string type = block.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;

                    if (type == "text" && block.TryGetProperty("text", out var textEl))
                    {
                        contentBlocks.Add(new { type = "text", text = textEl.GetString() });
                    }
                    else if (type == "image" && block.TryGetProperty("source", out var srcEl) && srcEl.ValueKind == JsonValueKind.Object)
                    {
                        // Anthropic image source → OpenAI image_url（base64 → data URL，url 直传）
                        string srcType = srcEl.TryGetProperty("type", out var stEl) ? stEl.GetString() ?? string.Empty : string.Empty;
                        string url = srcType == "base64"
                            && srcEl.TryGetProperty("media_type", out var mtEl)
                            && srcEl.TryGetProperty("data", out var dataEl)
                            ? $"data:{mtEl.GetString()};base64,{dataEl.GetString()}"
                            : srcEl.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? string.Empty : string.Empty;
                        if (url.Length > 0)
                        {
                            contentBlocks.Add(new { type = "image_url", image_url = new { url } });
                        }
                    }
                    else if (type == "tool_use" && isAssistant)
                    {
                        toolCalls.Add(new
                        {
                            id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
                            type = "function",
                            function = new
                            {
                                name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                arguments = block.TryGetProperty("input", out var inputEl)
                                    && (inputEl.ValueKind == JsonValueKind.Object || inputEl.ValueKind == JsonValueKind.Array)
                                    ? inputEl.GetRawText()
                                    : "{}"
                            }
                        });
                    }
                    else if (type == "tool_result")
                    {
                        // Anthropic 规范：tool_result 只出现在 user 消息中 → OpenAI tool 消息
                        string toolUseId = block.TryGetProperty("tool_use_id", out var tuidEl) ? tuidEl.GetString() ?? string.Empty : string.Empty;
                        string resultText = block.TryGetProperty("content", out var rcEl) ? ExtractTextLike(rcEl) ?? string.Empty : string.Empty;
                        messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            Content = JsonSerializer.SerializeToElement(resultText),
                            ExtensionData = new Dictionary<string, JsonElement>
                            {
                                ["tool_call_id"] = JsonSerializer.SerializeToElement(toolUseId)
                            }
                        });
                    }
                }

                if (contentBlocks.Count > 0 || toolCalls.Count == 0)
                {
                    messages.Add(new ChatMessage
                    {
                        Role = isAssistant ? "assistant" : "user",
                        Content = contentBlocks.Count == 0
                            ? JsonSerializer.SerializeToElement(string.Empty)
                            : JsonSerializer.SerializeToElement(contentBlocks)
                    });
                }

                if (toolCalls.Count > 0)
                {
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = JsonSerializer.SerializeToElement(string.Empty),
                        ExtensionData = new Dictionary<string, JsonElement>
                        {
                            ["tool_calls"] = JsonSerializer.SerializeToElement(toolCalls)
                        }
                    });
                }
            }
        }

        var extension = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array && toolsEl.GetArrayLength() > 0)
        {
            // Anthropic {name, description, input_schema} → OpenAI {type:"function", function:{...}}
            var tools = new List<object>();
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object) continue;
                string name = tool.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                if (name.Length == 0) continue;
                tools.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name,
                        description = tool.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                        parameters = tool.TryGetProperty("input_schema", out var schemaEl)
                            ? schemaEl
                            : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                    }
                });
            }
            if (tools.Count > 0)
            {
                extension["tools"] = JsonSerializer.SerializeToElement(tools);
            }
        }

        // tool_choice：auto→auto，any→required，tool→具名 function
        if (root.TryGetProperty("tool_choice", out var tcEl) && tcEl.ValueKind == JsonValueKind.Object
            && tcEl.TryGetProperty("type", out var tcTypeEl))
        {
            string tcType = tcTypeEl.GetString() ?? "auto";
            object? choice = tcType switch
            {
                "auto" => "auto",
                "any" => "required",
                "tool" => tcEl.TryGetProperty("name", out var tcNameEl)
                    ? new { type = "function", function = new { name = tcNameEl.GetString() } }
                    : null,
                _ => null
            };
            if (choice is not null)
            {
                extension["tool_choice"] = JsonSerializer.SerializeToElement(choice);
            }
        }

        if (root.TryGetProperty("stop_sequences", out var stopEl) && stopEl.ValueKind == JsonValueKind.Array)
        {
            extension["stop"] = stopEl.Clone();
        }
        if (root.TryGetProperty("top_p", out var topPEl) && topPEl.ValueKind == JsonValueKind.Number)
        {
            extension["top_p"] = topPEl.Clone();
        }

        return new ChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = root.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.True,
            Temperature = root.TryGetProperty("temperature", out var tempEl) && tempEl.ValueKind == JsonValueKind.Number
                ? tempEl.GetDouble()
                : null,
            MaxTokens = root.TryGetProperty("max_tokens", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number
                ? maxEl.GetInt32()
                : null,
            ExtensionData = extension.Count > 0 ? extension : null
        };
    }

    /// <summary>
    /// 把 OpenAI 兼容非流式响应 JSON 翻译为 Anthropic Messages 响应 JSON（下游出口方向）。
    /// </summary>
    public static string ToAnthropicJson(string openAiBody)
    {
        using var doc = JsonDocument.Parse(openAiBody);
        var root = doc.RootElement;

        string id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        string model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? string.Empty : string.Empty;

        var contentBlocks = new List<object>();
        string stopReason = "end_turn";

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String
                    && (contentEl.GetString() ?? string.Empty).Length > 0)
                {
                    contentBlocks.Add(new { type = "text", text = contentEl.GetString() });
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
                        string name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                        if (name.Length == 0) continue;
                        JsonElement input = default;
                        if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        {
                            try
                            {
                                input = JsonDocument.Parse(argsEl.GetString() ?? "{}").RootElement.Clone();
                            }
                            catch (JsonException)
                            {
                                input = default;
                            }
                        }
                        contentBlocks.Add(new
                        {
                            type = "tool_use",
                            id = call.TryGetProperty("id", out var callIdEl) ? callIdEl.GetString() ?? string.Empty : string.Empty,
                            name,
                            input = input.ValueKind == JsonValueKind.Object ? input : JsonSerializer.SerializeToElement(new { })
                        });
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
            {
                stopReason = frEl.GetString() switch
                {
                    "tool_calls" => "tool_use",
                    "length" => "max_tokens",
                    _ => "end_turn"
                };
            }
        }

        int inputTokens = 0;
        int outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0;
            outputTokens = usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0;
        }

        return JsonSerializer.Serialize(new
        {
            id = id.Length > 0 ? id : $"msg_{Guid.NewGuid():N}",
            type = "message",
            role = "assistant",
            model,
            content = contentBlocks,
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens }
        });
    }

    /// <summary>
    /// 有状态流式翻译器：把 OpenAI 兼容 SSE data 行翻译为 Anthropic 事件序列
    /// （message_start → content_block_start/delta/stop → message_delta → message_stop）。
    /// 每个 OnData 返回 0..n 个完整 SSE 块（含 event: 行与空行分隔）。
    /// </summary>
    public sealed class AnthropicStreamTranslator
    {
        private readonly string _fallbackModel;
        private string _model;
        private bool _started;
        private bool _blockClosed;
        private string _stopReason = "end_turn";
        private ChatUsage? _usage;

        /// <param name="requestedModel">请求的模型名（chunk 未携带 model 时的回退值）。</param>
        public AnthropicStreamTranslator(string requestedModel)
        {
            _fallbackModel = requestedModel;
            _model = requestedModel;
        }

        /// <summary>翻译一条 OpenAI data 行（JSON 或 [DONE]）。</summary>
        public IReadOnlyList<string> OnData(string data)
        {
            var blocks = new List<string>();
            if (data == "[DONE]")
            {
                EnsureStarted(blocks);
                CloseBlock(blocks);
                blocks.Add(Event("message_delta", JsonSerializer.Serialize(new
                {
                    type = "message_delta",
                    delta = new { stop_reason = _stopReason, stop_sequence = (string?)null },
                    usage = new { output_tokens = _usage?.CompletionTokens ?? 0 }
                })));
                blocks.Add(Event("message_stop", "{\"type\":\"message_stop\"}"));
                return blocks;
            }

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                {
                    _model = modelEl.GetString() ?? _model;
                }
                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    _usage = new ChatUsage
                    {
                        PromptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0,
                        CompletionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0
                    };
                }

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return blocks;
                }

                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    bool hasContent = delta.TryGetProperty("content", out var contentEl)
                        && contentEl.ValueKind == JsonValueKind.String
                        && (contentEl.GetString() ?? string.Empty).Length > 0;

                    if (hasContent || delta.TryGetProperty("role", out _))
                    {
                        EnsureStarted(blocks);
                    }
                    if (hasContent)
                    {
                        blocks.Add(Event("content_block_delta", JsonSerializer.Serialize(new
                        {
                            type = "content_block_delta",
                            index = 0,
                            delta = new { type = "text_delta", text = contentEl.GetString() }
                        })));
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                {
                    _stopReason = frEl.GetString() switch
                    {
                        "tool_calls" => "tool_use",
                        "length" => "max_tokens",
                        _ => "end_turn"
                    };
                }
            }
            catch (JsonException)
            {
                // 非 JSON data 行跳过（与上游方向 TranslateStreamEvent 行为一致）
            }

            return blocks;
        }

        /// <summary>流中途失败：输出 Anthropic error 事件。</summary>
        public static string OnError(string type, string message)
            => Event("error", JsonSerializer.Serialize(new { type = "error", error = new { type, message } }));

        private void EnsureStarted(List<string> blocks)
        {
            if (_started) return;
            _started = true;
            blocks.Add(Event("message_start", JsonSerializer.Serialize(new
            {
                type = "message_start",
                message = new
                {
                    id = $"msg_{Guid.NewGuid():N}",
                    type = "message",
                    role = "assistant",
                    model = _model.Length > 0 ? _model : _fallbackModel,
                    content = Array.Empty<object>(),
                    stop_reason = (string?)null,
                    stop_sequence = (string?)null,
                    usage = new { input_tokens = _usage?.PromptTokens ?? 0, output_tokens = 0 }
                }
            })));
            blocks.Add(Event("content_block_start", JsonSerializer.Serialize(new
            {
                type = "content_block_start",
                index = 0,
                content_block = new { type = "text", text = string.Empty }
            })));
        }

        private void CloseBlock(List<string> blocks)
        {
            if (!_started || _blockClosed) return;
            _blockClosed = true;
            blocks.Add(Event("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}"));
        }

        private static string Event(string eventType, string dataJson)
            => $"event: {eventType}\ndata: {dataJson}\n\n";
    }

    /// <summary>
    /// 抽取 string 或 text blocks 数组的合并文本；其他形态返回 null。
    /// </summary>
    private static string? ExtractTextLike(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        if (el.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var block in el.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "text"
                    && block.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    parts.Add(textEl.GetString() ?? string.Empty);
                }
            }
            return parts.Count > 0 ? string.Join(string.Empty, parts) : null;
        }
        return null;
    }

    private static object BuildUserContent(ChatMessage msg)
    {
        if (msg.Content is not { } content)
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        // 多模态数组：text / image_url → Anthropic text / image
        if (content.ValueKind == JsonValueKind.Array)
        {
            var blocks = new List<object>();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
                if (type == "text" && item.TryGetProperty("text", out var textEl))
                {
                    blocks.Add(new { type = "text", text = textEl.GetString() });
                }
                else if (type == "image_url"
                         && item.TryGetProperty("image_url", out var imageEl)
                         && imageEl.TryGetProperty("url", out var urlEl))
                {
                    string url = urlEl.GetString() ?? string.Empty;
                    // data URL（下游入口的 base64 图像）→ Anthropic base64 source；
                    // 直接以 url source 发 data URL 不被 Anthropic 接受（vision 断链）。
                    if (url.StartsWith("data:", StringComparison.Ordinal))
                    {
                        int semi = url.IndexOf(';');
                        int comma = url.IndexOf(',');
                        if (semi > 5 && comma > semi)
                        {
                            blocks.Add(new
                            {
                                type = "image",
                                source = new
                                {
                                    type = "base64",
                                    media_type = url["data:".Length..semi],
                                    data = url[(comma + 1)..]
                                }
                            });
                        }
                    }
                    else if (url.Length > 0)
                    {
                        blocks.Add(new { type = "image", source = new { type = "url", url } });
                    }
                }
            }
            return blocks;
        }

        return string.Empty;
    }

    private static object? BuildAssistantContent(ChatMessage msg)
    {
        var blocks = new List<object>();

        if (msg.Content is { } content && content.ValueKind == JsonValueKind.String)
        {
            string? text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                blocks.Add(new { type = "text", text });
            }
        }

        // OpenAI tool_calls（ExtensionData）→ Anthropic tool_use 块
        if (msg.ExtensionData is not null
            && msg.ExtensionData.TryGetValue("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                string id = call.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                string name = string.Empty;
                JsonElement arguments = default;
                if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    if (fn.TryGetProperty("arguments", out var argsEl))
                    {
                        if (argsEl.ValueKind == JsonValueKind.String)
                        {
                            try
                            {
                                arguments = JsonDocument.Parse(argsEl.GetString() ?? "{}").RootElement.Clone();
                            }
                            catch (JsonException)
                            {
                                arguments = default;
                            }
                        }
                        else if (argsEl.ValueKind == JsonValueKind.Object)
                        {
                            arguments = argsEl.Clone();
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(name)) continue;

                blocks.Add(new
                {
                    type = "tool_use",
                    id,
                    name,
                    input = arguments.ValueKind == JsonValueKind.Object ? arguments : JsonSerializer.SerializeToElement(new { })
                });
            }
        }

        return blocks.Count > 0 ? blocks : null;
    }

    private static object BuildToolResult(ChatMessage msg)
    {
        string toolUseId = msg.ExtensionData is not null
            && msg.ExtensionData.TryGetValue("tool_call_id", out var idEl)
            ? idEl.GetString() ?? string.Empty
            : string.Empty;

        string content = msg.GetText();

        return new
        {
            type = "tool_result",
            tool_use_id = toolUseId,
            content
        };
    }
}

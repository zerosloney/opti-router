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
            ["model"] = endpoint.Id,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["messages"] = messages
        };

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
                    blocks.Add(new { type = "image", source = new { type = "url", url = urlEl.GetString() } });
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

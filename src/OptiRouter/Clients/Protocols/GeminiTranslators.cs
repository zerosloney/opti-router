using System.Text.Json;
using OptiRouter.Configuration;

namespace OptiRouter.Clients.Protocols;

/// <summary>
/// Google Gemini generateContent API 请求/响应/流式翻译器。
/// 双向翻译 OpenAI 兼容契约与 Gemini 原生协议（contents/parts、systemInstruction、
/// functionCall/functionResponse、usageMetadata），对外保持 OpenAI JSON 不变。
/// </summary>
public static class GeminiTranslators
{
    /// <summary>
    /// 把 OpenAI 兼容 ChatRequest 翻译为 Gemini generateContent 请求体 JSON。
    /// </summary>
    public static string BuildRequestBody(ChatRequest request, ModelEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemParts = new List<string>();
        var contents = new List<object>();

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
                    contents.Add(new { role = "user", parts = BuildParts(msg, isToolResult: false) });
                    break;

                case "assistant":
                    contents.Add(new { role = "model", parts = BuildParts(msg, isToolResult: false) });
                    break;

                case "tool":
                    contents.Add(new { role = "user", parts = BuildParts(msg, isToolResult: true) });
                    break;
            }
        }

        var body = new Dictionary<string, object?>
        {
            ["contents"] = contents
        };

        if (systemParts.Count > 0)
        {
            body["systemInstruction"] = new { parts = systemParts.Select(t => (object)new { text = t }).ToList() };
        }

        var generationConfig = new Dictionary<string, object?>();
        if (request.MaxTokens is > 0)
        {
            generationConfig["maxOutputTokens"] = request.MaxTokens;
        }
        if (request.Temperature is not null)
        {
            generationConfig["temperature"] = request.Temperature;
        }
        if (generationConfig.Count > 0)
        {
            body["generationConfig"] = generationConfig;
        }

        // tools：OpenAI 格式 → Gemini functionDeclarations
        if (request.ExtensionData is not null
            && request.ExtensionData.TryGetValue("tools", out var toolsEl)
            && toolsEl.ValueKind == JsonValueKind.Array
            && toolsEl.GetArrayLength() > 0)
        {
            var declarations = new List<object>();
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (!tool.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;

                declarations.Add(new
                {
                    name,
                    description = fn.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                    parameters = fn.TryGetProperty("parameters", out var paramsEl)
                        ? paramsEl
                        : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                });
            }
            if (declarations.Count > 0)
            {
                body["tools"] = new object[] { new { functionDeclarations = declarations } };
            }
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// 把 Gemini 非流式响应 JSON 翻译为 OpenAI 兼容响应 JSON。
    /// </summary>
    public static string ToOpenAiJson(string geminiBody)
    {
        using var doc = JsonDocument.Parse(geminiBody);
        var root = doc.RootElement;

        string? text = null;
        var toolCalls = new List<object>();
        string finishReason = "stop";

        if (root.TryGetProperty("candidates", out var candidates)
            && candidates.ValueKind == JsonValueKind.Array
            && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];

            if (candidate.TryGetProperty("finishReason", out var frEl))
            {
                finishReason = frEl.GetString() switch
                {
                    "MAX_TOKENS" => "length",
                    "SAFETY" => "content_filter",
                    "RECITATION" => "content_filter",
                    _ => "stop"
                };
            }

            if (candidate.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                var textParts = new List<string>();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textEl))
                    {
                        textParts.Add(textEl.GetString() ?? string.Empty);
                    }
                    else if (part.TryGetProperty("functionCall", out var fnCall)
                             && fnCall.ValueKind == JsonValueKind.Object)
                    {
                        string name = fnCall.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                        var args = fnCall.TryGetProperty("args", out var argsEl) ? argsEl : default;
                        toolCalls.Add(new
                        {
                            id = $"call-{Guid.NewGuid():N}"[..12],
                            type = "function",
                            function = new
                            {
                                name,
                                arguments = args.ValueKind == JsonValueKind.Object
                                    ? args.GetRawText()
                                    : "{}"
                            }
                        });
                    }
                }
                if (textParts.Count > 0)
                {
                    text = string.Join(string.Empty, textParts);
                }
            }
        }

        // usageMetadata → usage
        int promptTokens = 0;
        int completionTokens = 0;
        if (root.TryGetProperty("usageMetadata", out var usageMeta))
        {
            promptTokens = usageMeta.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0;
            completionTokens = usageMeta.TryGetProperty("candidatesTokenCount", out var ct) ? ct.GetInt32() : 0;
        }

        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = text
        };
        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        return JsonSerializer.Serialize(new
        {
            id = $"gemini-{Guid.NewGuid():N}",
            model = root.TryGetProperty("modelVersion", out var mv) ? mv.GetString() : string.Empty,
            choices = new object[]
            {
                new { index = 0, message, finish_reason = finishReason }
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
    /// 把 Gemini 流式 data 行（与 OpenAI 同为 <c>data: {...}</c>）翻译为 OpenAI delta 行；
    /// 返回 null 表示跳过。
    /// </summary>
    public static string? TranslateStreamLine(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;

            // 结束标记
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("done", out var doneEl) && doneEl.ValueKind == JsonValueKind.True)
            {
                return "[DONE]";
            }

            if (!root.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                return null;
            }

            var candidate = candidates[0];
            string? text = null;
            if (candidate.TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        text = textEl.GetString();
                        break;
                    }
                }
            }

            if (text is null)
            {
                return null;
            }

            return JsonSerializer.Serialize(new
            {
                choices = new object[]
                {
                    new { index = 0, delta = new { content = text } }
                }
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<object> BuildParts(ChatMessage msg, bool isToolResult)
    {
        var parts = new List<object>();

        if (isToolResult)
        {
            // OpenAI tool 消息 → Gemini functionResponse
            string name = msg.ExtensionData is not null
                && msg.ExtensionData.TryGetValue("tool_call_id", out var idEl)
                ? idEl.GetString() ?? string.Empty
                : string.Empty;
            string content = msg.GetText();
            parts.Add(new
            {
                functionResponse = new
                {
                    name,
                    response = new { content, result = content }
                }
            });
            return parts;
        }

        if (msg.Content is { } contentEl)
        {
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                parts.Add(new { text = contentEl.GetString() ?? string.Empty });
            }
            else if (contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    string type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;
                    if (type == "text" && item.TryGetProperty("text", out var textEl))
                    {
                        parts.Add(new { text = textEl.GetString() });
                    }
                }
            }
        }

        // assistant 消息的 OpenAI tool_calls → Gemini functionCall（functionResponse 由 tool 消息回填）
        if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            && msg.ExtensionData is not null
            && msg.ExtensionData.TryGetValue("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
                string name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var args = default(JsonElement);
                if (fn.TryGetProperty("arguments", out var argsEl))
                {
                    if (argsEl.ValueKind == JsonValueKind.String)
                    {
                        try
                        {
                            args = JsonDocument.Parse(argsEl.GetString() ?? "{}").RootElement.Clone();
                        }
                        catch (JsonException)
                        {
                            args = default;
                        }
                    }
                    else if (argsEl.ValueKind == JsonValueKind.Object)
                    {
                        args = argsEl.Clone();
                    }
                }
                parts.Add(new
                {
                    functionCall = new
                    {
                        name,
                        args = args.ValueKind == JsonValueKind.Object ? args : JsonSerializer.SerializeToElement(new { })
                    }
                });
            }
        }

        return parts;
    }
}

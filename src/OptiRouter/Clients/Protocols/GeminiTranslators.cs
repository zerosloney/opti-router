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

    /// <summary>
    /// 把 Gemini generateContent 请求体 JSON 翻译为 OpenAI 兼容 ChatRequest（下游入口方向）。
    /// contents/parts、systemInstruction、functionCall/functionResponse 与 generationConfig 均映射。
    /// </summary>
    /// <param name="geminiBody">Gemini 请求体 JSON（不含 stream 字段，流式由端点路由决定）。</param>
    /// <param name="modelFromPath">URL 路径中的模型名（Gemini 的 model 在路径而非 body）。</param>
    public static ChatRequest FromGeminiJson(string geminiBody, string modelFromPath)
    {
        using var doc = JsonDocument.Parse(geminiBody);
        var root = doc.RootElement;

        var messages = new List<ChatMessage>();

        // systemInstruction → system 消息
        if (root.TryGetProperty("systemInstruction", out var sysEl)
            && sysEl.ValueKind == JsonValueKind.Object
            && sysEl.TryGetProperty("parts", out var sysParts)
            && sysParts.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var part in sysParts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    texts.Add(textEl.GetString() ?? string.Empty);
                }
            }
            string systemText = string.Join(string.Empty, texts);
            if (systemText.Length > 0)
            {
                messages.Add(ChatMessage.FromText("system", systemText));
            }
        }

        if (root.TryGetProperty("contents", out var contentsEl) && contentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var content in contentsEl.EnumerateArray())
            {
                if (content.ValueKind != JsonValueKind.Object) continue;
                bool isModel = content.TryGetProperty("role", out var roleEl)
                    && string.Equals(roleEl.GetString(), "model", StringComparison.OrdinalIgnoreCase);

                if (!content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var textParts = new List<object>();
                var toolCalls = new List<object>();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;

                    if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    {
                        textParts.Add(new { type = "text", text = textEl.GetString() });
                    }
                    else if (part.TryGetProperty("inlineData", out var inlineEl) && inlineEl.ValueKind == JsonValueKind.Object
                        && inlineEl.TryGetProperty("mimeType", out var mimeEl)
                        && inlineEl.TryGetProperty("data", out var dataEl))
                    {
                        // Gemini inlineData → OpenAI image_url data URL
                        textParts.Add(new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:{mimeEl.GetString()};base64,{dataEl.GetString()}" }
                        });
                    }
                    else if (isModel && part.TryGetProperty("functionCall", out var fnCall) && fnCall.ValueKind == JsonValueKind.Object)
                    {
                        // model 的 functionCall → assistant tool_calls
                        toolCalls.Add(new
                        {
                            id = $"call-{Guid.NewGuid():N}"[..12],
                            type = "function",
                            function = new
                            {
                                name = fnCall.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty,
                                arguments = fnCall.TryGetProperty("args", out var argsEl)
                                    && (argsEl.ValueKind == JsonValueKind.Object || argsEl.ValueKind == JsonValueKind.Array)
                                    ? argsEl.GetRawText()
                                    : "{}"
                            }
                        });
                    }
                    else if (!isModel && part.TryGetProperty("functionResponse", out var fnResp) && fnResp.ValueKind == JsonValueKind.Object)
                    {
                        // user 的 functionResponse → tool 消息（Gemini 用 name 匹配，OpenAI 用 id——以 name 充当 tool_call_id）
                        string fnName = fnResp.TryGetProperty("name", out var fnNameEl) ? fnNameEl.GetString() ?? string.Empty : string.Empty;
                        string resultJson = fnResp.TryGetProperty("response", out var respEl) ? respEl.GetRawText() : "{}";
                        messages.Add(new ChatMessage
                        {
                            Role = "tool",
                            Content = JsonSerializer.SerializeToElement(resultJson),
                            ExtensionData = new Dictionary<string, JsonElement>
                            {
                                ["tool_call_id"] = JsonSerializer.SerializeToElement(fnName)
                            }
                        });
                    }
                }

                if (textParts.Count > 0 || toolCalls.Count == 0)
                {
                    messages.Add(new ChatMessage
                    {
                        Role = isModel ? "assistant" : "user",
                        Content = textParts.Count == 0
                            ? JsonSerializer.SerializeToElement(string.Empty)
                            : JsonSerializer.SerializeToElement(textParts)
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

        // tools[{functionDeclarations}] → OpenAI tools
        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            var tools = new List<object>();
            foreach (var toolSet in toolsEl.EnumerateArray())
            {
                if (!toolSet.TryGetProperty("functionDeclarations", out var decls) || decls.ValueKind != JsonValueKind.Array) continue;
                foreach (var decl in decls.EnumerateArray())
                {
                    if (decl.ValueKind != JsonValueKind.Object) continue;
                    string name = decl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    if (name.Length == 0) continue;
                    tools.Add(new
                    {
                        type = "function",
                        function = new
                        {
                            name,
                            description = decl.TryGetProperty("description", out var descEl) ? descEl.GetString() : null,
                            parameters = decl.TryGetProperty("parameters", out var paramsEl)
                                ? paramsEl
                                : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
                        }
                    });
                }
            }
            if (tools.Count > 0)
            {
                extension["tools"] = JsonSerializer.SerializeToElement(tools);
            }
        }

        double? temperature = null;
        int? maxTokens = null;
        if (root.TryGetProperty("generationConfig", out var genConfig) && genConfig.ValueKind == JsonValueKind.Object)
        {
            if (genConfig.TryGetProperty("temperature", out var tempEl) && tempEl.ValueKind == JsonValueKind.Number)
            {
                temperature = tempEl.GetDouble();
            }
            if (genConfig.TryGetProperty("maxOutputTokens", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
            {
                maxTokens = maxEl.GetInt32();
            }
            if (genConfig.TryGetProperty("topP", out var topPEl) && topPEl.ValueKind == JsonValueKind.Number)
            {
                extension["top_p"] = topPEl.Clone();
            }
        }
        if (root.TryGetProperty("stopSequences", out var stopEl) && stopEl.ValueKind == JsonValueKind.Array)
        {
            extension["stop"] = stopEl.Clone();
        }

        return new ChatRequest
        {
            Model = modelFromPath,
            Messages = messages,
            Stream = false,
            Temperature = temperature,
            MaxTokens = maxTokens,
            ExtensionData = extension.Count > 0 ? extension : null
        };
    }

    /// <summary>
    /// 把 OpenAI 兼容非流式响应 JSON 翻译为 Gemini generateContent 响应 JSON（下游出口方向）。
    /// </summary>
    public static string ToGeminiJson(string openAiBody)
    {
        using var doc = JsonDocument.Parse(openAiBody);
        var root = doc.RootElement;

        string model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? string.Empty : string.Empty;

        var parts = new List<object>();
        string finishReason = "STOP";

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String
                    && (contentEl.GetString() ?? string.Empty).Length > 0)
                {
                    parts.Add(new { text = contentEl.GetString() });
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
                        string name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                        if (name.Length == 0) continue;
                        JsonElement args = default;
                        if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
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
            }

            if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
            {
                finishReason = frEl.GetString() switch
                {
                    "length" => "MAX_TOKENS",
                    "content_filter" => "SAFETY",
                    _ => "STOP"
                };
            }
        }

        int promptTokens = 0;
        int completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : 0;
        }

        return JsonSerializer.Serialize(new
        {
            candidates = new object[]
            {
                new
                {
                    content = new { parts, role = "model" },
                    finishReason,
                    index = 0
                }
            },
            usageMetadata = new
            {
                promptTokenCount = promptTokens,
                candidatesTokenCount = completionTokens,
                totalTokenCount = promptTokens + completionTokens
            },
            modelVersion = model
        });
    }

    /// <summary>
    /// 有状态流式翻译器：把 OpenAI 兼容 SSE data 行翻译为 Gemini GenerateContentResponse 流块。
    /// content delta → 文本块；[DONE] → 带 finishReason + usageMetadata 的终结块（Gemini 流以连接结束为界，无 [DONE] 标记）。
    /// </summary>
    public sealed class GeminiStreamTranslator
    {
        private readonly string _fallbackModel;
        private string _model;
        private string _finishReason = "STOP";
        private ChatUsage? _usage;

        /// <param name="requestedModel">路径中的模型名（chunk 未携带 model 时的回退值）。</param>
        public GeminiStreamTranslator(string requestedModel)
        {
            _fallbackModel = requestedModel;
            _model = requestedModel;
        }

        /// <summary>翻译一条 OpenAI data 行（JSON 或 [DONE]），返回 0..n 个 data 块（含空行分隔）。</summary>
        public IReadOnlyList<string> OnData(string data)
        {
            if (data == "[DONE]")
            {
                var finalChunk = new
                {
                    candidates = new object[]
                    {
                        new { content = new { parts = Array.Empty<object>(), role = "model" }, finishReason = _finishReason, index = 0 }
                    },
                    usageMetadata = new
                    {
                        promptTokenCount = _usage?.PromptTokens ?? 0,
                        candidatesTokenCount = _usage?.CompletionTokens ?? 0,
                        totalTokenCount = (_usage?.PromptTokens ?? 0) + (_usage?.CompletionTokens ?? 0)
                    },
                    modelVersion = _model.Length > 0 ? _model : _fallbackModel
                };
                return new[] { $"data: {JsonSerializer.Serialize(finalChunk)}\n\n" };
            }

            var blocks = new List<string>();
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
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String
                    && (contentEl.GetString() ?? string.Empty).Length > 0)
                {
                    var chunk = new
                    {
                        candidates = new object[]
                        {
                            new
                            {
                                content = new { parts = new object[] { new { text = contentEl.GetString() } }, role = "model" },
                                index = 0
                            }
                        },
                        modelVersion = _model.Length > 0 ? _model : _fallbackModel
                    };
                    blocks.Add($"data: {JsonSerializer.Serialize(chunk)}\n\n");
                }

                if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                {
                    _finishReason = frEl.GetString() switch
                    {
                        "length" => "MAX_TOKENS",
                        "content_filter" => "SAFETY",
                        _ => "STOP"
                    };
                }
            }
            catch (JsonException)
            {
                // 非 JSON data 行跳过
            }

            return blocks;
        }

        /// <summary>流中途失败：输出 Gemini 错误块。</summary>
        public static string OnError(int code, string status, string message)
            => $"data: {JsonSerializer.Serialize(new { error = new { code, message, status } })}\n\n";
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

using System.Text;
using System.Text.Json;
using OptiRouter.Clients;

namespace OptiRouter.Clients.Protocols;

/// <summary>
/// OpenAI Responses API（POST /v1/responses）与内部 OpenAI 兼容 ChatRequest 契约的双向翻译器。
/// <para>
/// 下游保持 OpenAI Responses 协议形状（input/output/usage 等字段命名），
/// 内部统一翻译为 <see cref="ChatRequest"/> 进路由管线；上游返回后（非流式 JSON /
/// 流式 SSE 事件）再翻译回 Responses 格式。协议入口对外始终是 Responses 契约。
/// </para>
/// <para>
/// 字段映射：input（string 或消息对象数组）→ messages；instructions → system 消息；
/// max_output_tokens → MaxTokens；temperature 直通；top_p / tools / tool_choice /
/// parallel_tool_calls / text.format / store / include / previous_response_id 经
/// ChatRequest.ExtensionData 原样透传，保证 tool-use 等高级能力不被丢弃。
/// 不映射：truncation / safety_identifier / reasoning（Responses 专有语义，上游 chat 契约无对应物，静默忽略）；
/// store / previous_response_id 只做透传，网关本身不存储响应。
/// </para>
/// </summary>
public static class OpenAiResponsesTranslators
{
    /// <summary>Responses API 流式事件类型常量。</summary>
    public const string EventCreated = "response.created";
    public const string EventOutputItemAdded = "response.output_item.added";
    public const string EventContentPartAdded = "response.content_part.added";
    public const string EventOutputTextDelta = "response.output_text.delta";
    public const string EventOutputTextDone = "response.output_text.done";
    public const string EventFunctionCallArgumentsDelta = "response.function_call_arguments.delta";
    public const string EventFunctionCallArgumentsDone = "response.function_call_arguments.done";
    public const string EventOutputItemDone = "response.output_item.done";
    public const string EventCompleted = "response.completed";

    /// <summary>
    /// 把 OpenAI Responses 请求体 JSON 翻译为内部 ChatRequest。
    /// </summary>
    /// <param name="json">客户端 POST /v1/responses 的原始请求体。</param>
    /// <returns>翻译后的 ChatRequest；messages 已归一化，未知字段进 ExtensionData。</returns>
    /// <exception cref="JsonException">JSON 结构非法或 input 类型不受支持时抛出。</exception>
    public static ChatRequest FromResponsesJson(string json)
    {
        // 空/空白请求体交给 JsonDocument.Parse 抛 JsonException，与兄弟端点同样落 400。
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The Responses request body must be a JSON object.");
        }

        string model = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString() ?? string.Empty
            : string.Empty;

        var messages = new List<ChatMessage>();

        if (root.TryGetProperty("instructions", out var instructions) && instructions.ValueKind is JsonValueKind.String or JsonValueKind.Array)
        {
            string text = ExtractText(instructions);
            if (!string.IsNullOrWhiteSpace(text))
            {
                messages.Add(ChatMessage.FromText("system", text));
            }
        }

        if (!root.TryGetProperty("input", out var input))
        {
            throw new JsonException("The Responses request must contain an 'input' field.");
        }

        switch (input.ValueKind)
        {
            case JsonValueKind.String:
                messages.Add(ChatMessage.FromText("user", input.GetString() ?? string.Empty));
                break;

            case JsonValueKind.Array:
                AppendInputArray(messages, input);
                break;

            default:
                throw new JsonException("'input' must be a string or an array of input items.");
        }

        if (messages.Count == 0)
        {
            throw new JsonException("'input' must contain at least one message.");
        }

        int? maxTokens = null;
        if (root.TryGetProperty("max_output_tokens", out var maxTok) && maxTok.ValueKind == JsonValueKind.Number)
        {
            maxTokens = maxTok.GetInt32();
            if (maxTokens <= 0)
            {
                throw new JsonException("'max_output_tokens' must be greater than zero.");
            }
        }

        double? temperature = null;
        if (root.TryGetProperty("temperature", out var temp) && temp.ValueKind == JsonValueKind.Number)
        {
            temperature = temp.GetDouble();
        }

        bool stream = false;
        if (root.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind == JsonValueKind.True)
        {
            stream = true;
        }

        var extension = new Dictionary<string, JsonElement>();
        CopyExtensionFields(root, extension);
        // ChatRequest 没有 TopP 属性，与 Anthropic/Gemini 入口口径一致：经 ExtensionData 透传上游。
        if (root.TryGetProperty("top_p", out var topP) && topP.ValueKind == JsonValueKind.Number)
        {
            extension["top_p"] = topP.Clone();
        }

        return new ChatRequest
        {
            Model = model,
            Messages = messages,
            MaxTokens = maxTokens,
            Temperature = temperature,
            Stream = stream,
            ExtensionData = extension.Count > 0 ? extension : null
        };
    }

    /// <summary>
    /// 把内部 OpenAI 契约响应（chat.completion 形状的原始 JSON）翻译为
    /// OpenAI Responses API 非流式 JSON（object=response）。
    /// </summary>
    /// <param name="responseBody">上游返回的原始 JSON 字符串。</param>
    /// <param name="responseId">用于生成响应 id；null 时生成随机 id。</param>
    /// <returns>Responses 格式的 JSON 字符串。</returns>
    public static string ToResponsesJson(string responseBody, string? responseId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseBody);

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        string id = responseId ?? NewResponseId();
        long createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string model = root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String
            ? mEl.GetString() ?? string.Empty
            : string.Empty;
        string status = root.TryGetProperty("status", out var sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString()!
            : "completed";

        var output = new List<object>();
        string? finishReason = null;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                {
                    finishReason = frEl.GetString();
                }

                if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 工具调用：message.tool_calls → Responses function_call output items
                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        string? callId = tc.TryGetProperty("id", out var tcIdEl) ? tcIdEl.GetString() : null;
                        string? name = null;
                        string? args = null;
                        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            name = fn.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                            args = fn.TryGetProperty("arguments", out var aEl) ? aEl.GetString() : null;
                        }
                        output.Add(new
                        {
                            type = "function_call",
                            id = NewFunctionCallId(),
                            call_id = callId,
                            name = name ?? string.Empty,
                            arguments = args ?? "{}",
                            status = "completed"
                        });
                    }
                }

                // 文本内容 → Responses output message（content 为 output_text 块数组）
                if (message.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String)
                    {
                        string text = content.GetString() ?? string.Empty;
                        if (text.Length > 0)
                        {
                            output.Add(BuildOutputMessage(text));
                        }
                    }
                    else if (content.ValueKind == JsonValueKind.Array)
                    {
                        var parts = new List<object>();
                        foreach (var item in content.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object
                                && item.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                                && tEl.ValueEquals("text")
                                && item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                            {
                                parts.Add(new
                                {
                                    type = "output_text",
                                    text = textEl.GetString(),
                                    annotations = new object[0]
                                });
                            }
                        }
                        if (parts.Count > 0)
                        {
                            output.Add(new
                            {
                                type = "message",
                                id = NewMessageId(),
                                status = "completed",
                                role = "assistant",
                                content = parts
                            });
                        }
                    }
                }
            }
        }

        var result = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = createdAt,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output
        };

        ApplyFinishReason(finishReason, result);

        var usage = ExtractUsage(root);
        if (usage is not null)
        {
            result["usage"] = usage;
        }

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// 流式事件翻译器：把内部 OpenAI chat.completion.chunk 的 SSE data 行翻译为
    /// Responses API 的 SSE 事件序列（<c>event: {type}\ndata: {json}\n\n</c> 双行帧）。
    /// <para>
    /// 文本走完整 output_text 生命周期：response.created → output_item.added →
    /// content_part.added → output_text.delta（逐段）→ output_text.done →
    /// output_item.done → response.completed。
    /// </para>
    /// <para>
    /// 工具调用走 function_call 项生命周期：output_item.added →
    /// function_call_arguments.delta（逐段）→ function_call_arguments.done → output_item.done。
    /// 文本与工具调用可以是多个输出项，output_index 按开项顺序递增。
    /// </para>
    /// </summary>
    public sealed class ResponsesStreamTranslator
    {
        private readonly string _responseId;
        private readonly long _createdAt;
        private readonly List<ItemState> _items = new();
        private readonly Dictionary<int, ItemState> _toolItemByIndex = new();
        private ItemState? _messageItem;
        private string _model = string.Empty;
        private bool _created;
        private bool _completed;
        private int _inputTokens;
        private int _outputTokens;
        private bool _hasUsage;

        /// <summary>
        /// 单个输出项（assistant 消息或 function_call）的流式状态。chat 流以
        /// <c>delta.tool_calls[].index</c> 分片下发（id/name 在首片、arguments 是增量），
        /// 故与 Anthropic 入口同口径做分片缓冲；Responses 侧一旦拿到 call_id 即开项、参数实时下发。
        /// </summary>
        private sealed class ItemState
        {
            public required int OutputIndex { get; init; }
            public required string ItemId { get; init; }
            public bool IsMessage { get; init; }
            public string CallId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public StringBuilder Content { get; } = new();

            /// <summary>已作为 delta 下发的字符数，避免重复发送。</summary>
            public int EmittedLength;

            public bool Added { get; set; }

            public string FullText => Content.ToString();
        }

        public ResponsesStreamTranslator(string? responseId = null)
        {
            _responseId = responseId ?? NewResponseId();
            _createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        /// <summary>
        /// 翻译一条内部 SSE data 行（JSON 或 [DONE]），返回按序拼接即可写出的 Responses SSE 帧列表。
        /// </summary>
        public IReadOnlyList<string> OnData(string dataJson)
        {
            if (_completed)
            {
                return Array.Empty<string>();
            }

            if (dataJson == "[DONE]")
            {
                _completed = true;
                return BuildClosingEvents(null);
            }

            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                {
                    _model = mEl.GetString() ?? string.Empty;
                }

                CaptureUsage(root);

                var events = new List<string>();
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                {
                    return events;
                }

                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        AppendTextDelta(events, delta);
                        AppendToolCallDeltas(events, delta);
                    }

                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        string? reason = fr.GetString();
                        if (!string.IsNullOrEmpty(reason))
                        {
                            _completed = true;
                            events.AddRange(BuildClosingEvents(reason));
                            return events;
                        }
                    }
                }

                return events;
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        private void AppendTextDelta(List<string> events, JsonElement delta)
        {
            if (!delta.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            {
                return;
            }

            string piece = content.GetString() ?? string.Empty;
            if (piece.Length == 0)
            {
                return;
            }

            var item = _messageItem ??= NewItem(isMessage: true);
            if (!item.Added)
            {
                OpenMessageItem(events, item);
            }

            item.Content.Append(piece);
            events.Add(Frame(EventOutputTextDelta, new Dictionary<string, object?>
            {
                ["type"] = EventOutputTextDelta,
                ["item_id"] = item.ItemId,
                ["output_index"] = item.OutputIndex,
                ["content_index"] = 0,
                ["delta"] = piece
            }));
        }

        /// <summary>
        /// chat 流的 <c>delta.tool_calls</c> 分片 → function_call 输出项。
        /// 按 index 缓冲 id/name 与 arguments 增量；Responses 的 function_call 项以 call_id 为标识，
        /// 故 call_id 未到的早期分片只累积不发事件，开项时一次性补齐已到达的参数。
        /// </summary>
        private void AppendToolCallDeltas(List<string> events, JsonElement delta)
        {
            if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var tc in toolCalls.EnumerateArray())
            {
                if (tc.ValueKind != JsonValueKind.Object
                    || !tc.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                int index = idxEl.GetInt32();
                if (!_toolItemByIndex.TryGetValue(index, out var item))
                {
                    item = NewItem(isMessage: false);
                    _toolItemByIndex[index] = item;
                }

                if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    item.CallId = idEl.GetString() ?? item.CallId;
                }

                if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                    {
                        item.Name = nameEl.GetString() ?? item.Name;
                    }
                    if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                    {
                        item.Content.Append(argsEl.GetString());
                    }
                }

                if (!item.Added)
                {
                    if (item.CallId.Length == 0)
                    {
                        continue;
                    }
                    OpenFunctionCallItem(events, item);
                }

                FlushArgumentDelta(events, item);
            }
        }

        private void FlushArgumentDelta(List<string> events, ItemState item)
        {
            string full = item.FullText;
            if (full.Length <= item.EmittedLength)
            {
                return;
            }

            events.Add(Frame(EventFunctionCallArgumentsDelta, new Dictionary<string, object?>
            {
                ["type"] = EventFunctionCallArgumentsDelta,
                ["item_id"] = item.ItemId,
                ["output_index"] = item.OutputIndex,
                ["delta"] = full[item.EmittedLength..]
            }));
            item.EmittedLength = full.Length;
        }

        private void CaptureUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _hasUsage = true;
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number)
            {
                _inputTokens = p.GetInt32();
            }
            if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number)
            {
                _outputTokens = c.GetInt32();
            }
        }

        /// <summary>
        /// 首个事件：response.created（status=in_progress，output 空数组），整条流只发一次。
        /// </summary>
        private void EnsureCreated(List<string> events)
        {
            if (_created)
            {
                return;
            }

            _created = true;
            events.Add(Frame(EventCreated, new Dictionary<string, object?>
            {
                ["type"] = EventCreated,
                ["response"] = BuildResponseObject("in_progress", Array.Empty<object>())
            }));
        }

        private ItemState NewItem(bool isMessage)
        {
            var item = new ItemState
            {
                OutputIndex = _items.Count,
                ItemId = isMessage ? NewMessageId() : NewFunctionCallId(),
                IsMessage = isMessage
            };
            _items.Add(item);
            return item;
        }

        /// <summary>
        /// 打开 assistant 消息输出项：output_item.added + content_part.added。
        /// </summary>
        private void OpenMessageItem(List<string> events, ItemState item)
        {
            EnsureCreated(events);
            item.Added = true;
            events.Add(Frame(EventOutputItemAdded, new Dictionary<string, object?>
            {
                ["type"] = EventOutputItemAdded,
                ["response_id"] = _responseId,
                ["output_index"] = item.OutputIndex,
                ["item"] = new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["id"] = item.ItemId,
                    ["status"] = "in_progress",
                    ["role"] = "assistant",
                    ["content"] = Array.Empty<object>()
                }
            }));
            events.Add(Frame(EventContentPartAdded, new Dictionary<string, object?>
            {
                ["type"] = EventContentPartAdded,
                ["item_id"] = item.ItemId,
                ["output_index"] = item.OutputIndex,
                ["content_index"] = 0,
                ["part"] = new Dictionary<string, object?>
                {
                    ["type"] = "output_text",
                    ["text"] = string.Empty,
                    ["annotations"] = Array.Empty<object>()
                }
            }));
        }

        /// <summary>
        /// 打开 function_call 输出项（arguments 先给空串，随后的 delta 逐段补齐）。
        /// </summary>
        private void OpenFunctionCallItem(List<string> events, ItemState item)
        {
            EnsureCreated(events);
            item.Added = true;
            events.Add(Frame(EventOutputItemAdded, new Dictionary<string, object?>
            {
                ["type"] = EventOutputItemAdded,
                ["response_id"] = _responseId,
                ["output_index"] = item.OutputIndex,
                ["item"] = new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["id"] = item.ItemId,
                    ["call_id"] = item.CallId,
                    ["name"] = item.Name,
                    ["arguments"] = string.Empty,
                    ["status"] = "in_progress"
                }
            }));
        }

        /// <summary>
        /// 收尾事件序列：逐输出项补齐 added（若迟到）与 done 事件，最后 response.completed
        /// 携带完整 output 数组。
        /// </summary>
        private IReadOnlyList<string> BuildClosingEvents(string? finishReason)
        {
            var events = new List<string>();
            EnsureCreated(events);

            foreach (var item in _items)
            {
                if (!item.Added)
                {
                    // 上游始终没给 call_id：合成一个，保证 added/done/completed 三项标识一致
                    if (!item.IsMessage && item.CallId.Length == 0)
                    {
                        item.CallId = $"call_{Guid.NewGuid():N}";
                    }
                    if (item.IsMessage)
                    {
                        OpenMessageItem(events, item);
                    }
                    else
                    {
                        OpenFunctionCallItem(events, item);
                    }
                }

                if (item.IsMessage)
                {
                    events.Add(Frame(EventOutputTextDone, new Dictionary<string, object?>
                    {
                        ["type"] = EventOutputTextDone,
                        ["item_id"] = item.ItemId,
                        ["output_index"] = item.OutputIndex,
                        ["content_index"] = 0,
                        ["text"] = item.FullText
                    }));
                }
                else
                {
                    FlushArgumentDelta(events, item);
                    events.Add(Frame(EventFunctionCallArgumentsDone, new Dictionary<string, object?>
                    {
                        ["type"] = EventFunctionCallArgumentsDone,
                        ["item_id"] = item.ItemId,
                        ["output_index"] = item.OutputIndex,
                        ["arguments"] = item.FullText
                    }));
                }

                events.Add(Frame(EventOutputItemDone, new Dictionary<string, object?>
                {
                    ["type"] = EventOutputItemDone,
                    ["response_id"] = _responseId,
                    ["output_index"] = item.OutputIndex,
                    ["item"] = BuildItemJson(item)
                }));
            }

            var outputItems = new List<object>();
            foreach (var item in _items)
            {
                outputItems.Add(BuildItemJson(item));
            }

            var response = BuildResponseObject("completed", outputItems);
            ApplyFinishReason(finishReason, response);
            if (_hasUsage)
            {
                response["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = _inputTokens,
                    ["output_tokens"] = _outputTokens,
                    ["total_tokens"] = _inputTokens + _outputTokens
                };
            }

            events.Add(Frame(EventCompleted, new Dictionary<string, object?>
            {
                ["type"] = EventCompleted,
                ["response"] = response
            }));
            return events;
        }

        private Dictionary<string, object?> BuildResponseObject(string status, object output) => new()
        {
            ["id"] = _responseId,
            ["object"] = "response",
            ["created_at"] = _createdAt,
            ["status"] = status,
            ["model"] = _model,
            ["output"] = output
        };

        private static Dictionary<string, object?> BuildItemJson(ItemState item)
        {
            if (item.IsMessage)
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["id"] = item.ItemId,
                    ["status"] = "completed",
                    ["role"] = "assistant",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "output_text",
                            ["text"] = item.FullText,
                            ["annotations"] = Array.Empty<object>()
                        }
                    }
                };
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "function_call",
                ["id"] = item.ItemId,
                ["call_id"] = item.CallId,
                ["name"] = item.Name,
                ["arguments"] = item.FullText,
                ["status"] = "completed"
            };
        }

        internal static string Frame(string eventType, Dictionary<string, object?> payload)
            => $"event: {eventType}\ndata: {JsonSerializer.Serialize(payload)}\n\n";
    }

    /// <summary>
    /// 把 chat 的 finish_reason 映射为 Responses 的 status：length / content_filter 是
    /// incomplete + incomplete_details，其余（stop / tool_calls）都是 completed。
    /// Responses 契约没有顶层 finish_reason 字段。
    /// </summary>
    private static void ApplyFinishReason(string? finishReason, Dictionary<string, object?> response)
    {
        string? incompleteReason = finishReason switch
        {
            "length" => "max_output_tokens",
            "content_filter" => "content_filter",
            _ => null
        };
        if (incompleteReason is null)
        {
            return;
        }

        response["status"] = "incomplete";
        response["incomplete_details"] = new Dictionary<string, object?> { ["reason"] = incompleteReason };
    }

    private static object BuildOutputMessage(string text)
    {
        return new
        {
            type = "message",
            id = NewMessageId(),
            status = "completed",
            role = "assistant",
            content = new object[]
            {
                new { type = "output_text", text, annotations = new object[0] }
            }
        };
    }

    private static string NewResponseId() => $"resp_{Guid.NewGuid():N}";
    private static string NewMessageId() => $"msg_{Guid.NewGuid():N}";
    private static string NewFunctionCallId() => $"fc_{Guid.NewGuid():N}";

    private static object? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int prompt = 0, completion = 0, total = 0;
        if (usage.TryGetProperty("prompt_tokens", out var p) && p.ValueKind == JsonValueKind.Number) prompt = p.GetInt32();
        if (usage.TryGetProperty("completion_tokens", out var c) && c.ValueKind == JsonValueKind.Number) completion = c.GetInt32();
        if (usage.TryGetProperty("total_tokens", out var t) && t.ValueKind == JsonValueKind.Number) total = t.GetInt32();

        return new
        {
            input_tokens = prompt,
            output_tokens = completion,
            total_tokens = total
        };
    }

    private static void CopyExtensionFields(JsonElement root, Dictionary<string, JsonElement> extension)
    {
        // 这些 Responses 字段在 chat 契约里同名同义，或上游会忽略未知字段，经 ExtensionData 原样透传。
        // OpenAI 兼容上游会忽略未知字段；原生协议客户端（Anthropic/Gemini）只读取自己认识的字段，不会受影响。
        // stream_options 不在此列——它是 chat 专有字段，Responses 请求体没有这个概念，不做透传。
        string[] known = { "text", "tools", "tool_choice", "store", "include", "previous_response_id", "metadata", "parallel_tool_calls" };
        foreach (var name in known)
        {
            if (root.TryGetProperty(name, out var el))
            {
                extension[name] = el.Clone();
            }
        }
    }

    private static void AppendInputArray(List<ChatMessage> messages, JsonElement input)
    {
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                messages.Add(ChatMessage.FromText("user", item.GetString() ?? string.Empty));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Each item in 'input' must be a string or an input item object.");
            }

            string? type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;

            // function_call_output → tool 消息（tool_call_id + content）
            if (type == "function_call_output")
            {
                string? callId = item.TryGetProperty("call_id", out var cidEl) && cidEl.ValueKind == JsonValueKind.String
                    ? cidEl.GetString()
                    : null;
                string output = item.TryGetProperty("output", out var outEl)
                    ? outEl.ValueKind switch
                    {
                        JsonValueKind.String => outEl.GetString() ?? string.Empty,
                        JsonValueKind.Array => ExtractText(outEl),
                        JsonValueKind.Null => string.Empty,
                        // 结构化输出（object）按上游 chat 契约序列化为字符串
                        _ => outEl.GetRawText()
                    }
                    : string.Empty;
                var toolMsg = new ChatMessage
                {
                    Role = "tool",
                    Content = JsonSerializer.SerializeToElement(output)
                };
                var toolExt = new Dictionary<string, JsonElement>();
                if (callId is not null)
                {
                    toolExt["tool_call_id"] = JsonSerializer.SerializeToElement(callId);
                }
                toolMsg = toolMsg with { ExtensionData = toolExt.Count > 0 ? toolExt : null };
                messages.Add(toolMsg);
                continue;
            }

            // function_call（assistant 侧的工具调用记录）→ assistant 消息 + tool_calls
            if (type == "function_call")
            {
                string? callId = item.TryGetProperty("call_id", out var cid2El) && cid2El.ValueKind == JsonValueKind.String
                    ? cid2El.GetString()
                    : null;
                string? recordId = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                string? name = null;
                string? args = null;
                if (item.TryGetProperty("arguments", out var argsEl))
                {
                    args = argsEl.ValueKind == JsonValueKind.String ? argsEl.GetString() : argsEl.GetRawText();
                }
                if (item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    name = nameEl.GetString();
                }

                var toolCall = new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = name ?? string.Empty,
                        ["arguments"] = args ?? "{}"
                    }
                };
                // 上游 chat 契约要求 tool_calls[].id 与后续 tool 消息的 tool_call_id 一致，而 Responses
                // 转写里承担这一职责的是 call_id（条目主键 id 是 fc_ 前缀，与调用无关），故 call_id 优先、
                // 仅缺 call_id 时才回退记录 id。
                toolCall["id"] = callId ?? recordId ?? $"call_{Guid.NewGuid():N}";

                var assistantMsg = new ChatMessage { Role = "assistant" };
                assistantMsg = assistantMsg with
                {
                    ExtensionData = new Dictionary<string, JsonElement>
                    {
                        ["tool_calls"] = JsonSerializer.SerializeToElement(new[] { toolCall })
                    }
                };
                messages.Add(assistantMsg);
                continue;
            }

            // 普通消息：role + content
            string role = item.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                ? roleEl.GetString() ?? "user"
                : "user";
            string normalizedRole = role.ToLowerInvariant() switch
            {
                "system" => "system",
                "assistant" => "assistant",
                "tool" => "tool",
                _ => "user"
            };

            if (normalizedRole == "system")
            {
                string text = ExtractContentText(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    messages.Add(ChatMessage.FromText("system", text));
                }
                continue;
            }

            var message = new ChatMessage { Role = normalizedRole };
            if (item.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
            {
                message = message with { Content = NormalizeInputContent(content) };
            }

            var ext = new Dictionary<string, JsonElement>();
            if (item.TryGetProperty("type", out var typeEl2))
                ext["type"] = typeEl2.Clone();
            if (item.TryGetProperty("id", out var idEl2))
                ext["id"] = idEl2.Clone();
            if (item.TryGetProperty("name", out var nameEl2))
                ext["name"] = nameEl2.Clone();
            if (item.TryGetProperty("tool_call_id", out var tcidEl))
                ext["tool_call_id"] = tcidEl.Clone();
            if (item.TryGetProperty("status", out var statusEl))
                ext["status"] = statusEl.Clone();

            message = message with { ExtensionData = ext.Count > 0 ? ext : null };
            messages.Add(message);
        }
    }

    /// <summary>
    /// 把 Responses 入站内容块归一化为 chat 契约认识的类型：
    /// input_text / output_text → text，input_image → image_url；其余块原样保留。
    /// 不归一化会让严格上游 400（只接受 type=text/image_url）。
    /// </summary>
    private static JsonElement NormalizeInputContent(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.Clone();
        }

        var blocks = new List<object?>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                if (ChatMessage.IsResponsesTextAliasType(typeEl))
                {
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = block.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                            ? textEl.GetString()
                            : string.Empty
                    });
                    continue;
                }

                if (typeEl.ValueEquals("input_image")
                    && block.TryGetProperty("image_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    var imageUrl = new Dictionary<string, object?> { ["url"] = urlEl.GetString() };
                    if (block.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String)
                    {
                        imageUrl["detail"] = detailEl.GetString();
                    }
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "image_url",
                        ["image_url"] = imageUrl
                    });
                    continue;
                }
            }

            blocks.Add(block.Clone());
        }

        return JsonSerializer.SerializeToElement(blocks);
    }

    private static string ExtractContentText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }
        return ExtractText(content);
    }

    private static string ExtractText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                {
                    // input_text/output_text 块也带 text 字段，按属性读取即可，无需按 type 分支。
                    sb.Append(tEl.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }
}
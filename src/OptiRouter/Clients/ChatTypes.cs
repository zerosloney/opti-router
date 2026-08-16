using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiRouter.Clients;

/// <summary>
/// OpenAI Chat Completions 请求。
/// </summary>
/// <remarks>
/// 未知字段（top_p / tools / tool_choice / response_format / stop / seed / n / logit_bias / user 等）
/// 经 <see cref="ExtensionData"/> 原样透传上游，保证 tool-use 等高级能力不被丢弃。
/// </remarks>
public sealed record ChatRequest
{
    /// <summary>
    /// 模型标识，客户端会强制覆盖为端点配置的模型名。
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// 对话消息列表。
    /// </summary>
    public IList<ChatMessage> Messages { get; init; } = new List<ChatMessage>();

    /// <summary>
    /// 是否流式返回。
    /// </summary>
    public bool Stream { get; init; }

    /// <summary>
    /// 采样温度，0~2。
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// 最大生成 token 数。
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>
    /// 未知字段原样透传。键名为上游原始 property name（不经过 naming policy 转换），
    /// 序列化时原样写回，避免破坏上游契约（如 function calling 的 tool_calls 结构）。
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>
/// 单条对话消息。
/// </summary>
/// <remarks>
/// <see cref="Content"/> 为 <see cref="JsonElement"/> 以同时支持纯文本（string）
/// 与多模态数组（vision 的 <c>[{type:"text"...},{type:"image_url"...}]</c>）。
/// 序列化时原样写回，上游收到完整结构。
/// 路由策略/估算器只需文本时调 <see cref="GetText"/> 抽取文本部分。
/// </remarks>
public sealed record ChatMessage
{
    /// <summary>
    /// 角色：system / user / assistant / tool。
    /// </summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>
    /// 消息内容。纯文本时为 string kind，多模态时为 array kind；未提供时为 null（序列化时跳过）。
    /// 用 <see cref="JsonElement"/>? 而非裸 <see cref="JsonElement"/>，避免默认值（Undefined）无法序列化。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    /// <summary>
    /// 未知字段原样透传（如 assistant 消息的 tool_calls、tool 消息的 tool_call_id）。
    /// 键名为上游原始 property name，序列化时原样写回，保证工具调用重放不破坏上游契约。
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }

    /// <summary>
    /// 从纯文本构造。覆盖最常见的字符串 content 场景，简化调用方。
    /// </summary>
    public static ChatMessage FromText(string role, string content)
        => new()
        {
            Role = role,
            Content = JsonSerializer.SerializeToElement(content)
        };

    /// <summary>
    /// 抽取 content 的文本部分用于路由判定与 token 估算。
    /// 纯文本直接返回；多模态数组拼接所有 type=="text" 的 text 字段；其他情况返回空串。
    /// </summary>
    public string GetText()
    {
        if (Content is not { } el) return string.Empty;
        if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null)
            return string.Empty;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? string.Empty;

        if (el.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var typeEl)
                    && typeEl.ValueKind == JsonValueKind.String
                    && typeEl.ValueEquals("text")
                    && item.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    sb.Append(textEl.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }
}

/// <summary>
/// OpenAI Chat Completions 非流式响应。
/// </summary>
public sealed record ChatResponse
{
    /// <summary>
    /// 响应 ID。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 实际使用的模型。
    /// </summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>
    /// 候选结果列表。
    /// </summary>
    public IList<ChatChoice> Choices { get; init; } = new List<ChatChoice>();

    /// <summary>
    /// Token 用量。
    /// </summary>
    public ChatUsage Usage { get; init; } = new();
}

/// <summary>
/// 单条候选结果。
/// </summary>
public sealed record ChatChoice
{
    /// <summary>
    /// 候选索引。
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// 生成的消息。
    /// </summary>
    public ChatMessage Message { get; init; } = new();

    /// <summary>
    /// 结束原因。
    /// </summary>
    public string FinishReason { get; init; } = string.Empty;
}

/// <summary>
/// Token 用量统计。
/// </summary>
public sealed record ChatUsage
{
    /// <summary>
    /// 提示 token 数。
    /// </summary>
    public int PromptTokens { get; init; }

    /// <summary>
    /// 补全 token 数。
    /// </summary>
    public int CompletionTokens { get; init; }

    /// <summary>
    /// 总 token 数。
    /// </summary>
    public int TotalTokens { get; init; }

    /// <summary>Prompt tokens served from an upstream prompt cache.</summary>
    public int CachedInputTokens { get; init; }

    /// <summary>Prompt tokens written to an upstream prompt cache.</summary>
    public int CacheWriteInputTokens { get; init; }

    /// <summary>Explicit or safely derived uncached prompt tokens.</summary>
    public int UncachedInputTokens { get; init; }
}

/// <summary>
/// OpenAI 流式响应块（SSE data 行解析后）。
/// </summary>
public sealed record ChatStreamChunk
{
    /// <summary>
    /// 响应 ID。
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// 增量文本，可空（首块可能为空，仅含 role）。
    /// </summary>
    public string? DeltaContent { get; init; }

    /// <summary>
    /// 最后一块的结束原因。
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// 最后一块可能携带的 usage。
    /// </summary>
    public ChatUsage? Usage { get; init; }
}

// 内部辅助 DTO，用于反序列化 OpenAI SSE 原始 JSON 结构。
internal sealed record RawStreamChunk(
    string Id,
    IList<RawStreamChoice> Choices,
    ChatUsage? Usage);

internal sealed record RawStreamChoice(
    int Index,
    RawStreamDelta Delta,
    string? FinishReason);

internal sealed record RawStreamDelta(
    string? Content,
    string? Role);

/// <summary>
/// 非流式原始响应：上游返回的原始 JSON 字符串 + 从中提取的 token 用量（供记账）。
/// </summary>
/// <param name="Body">上游原始 JSON 字符串，原样回传客户端。</param>
/// <param name="Usage">从 Body 提取的 token 用量；上游未返回时为 null。</param>
/// <param name="Metadata">规范化响应元数据；未知时为 null。</param>
public sealed record RawChatResponse(
    string Body,
    ChatUsage? Usage,
    UpstreamResponseMetadata? Metadata = null);

/// <summary>
/// 流式原始响应行：单条 SSE <c>data:</c> 后的原始内容 + 从中提取的 token 用量。
/// </summary>
/// <param name="Data">原始 data 内容（JSON 或 <c>[DONE]</c>）。</param>
/// <param name="Usage">从 Data 提取的 token 用量；该行未携带或非 JSON 时为 null。</param>
/// <param name="Metadata">仅首个 data 行携带的规范化响应元数据。</param>
public sealed record RawStreamLine(
    string Data,
    ChatUsage? Usage,
    UpstreamResponseMetadata? Metadata = null);

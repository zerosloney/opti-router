using System.Text.Json.Serialization;

namespace OptiRouter.Clients;

/// <summary>
/// OpenAI Chat Completions 请求。
/// </summary>
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
}

/// <summary>
/// 单条对话消息。
/// </summary>
public sealed record ChatMessage
{
    /// <summary>
    /// 角色：system / user / assistant / tool。
    /// </summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>
    /// 消息内容。
    /// </summary>
    public string Content { get; init; } = string.Empty;
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
public sealed record RawChatResponse(string Body, ChatUsage? Usage);

/// <summary>
/// 流式原始响应行：单条 SSE <c>data:</c> 后的原始内容 + 从中提取的 token 用量。
/// </summary>
/// <param name="Data">原始 data 内容（JSON 或 <c>[DONE]</c>）。</param>
/// <param name="Usage">从 Data 提取的 token 用量；该行未携带或非 JSON 时为 null。</param>
public sealed record RawStreamLine(string Data, ChatUsage? Usage);

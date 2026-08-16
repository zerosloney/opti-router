using OptiRouter.Clients;

namespace OptiRouter.Compression;

/// <summary>
/// 智能提示词压缩与 Token 动态瘦身配置选项。
/// </summary>
public sealed class PromptCompressionOptions
{
    /// <summary>
    /// 是否启用自适应提示词压缩。默认启用。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 触发压缩的最小 Token 阈值。低于此阈值的简短请求直接透传。默认 300。
    /// </summary>
    public int MinTokensToTrigger { get; set; } = 300;

    /// <summary>
    /// 目标压缩率（如 0.30 表示期望削减约 30% Token）。默认 0.30。
    /// </summary>
    public double TargetReductionRatio { get; set; } = 0.30;

    /// <summary>
    /// 保留最近完整对话轮次数（尾部最近 N 轮对话原样保留，不进行剪枝）。默认 2。
    /// </summary>
    public int PreserveRecentTurns { get; set; } = 2;

    /// <summary>
    /// 是否合并并去重重复的 System Instruction。默认 true。
    /// </summary>
    public bool DeduplicateSystemPrompts { get; set; } = true;

    /// <summary>
    /// 是否剔除多轮历史中的客套寒暄与冗余填充语（如 "Sure, I can help with that" 等）。默认 true。
    /// </summary>
    public bool StripConversationalFillers { get; set; } = true;

    /// <summary>
    /// 是否强制保护代码块（```）与 JSON 结构完整性（确保代码语法不被破坏）。默认 true。
    /// </summary>
    public bool PreserveCodeAndJson { get; set; } = true;
}

/// <summary>
/// 提示词压缩执行结果。
/// </summary>
public sealed record PromptCompressionResult(
    ChatRequest CompressedRequest,
    int OriginalEstimatedTokens,
    int CompressedEstimatedTokens,
    double ReductionRatio,
    bool WasCompressed,
    string StrategySummary);

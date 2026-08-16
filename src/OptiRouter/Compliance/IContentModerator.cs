namespace OptiRouter.Compliance;

/// <summary>
/// 内容审核方向。
/// </summary>
public enum ModerationDirection
{
    /// <summary>请求输入（用户消息）。</summary>
    Input = 0,

    /// <summary>模型输出（响应内容）。</summary>
    Output = 1
}

/// <summary>
/// 内容审核违规处理动作。
/// </summary>
public enum ModerationAction
{
    /// <summary>不处理（仅审计/记录）。</summary>
    None = 0,

    /// <summary>阻断：输入违规拒绝请求，输出违规中断响应。</summary>
    Block = 1,

    /// <summary>替换敏感内容（当前版本仅输入/输出阻断可用，Redact 为后续扩展）。</summary>
    Redact = 2
}

/// <summary>
/// 内容审核结果。
/// </summary>
/// <param name="IsViolation">是否判定违规。</param>
/// <param name="Category">违规类别（如 hate / violence / sexual 等），无违规时为 null。</param>
/// <param name="Score">最高类别分数 [0,1]。</param>
/// <param name="Reason">判定说明（含阈值信息或服务不可用原因）。</param>
public sealed record ModerationResult(bool IsViolation, string? Category, double Score, string? Reason);

/// <summary>
/// 内容审核器抽象。实现可对接 OpenAI Moderation API、本地模型或规则引擎。
/// </summary>
public interface IContentModerator
{
    /// <summary>实现标识（用于审计与诊断）。</summary>
    string Name { get; }

    /// <summary>
    /// 审核单段文本。服务不可用时实现应 fail-open（返回非违规并说明原因），
    /// 由调用方按策略决定是否放行。
    /// </summary>
    Task<ModerationResult> ModerateTextAsync(string text, ModerationDirection direction, CancellationToken ct = default);
}

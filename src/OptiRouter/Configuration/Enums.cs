namespace OptiRouter.Configuration;

/// <summary>
/// 模型能力分档，用于路由时的能力匹配与降级策略。
/// </summary>
public enum ModelTier
{
    /// <summary>
    /// 最强能力档，通常价格最高、上下文最长。
    /// </summary>
    Strong = 0,

    /// <summary>
    /// 中等能力档，平衡性能与成本。
    /// </summary>
    Medium = 1,

    /// <summary>
    /// 低成本档，适合简单任务或预算紧张场景。
    /// </summary>
    Cheap = 2
}

/// <summary>
/// 预算耗尽后的行为模式。
/// </summary>
public enum BudgetExhaustionMode
{
    /// <summary>
    /// 降级到更便宜的模型继续服务。
    /// </summary>
    Degrade = 0,

    /// <summary>
    /// 直接拒绝请求。
    /// </summary>
    Reject = 1
}

/// <summary>
/// Token 估算模式。
/// </summary>
public enum TokenEstimationMode
{
    /// <summary>
    /// 分桶加权粗估（CJK/ASCII/其他按经验系数）。零依赖、极快，误差约 ±15%。
    /// </summary>
    Bucket = 0,

    /// <summary>
    /// 真实 BPE 精确计数（SharpToken 内置词表，离线可用）。
    /// 计数异常时自动回退到 <see cref="Bucket"/> 粗估，保证路由不被阻塞。
    /// </summary>
    Tiktoken = 1
}

/// <summary>
/// 上游模型端点协议。默认 OpenAI 兼容；原生协议由对应客户端在内部完成
/// 请求/响应双向翻译，对外（下游）始终保持 OpenAI 契约不变。
/// </summary>
public enum ProviderProtocol
{
    /// <summary>OpenAI 兼容接口（默认）。</summary>
    OpenAI = 0,

    /// <summary>Anthropic Messages API（/v1/messages）。</summary>
    Anthropic = 1,

    /// <summary>Google Gemini generateContent API。</summary>
    Gemini = 2
}

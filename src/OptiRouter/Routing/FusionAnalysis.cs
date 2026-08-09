namespace OptiRouter.Routing;

/// <summary>
/// 融合路由 analyst 产出的结构化分析结果（参照 OpenRouter Fusion Router）。
/// analyst 不写最终答案，只报告 panel 各模型回答的共识、矛盾、覆盖缺口与独特洞察，
/// 供 outer 模型据此撰写最终答案。字段与 analyst prompt 要求的 JSON schema 对应。
/// </summary>
public sealed record FusionAnalysis
{
    /// <summary>所有（或多数）panel 模型一致认同的结论要点。</summary>
    public string Consensus { get; init; } = string.Empty;

    /// <summary>panel 模型之间存在的矛盾或分歧。</summary>
    public string Contradictions { get; init; } = string.Empty;

    /// <summary>所有 panel 模型都未覆盖到的盲点/缺口。</summary>
    public string Gaps { get; init; } = string.Empty;

    /// <summary>个别 panel 模型提供、其他模型遗漏的独特洞察。</summary>
    public string UniqueInsights { get; init; } = string.Empty;

    /// <summary>综合上述提炼出的最终回答方向建议。</summary>
    public string Recommendation { get; init; } = string.Empty;
}
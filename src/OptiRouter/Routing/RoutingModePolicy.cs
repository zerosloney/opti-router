using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 路由模式预设策略（Filter 组，链首）：解析 <c>model</c> 字段的模式预设
/// <c>auto:cost / auto:balanced / auto:intel</c>（intelligence 别名），
/// 设置 <see cref="RouterDecision.RoutingMode"/> 与 <see cref="RouterDecision.TargetTier"/>，
/// 并把候选过滤到目标档位——cost 省钱(Cheap)、balanced 平衡(Medium)、intel 质量(Strong)。
/// 为后续所有策略提供"北极星"目标；目标档无可用模型时保留全候选（模式标记仍生效，
/// FailoverPolicy 级联仍按 TargetTier 锚定）。非 auto: 前缀的请求透传不受影响。
/// </summary>
public sealed class RoutingModePolicy : IRouterPolicy
{
    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Filter;

    /// <summary>
    /// 解析 model 字段的模式预设；非预设（空/普通模型名/未知预设）返回 null。
    /// 供 ProxyOrchestrator 在路由决策之前联动压缩参数（压缩先于 Decide，拿不到 decision）。
    /// </summary>
    public static RoutingMode? TryResolveMode(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || !model.StartsWith(ExplicitModelPolicy.AutoModePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return model.Substring(ExplicitModelPolicy.AutoModePrefix.Length).ToLowerInvariant() switch
        {
            "cost" => RoutingMode.Cost,
            "intel" or "intelligence" => RoutingMode.Intelligence,
            "balanced" => RoutingMode.Balanced,
            _ => null
        };
    }

    /// <summary>
    /// 模式联动压缩参数：Cost 更早触发（阈值减半）更狠压缩（压缩率翻倍）省 token 即省钱；
    /// Intelligence 更晚触发（阈值翻倍）更保守（压缩率减半）保留完整上下文换质量；
    /// Balanced/无预设用配置原值。只调触发阈值与目标压缩率两个旋钮，
    /// 内容保护规则（代码块/近轮保留/去重）逐字段原样拷贝不动。
    /// </summary>
    internal static Compression.PromptCompressionOptions AdjustCompression(
        Compression.PromptCompressionOptions compression, RoutingMode? mode)
    {
        if (mode is null or RoutingMode.Balanced)
        {
            return compression;
        }

        var adjusted = new Compression.PromptCompressionOptions
        {
            Enabled = compression.Enabled,
            PreserveRecentTurns = compression.PreserveRecentTurns,
            DeduplicateSystemPrompts = compression.DeduplicateSystemPrompts,
            StripConversationalFillers = compression.StripConversationalFillers,
            PreserveCodeAndJson = compression.PreserveCodeAndJson
        };

        if (mode == RoutingMode.Cost)
        {
            adjusted.MinTokensToTrigger = Math.Max(1, compression.MinTokensToTrigger / 2);
            adjusted.TargetReductionRatio = Math.Min(0.8, compression.TargetReductionRatio * 2);
        }
        else // Intelligence
        {
            adjusted.MinTokensToTrigger = compression.MinTokensToTrigger * 2;
            adjusted.TargetReductionRatio = compression.TargetReductionRatio / 2;
        }

        return adjusted;
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var requested = context.Request.Model;
        var mode = TryResolveMode(requested);
        if (mode is null)
        {
            return previous.Append("routing-mode", string.IsNullOrWhiteSpace(requested)
                ? "no model requested, using default balanced"
                : "no mode preset, using default balanced");
        }

        var targetTier = mode switch
        {
            RoutingMode.Cost => ModelTier.Cheap,
            RoutingMode.Intelligence => ModelTier.Strong,
            _ => ModelTier.Medium
        };

        // 候选过滤到目标档：只标记不过滤时初始链仍是 Strong 优先排序，
        // auto:cost 实际会命中 Strong——"控成本"没有行为。目标档空则保留全候选兜底。
        var tierCandidates = previous.Candidates.Where(m => m.Tier == targetTier).ToList();
        var updated = tierCandidates.Count > 0
            ? previous with { Candidates = tierCandidates, RoutingMode = mode, TargetTier = targetTier }
            : previous with { RoutingMode = mode, TargetTier = targetTier };

        return updated.Append("routing-mode",
            tierCandidates.Count > 0
                ? $"preset={mode}, target={targetTier}, filtered to {tierCandidates.Count} {targetTier} candidate(s)"
                : $"preset={mode}, target={targetTier}, no {targetTier} candidate, keeping all");
    }
}

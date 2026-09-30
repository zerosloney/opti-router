using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 长输入策略：当输入 token 超过阈值时，只保留能容纳的模型；
/// 若 <see cref="RoutingOptions.LongInputForceMedium"/> 启用，额外从候选中排除 Strong 档
/// （长 prompt + Strong 档 stealth 模型 p95 70s+，远劣于 Medium 的"小但稳"）。
/// </summary>
/// <remarks>
/// intentional-simple: 上下文余量系数 1.2，留 20% 给输出。
/// 如需更保守，可调高系数；如需更激进，可调低。
/// </remarks>
public sealed class LongInputPolicy : IRouterPolicy
{
    public PolicyGroup Group => PolicyGroup.Filter;
    private const double ContextHeadroom = 1.2;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableTokenEstimator)
        {
            return previous.Append("long-input", "disabled");
        }

        int threshold = context.Options.Routing.LongInputThresholdTokens;
        int estimated = context.EstimatedInputTokens;
        bool forceMedium = context.Options.Routing.LongInputForceMedium;

        if (estimated <= threshold)
        {
            // 短 prompt：若开关开，仍然打日志记录（"未触发"），便于排查"配置是否生效"。
            string reason = forceMedium
                ? $"within-threshold({estimated}<={threshold}); force-medium-armed-but-not-triggered"
                : $"within-threshold({estimated}<={threshold})";
            return previous.Append("long-input", reason);
        }

        int requiredContext = (int)Math.Ceiling(estimated * ContextHeadroom);
        var filtered = previous.Candidates
            .Where(m => m.MaxContextTokens >= requiredContext)
            .ToList();

        if (forceMedium)
        {
            // 先记下 context 过滤后的列表——它本身就是 forceMedium 排除 Strong 后的兜底来源
            // （确保如果 forceMedium 过滤后为空，能回退到"能装下的所有模型"，而不是"原始所有模型"，
            // 避免把明显装不下的模型也带回来）。
            var contextFiltered = filtered;
            int beforeTierFilter = contextFiltered.Count;
            var afterTierFilter = contextFiltered.Where(m => m.Tier != ModelTier.Strong).ToList();
            int droppedStrong = beforeTierFilter - afterTierFilter.Count;

            if (afterTierFilter.Count > 0)
            {
                return (previous with { Candidates = afterTierFilter }).Append("long-input",
                    $"filtered to {afterTierFilter.Count} candidates (est={estimated}, required>={requiredContext}, dropped-strong={droppedStrong})");
            }

            // forceMedium 过滤后空：唯一能装下的都是 Strong。回退到 context 过滤后版本（保留 Strong），
            // 记录"forceMedium 想踢掉所有候选但没有可替代"——reason 不带 dropped-strong 标记（因为实际未丢任何东西）。
            if (contextFiltered.Count > 0)
            {
                return (previous with { Candidates = contextFiltered }).Append("long-input",
                    $"filtered to {contextFiltered.Count} candidates (est={estimated}, required>={requiredContext}, force-medium-kept-strong-only)");
            }
            // 否则落入下方 "no model fits" 通用兜底
        }

        if (filtered.Count > 0)
        {
            var withFiltered = previous with { Candidates = filtered };
            return withFiltered.Append("long-input", $"filtered to {filtered.Count} candidates (est={estimated}, required>={requiredContext})");
        }

        // 没有模型能装下，保留原候选 + warning
        return previous.Append("long-input", $"no model fits (est={estimated}, required>={requiredContext}), keeping original candidates");
    }
}

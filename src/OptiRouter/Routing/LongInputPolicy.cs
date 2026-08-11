using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 长输入策略：当输入 token 超过阈值时，只保留能容纳的模型。
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

        if (estimated <= threshold)
        {
            return previous.Append("long-input", $"within-threshold({estimated}<={threshold})");
        }

        int requiredContext = (int)Math.Ceiling(estimated * ContextHeadroom);
        var filtered = previous.Candidates
            .Where(m => m.MaxContextTokens >= requiredContext)
            .ToList();

        if (filtered.Count > 0)
        {
            var withFiltered = previous with { Candidates = filtered };
            return withFiltered.Append("long-input", $"filtered to {filtered.Count} candidates (est={estimated}, required>={requiredContext})");
        }

        // 没有模型能装下，保留原候选 + warning
        return previous.Append("long-input", $"no model fits (est={estimated}, required>={requiredContext}), keeping original candidates");
    }
}

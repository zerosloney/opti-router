using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 失败降级策略：排除已知失败的模型，必要时补充降级链。
/// </summary>
public sealed class FailoverPolicy : IRouterPolicy
{
    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableFailover)
        {
            return previous with { Reason = $"{previous.Reason}; failover: disabled" };
        }

        if (context.FailedModels.Count == 0)
        {
            return previous with { Reason = $"{previous.Reason}; failover: no-failed-models" };
        }

        var remaining = previous.Candidates
            .Where(m => !context.FailedModels.Contains(m.Name))
            .ToList();

        if (remaining.Count > 0)
        {
            string removed = string.Join(", ", previous.Candidates.Select(m => m.Name).Except(remaining.Select(m => m.Name)));
            string reason = removed.Length > 0
                ? $"failover: removed failed [{removed}], {remaining.Count} remaining"
                : "failover: no candidates removed";
            return previous with
            {
                Candidates = remaining,
                Reason = $"{previous.Reason}; {reason}"
            };
        }

        // 全部失败，需要补充降级链
        var fallback = BuildFallbackChain(context.AllModels, previous.Candidates, context.FailedModels);
        string fallbackReason = $"failover: all candidates failed, fallback to [{string.Join(", ", fallback.Select(m => m.Name))}]";
        return previous with
        {
            Candidates = fallback,
            Reason = $"{previous.Reason}; {fallbackReason}"
        };
    }

    private static List<ModelEndpointOptions> BuildFallbackChain(
        IReadOnlyList<ModelEndpointOptions> allModels,
        IReadOnlyList<ModelEndpointOptions> previousCandidates,
        IReadOnlySet<string> failedModels)
    {
        // intentional-simple: 降级顺序 Strong -> Medium -> Cheap，同 tier 按 MaxContextTokens 降序
        var tierOrder = new[] { ModelTier.Strong, ModelTier.Medium, ModelTier.Cheap };

        foreach (var tier in tierOrder)
        {
            var sameTierFallback = allModels
                .Where(m => m.Enabled && m.Tier == tier && !failedModels.Contains(m.Name))
                .OrderByDescending(m => m.MaxContextTokens)
                .ToList();

            if (sameTierFallback.Count > 0)
            {
                return sameTierFallback;
            }
        }

        // 保底：任何能用的最便宜模型
        var anyAvailable = allModels
            .Where(m => m.Enabled && !failedModels.Contains(m.Name))
            .OrderBy(m => m.InputPricePerMillion)
            .ToList();

        return anyAvailable.Count > 0 ? anyAvailable : new List<ModelEndpointOptions>();
    }
}

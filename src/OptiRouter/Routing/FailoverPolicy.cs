using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 失败降级策略：排除已知失败的模型与跨请求熔断冷却中的模型，必要时补充降级链。
/// </summary>
public sealed class FailoverPolicy : IRouterPolicy
{
    private readonly ModelHealthTracker _healthTracker;

    /// <summary>
    /// 构造失败降级策略。
    /// </summary>
    /// <param name="healthTracker">跨请求模型健康跟踪器。</param>
    public FailoverPolicy(ModelHealthTracker healthTracker)
    {
        _healthTracker = healthTracker ?? throw new ArgumentNullException(nameof(healthTracker));
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableFailover)
        {
            return previous with { Reason = $"{previous.Reason}; failover: disabled" };
        }

        // 合并单请求内失败模型与跨请求冷却中的模型。
        var excluded = new HashSet<string>(context.FailedModels);
        List<string> coolingDown = new();
        foreach (var model in previous.Candidates)
        {
            if (_healthTracker.IsCoolingDown(model.Name))
            {
                if (excluded.Add(model.Name))
                    coolingDown.Add(model.Name);
            }
        }

        if (excluded.Count == 0)
        {
            return previous with { Reason = $"{previous.Reason}; failover: no-failed-models" };
        }

        var remaining = previous.Candidates
            .Where(m => !excluded.Contains(m.Name))
            .ToList();

        if (remaining.Count > 0)
        {
            string removed = string.Join(", ", previous.Candidates.Select(m => m.Name).Except(remaining.Select(m => m.Name)));
            string coolingNote = coolingDown.Count > 0 ? $", cooling [{string.Join(", ", coolingDown)}]" : "";
            string reason = removed.Length > 0
                ? $"failover: removed failed [{removed}]{coolingNote}, {remaining.Count} remaining"
                : "failover: no candidates removed";
            return previous with
            {
                Candidates = remaining,
                Reason = $"{previous.Reason}; {reason}"
            };
        }

        // 全部排除，需要补充降级链
        var fallback = BuildFallbackChain(context.AllModels, previous.Candidates, excluded);
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
        IReadOnlySet<string> excludedModels)
    {
        // intentional-simple: 降级顺序 Strong -> Medium -> Cheap，同 tier 按 MaxContextTokens 降序
        var tierOrder = new[] { ModelTier.Strong, ModelTier.Medium, ModelTier.Cheap };

        foreach (var tier in tierOrder)
        {
            var sameTierFallback = allModels
                .Where(m => m.Enabled && m.Tier == tier && !excludedModels.Contains(m.Name))
                .OrderByDescending(m => m.MaxContextTokens)
                .ToList();

            if (sameTierFallback.Count > 0)
            {
                return sameTierFallback;
            }
        }

        // 保底：任何能用的最便宜模型
        var anyAvailable = allModels
            .Where(m => m.Enabled && !excludedModels.Contains(m.Name))
            .OrderBy(m => m.InputPricePerMillion)
            .ToList();

        return anyAvailable.Count > 0 ? anyAvailable : new List<ModelEndpointOptions>();
    }
}

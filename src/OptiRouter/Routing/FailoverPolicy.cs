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

        // 合并单请求内失败模型与跨请求熔断打开（冷却中）的模型。
        // 半开模型不排除：由 ProxyOrchestrator 通过探测槽位限流放行。
        var excluded = new HashSet<string>(context.FailedModels);
        List<string> coolingDown = new();
        List<string> halfOpen = new();
        foreach (var model in previous.Candidates)
        {
            var state = _healthTracker.GetState(model.Name);
            if (state == CircuitState.Open)
            {
                if (excluded.Add(model.Name))
                    coolingDown.Add(model.Name);
            }
            else if (state == CircuitState.HalfOpen)
            {
                halfOpen.Add(model.Name);
            }
        }

        if (excluded.Count == 0)
        {
            string halfOpenNote = halfOpen.Count > 0 ? $", half-open probing [{string.Join(", ", halfOpen)}]" : "";
            return previous with { Reason = $"{previous.Reason}; failover: no-failed-models{halfOpenNote}" };
        }

        var remaining = previous.Candidates
            .Where(m => !excluded.Contains(m.Name))
            .ToList();

        if (remaining.Count > 0)
        {
            string removed = string.Join(", ", previous.Candidates.Select(m => m.Name).Except(remaining.Select(m => m.Name)));
            string coolingNote = coolingDown.Count > 0 ? $", cooling [{string.Join(", ", coolingDown)}]" : "";
            string halfOpenNote = halfOpen.Count > 0 ? $", half-open probing [{string.Join(", ", halfOpen)}]" : "";
            string reason = removed.Length > 0
                ? $"failover: removed failed [{removed}]{coolingNote}{halfOpenNote}, {remaining.Count} remaining"
                : $"failover: no candidates removed{coolingNote}{halfOpenNote}";
            return previous with
            {
                Candidates = remaining,
                Reason = $"{previous.Reason}; {reason}"
            };
        }

        // 全部排除，需要补充降级链。
        // 感知原决策 tier：原 tier 失败后优先升档（如 Cheap 失败先试 Medium 再 Strong），
        // 而非固定 Strong->Medium->Cheap 顺序（否则 Cheap 失败会跳到最贵的 Strong，跳过 Medium）。
        var originalTier = previous.Candidates.Count > 0 ? previous.Candidates[0].Tier : ModelTier.Medium;
        var fallback = BuildFallbackChain(context.AllModels, previous.Candidates, excluded, originalTier);
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
        IReadOnlySet<string> excludedModels,
        ModelTier originalTier)
    {
        // 降级顺序感知原决策 tier：原 tier 失败后优先升档（Cheap 失败先试 Medium 再 Strong），
        // 而非固定 Strong->Medium->Cheap。这样 Cheap 失败不会直接跳到最贵的 Strong 跳过 Medium。
        // 同 tier 按 MaxContextTokens 降序。
        var originalRank = TierOrder.Rank(originalTier);
        var orderedTiers = TierOrder.FallbackChain
            .Where(t => t != originalTier)
            .OrderBy(t => Math.Abs(TierOrder.Rank(t) - originalRank))
            .ToList();

        foreach (var tier in orderedTiers)
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

using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 离线语义路由策略。支持 TF-IDF、稳定特征哈希与 Hybrid 两阶段匹配。
/// </summary>
public sealed class SemanticRouterPolicy : IRouterPolicy
{
    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Classify;

    private readonly ISemanticVectorEngine _vectorEngine;

    /// <summary>
    /// 初始化语义路由策略。
    /// </summary>
    /// <param name="vectorEngine">语义向量匹配引擎，为空则默认使用 <see cref="HybridSemanticVectorEngine"/>。</param>
    public SemanticRouterPolicy(ISemanticVectorEngine? vectorEngine = null)
    {
        _vectorEngine = vectorEngine ?? new HybridSemanticVectorEngine();
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var options = context.Options.Routing;
        if (!options.EnableSemanticRouter || options.SemanticRoutes is null || options.SemanticRoutes.Count == 0)
        {
            return previous.Append("semantic-router", "disabled");
        }

        string queryText = GetQueryText(context.Request);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return previous;
        }

        ISemanticVectorEngine effectiveEngine = GetEffectiveEngine(options);
        var (matchedRoute, maxSimilarity) = effectiveEngine.Match(queryText, options.SemanticRoutes);

        if (matchedRoute is not null && maxSimilarity >= options.SemanticSimilarityThreshold)
        {
            // Classify 组中的规则策略可能已把 previous.Candidates 缩到单一 tier。
            // 语义覆盖应在 RouterEngine 传入的、已经过全部 Filter 策略收缩的资格池上重新选 tier，
            // 这样既能跨越规则 tier，又绝不会带回能力/上下文/故障过滤掉的模型。
            var candidates = FilterByTier(context.AllModels, matchedRoute.TargetTier);
            if (candidates.Count > 0)
            {
                candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();
                var withCandidates = previous with
                {
                    Candidates = candidates,
                    ClassificationSignal = $"semantic:{matchedRoute.Name}",
                    ClassificationTargetTier = matchedRoute.TargetTier
                };
                return withCandidates.Append("semantic-router", $"matched={matchedRoute.Name}(sim={maxSimilarity:F4}, tier={matchedRoute.TargetTier}), {candidates.Count} candidates");
            }

            // 匹配命中但资格池无目标 tier 候选：不覆盖上游过滤结果，
            // 记独立 reason 区分于真正无匹配（no-match 误导：实际匹配了但零 tier 候选）。
            return previous.Append("semantic-router", $"matched={matchedRoute.Name}(sim={maxSimilarity:F4}, tier={matchedRoute.TargetTier}) but 0 tier candidates, unchanged");
        }

        return previous.Append("semantic-router", $"no-match(max_sim={maxSimilarity:F4})");
    }

    private ISemanticVectorEngine GetEffectiveEngine(RoutingOptions options)
    {
        if (_vectorEngine is HybridSemanticVectorEngine hybrid)
        {
            if (string.Equals(options.SemanticRouterMode, "TfIdf", StringComparison.OrdinalIgnoreCase))
            {
                return hybrid.SparseEngine;
            }
            if (string.Equals(options.SemanticRouterMode, "Dense", StringComparison.OrdinalIgnoreCase))
            {
                return hybrid.DenseEngine;
            }
            if (Math.Abs(options.HybridHighConfidenceThreshold - hybrid.HighConfidenceThreshold) > 1e-6)
            {
                return new HybridSemanticVectorEngine(hybrid.SparseEngine, hybrid.DenseEngine, options.HybridHighConfidenceThreshold);
            }
        }

        return _vectorEngine;
    }

    private static string GetQueryText(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return string.Empty;

        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg is not null && msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                var text = msg.GetText();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }

        return request.Messages[^1]?.GetText() ?? string.Empty;
    }

    private static List<ModelEndpointOptions> FilterByTier(IReadOnlyList<ModelEndpointOptions> models, ModelTier tier)
    {
        return models.Where(m => m.Enabled && m.Tier == tier).ToList();
    }
}

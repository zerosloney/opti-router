using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 向量空间语义路由策略。支持离线 TF-IDF 词袋模型与可扩展向量匹配引擎，
/// 支撑 100% 离线、高吞吐、零延迟、Native AOT 兼容的智能意图路由。
/// </summary>
public sealed class SemanticRouterPolicy : IRouterPolicy
{
    private readonly ISemanticVectorEngine _vectorEngine;

    /// <summary>
    /// 初始化语义路由策略。
    /// </summary>
    /// <param name="vectorEngine">语义向量匹配引擎，为空则默认使用 <see cref="TfIdfSemanticVectorEngine"/>。</param>
    public SemanticRouterPolicy(ISemanticVectorEngine? vectorEngine = null)
    {
        _vectorEngine = vectorEngine ?? new TfIdfSemanticVectorEngine();
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var options = context.Options.Routing;
        if (!options.EnableSemanticRouter || options.SemanticRoutes is null || options.SemanticRoutes.Count == 0)
        {
            return previous with { Reason = $"{previous.Reason}; semantic-router: disabled" };
        }

        string queryText = GetQueryText(context.Request);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return previous;
        }

        var (matchedRoute, maxSimilarity) = _vectorEngine.Match(queryText, options.SemanticRoutes);

        if (matchedRoute is not null && maxSimilarity >= options.SemanticSimilarityThreshold)
        {
            var candidates = FilterByTier(previous.Candidates, matchedRoute.TargetTier);
            if (candidates.Count > 0)
            {
                candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();
                string reason = $"semantic-router: matched={matchedRoute.Name}(sim={maxSimilarity:F4}, tier={matchedRoute.TargetTier}), {candidates.Count} candidates";

                return previous with
                {
                    Candidates = candidates,
                    Reason = $"{previous.Reason}; {reason}"
                };
            }
        }

        return previous with { Reason = $"{previous.Reason}; semantic-router: no-match(max_sim={maxSimilarity:F4})" };
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

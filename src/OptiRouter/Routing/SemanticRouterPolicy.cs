using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 向量空间语义路由策略。支持离线 TF-IDF 词袋模型与可扩展向量匹配引擎，
/// 支撑 100% 离线、高吞吐、零延迟、Native AOT 兼容的智能意图路由。
/// </summary>
public sealed class SemanticRouterPolicy : IRouterPolicy
{
    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Classify;

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
            string detail = "semantic-router: disabled";
            return previous with
            {
                Reason = $"{previous.Reason}; {detail}",
                ReasonEvents = previous.ReasonEvents.Append(new ReasonEvent("semantic-router", detail)).ToList()
            };
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
                    Reason = $"{previous.Reason}; {reason}",
                    ReasonEvents = previous.ReasonEvents.Append(new ReasonEvent("semantic-router", reason)).ToList()
                };
            }

            // 匹配命中但 previous.Candidates 无目标 tier 候选：不覆盖上游过滤结果，
            // 记独立 reason 区分于真正无匹配（no-match 误导：实际匹配了但零 tier 候选）。
            string noTierDetail = $"semantic-router: matched={matchedRoute.Name}(sim={maxSimilarity:F4}, tier={matchedRoute.TargetTier}) but 0 tier candidates, unchanged";
            return previous with
            {
                Reason = $"{previous.Reason}; {noTierDetail}",
                ReasonEvents = previous.ReasonEvents.Append(new ReasonEvent("semantic-router", noTierDetail)).ToList()
            };
        }

        string noMatchDetail = $"semantic-router: no-match(max_sim={maxSimilarity:F4})";
        return previous with
        {
            Reason = $"{previous.Reason}; {noMatchDetail}",
            ReasonEvents = previous.ReasonEvents.Append(new ReasonEvent("semantic-router", noMatchDetail)).ToList()
        };
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

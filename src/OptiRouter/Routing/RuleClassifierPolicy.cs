using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 规则分级策略：根据请求特征推断目标能力分档。
/// </summary>
/// <remarks>
/// intentional-simple: 以下阈值是经验值，用于快速路由分级。
/// 如需更精确，可替换为轻量级分类模型或更复杂的启发式。
/// </remarks>
public sealed class RuleClassifierPolicy : IRouterPolicy
{
    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableRuleClassifier)
        {
            return previous with { Reason = $"{previous.Reason}; rule-classifier: disabled" };
        }

        var (targetTier, targetReason) = ClassifyRequest(context);
        var candidates = FilterByTier(context.AllModels, targetTier);

        if (candidates.Count == 0)
        {
            // 回落到 DefaultTier
            targetTier = context.Options.Routing.DefaultTier;
            targetReason = "fallback-to-default";
            candidates = FilterByTier(context.AllModels, targetTier);
        }

        // 按 MaxContextTokens 降序
        candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();

        string reason = $"rule-classifier: target={targetTier}({targetReason}), {candidates.Count} candidates";

        return previous with
        {
            Candidates = candidates,
            Reason = $"{previous.Reason}; {reason}"
        };
    }

    private static (ModelTier Tier, string Reason) ClassifyRequest(RouterContext context)
    {
        var request = context.Request;
        bool hasCode = false;
        int totalMessageCount = request.Messages?.Count ?? 0;
        bool hasLongSystemPrompt = false;

        foreach (var msg in request.Messages ?? Enumerable.Empty<Clients.ChatMessage>())
        {
            if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase) && msg.Content.Length > 2000)
            {
                hasLongSystemPrompt = true;
            }

            if (ContainsCodeIndicators(msg.Content))
            {
                hasCode = true;
            }
        }

        bool isSingleShortMessage = totalMessageCount == 1
            && (request.Messages?.FirstOrDefault()?.Content.Length ?? 0) < 100
            && !hasCode;

        if (hasCode)
        {
            return (ModelTier.Strong, "code-detected");
        }

        if (totalMessageCount > 1 && hasLongSystemPrompt)
        {
            return (ModelTier.Strong, "complex-instruction");
        }

        if (isSingleShortMessage)
        {
            return (ModelTier.Cheap, "simple-qa");
        }

        return (context.Options.Routing.DefaultTier, "default");
    }

    private static bool ContainsCodeIndicators(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;

        // intentional-simple: 常见代码标记
        ReadOnlySpan<string> indicators = new[]
        {
            "```",
            "function ",
            "def ",
            "class ",
            "public ",
            "import "
        };

        foreach (var indicator in indicators)
        {
            if (content.Contains(indicator, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static List<ModelEndpointOptions> FilterByTier(
        IReadOnlyList<ModelEndpointOptions> models,
        ModelTier tier)
    {
        return models
            .Where(m => m.Enabled && m.Tier == tier)
            .ToList();
    }
}

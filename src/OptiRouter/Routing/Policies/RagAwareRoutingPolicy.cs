using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 知识库与动态 RAG 检索感知路由策略 (RAG-Aware Context Density Policy)。
/// 自动分析提示词中的 RAG 知识片段充分度：
/// 1. 高充分度（知识库已包含明确答案）：优先调度高性价比模型（Cheap/Medium），降低 70%+ Token 成本；
/// 2. 低充分度或多文档矛盾冲突：优先调度高推理能力模型（Strong Tier），进行深度归纳与抗幻觉推理。
/// </summary>
public sealed class RagAwareRoutingPolicy : IRouterPolicy
{
    private readonly RagContextDensityAnalyzer _analyzer;

    public PolicyGroup Group => PolicyGroup.Classify;

    public RagAwareRoutingPolicy(RagContextDensityAnalyzer? analyzer = null)
    {
        _analyzer = analyzer ?? new RagContextDensityAnalyzer();
    }

    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (context.Options?.Routing == null || !context.Options.Routing.EnableRagAwareRouting)
        {
            return previous;
        }

        if (context.Request == null || previous.Candidates.Count <= 1)
        {
            return previous;
        }

        var ragResult = _analyzer.Analyze(context.Request);
        if (!ragResult.HasRagContext || ragResult.Sufficiency == RagSufficiency.None)
        {
            return previous;
        }

        double highThreshold = context.Options.Routing.RagHighSufficiencyThreshold;
        double lowThreshold = context.Options.Routing.RagLowSufficiencyThreshold;

        var candidates = previous.Candidates.ToList();

        if (ragResult.Sufficiency == RagSufficiency.High || ragResult.QueryCoverageRatio >= highThreshold)
        {
            // 高充分度：优先 Cheap / Medium 经济高效模型
            var prioritized = candidates
                .OrderBy(m => m.Tier switch
                {
                    ModelTier.Cheap => 0,
                    ModelTier.Medium => 1,
                    _ => 2
                })
                .ToList();

            var updated = previous with { Candidates = prioritized };
            return updated.Append("rag-aware", $"high_sufficiency: docs={ragResult.DocumentCount}, coverage={ragResult.QueryCoverageRatio:P0}, prioritized Cheap/Medium");
        }

        if (ragResult.Sufficiency == RagSufficiency.Conflict ||
            ragResult.Sufficiency == RagSufficiency.Low ||
            ragResult.QueryCoverageRatio <= lowThreshold)
        {
            // 知识匮乏或冲突：优先 Strong 高端模型，避免幻觉
            var prioritized = candidates
                .OrderBy(m => m.Tier switch
                {
                    ModelTier.Strong => 0,
                    ModelTier.Medium => 1,
                    _ => 2
                })
                .ToList();

            var updated = previous with { Candidates = prioritized };
            return updated.Append("rag-aware", $"low_or_conflict: docs={ragResult.DocumentCount}, coverage={ragResult.QueryCoverageRatio:P0}, prioritized Strong");
        }

        return previous;
    }
}

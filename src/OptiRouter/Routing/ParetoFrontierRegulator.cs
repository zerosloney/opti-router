using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 帕累托前沿候选模型评估结果。
/// </summary>
public sealed record ParetoCandidate(
    ModelEndpointOptions Model,
    double EstimatedCostUsd,
    double QualityScore,
    bool IsParetoDominated,
    double UtilityScore);

/// <summary>
/// 模型 Cost-Quality 帕累托前沿动态调节器 (Pareto Frontier Cost-Quality Regulator)。
/// 计算所有候选模型的成本-质量二维坐标，识别被劣化的非帕累托最优候选（Pareto-Dominated），
/// 并通过可调权重因子 λ \in [0, 1] 在质量最大化 (λ=1) 与成本最小化 (λ=0) 之间动态折中重排候选链。
/// </summary>
public sealed class ParetoFrontierRegulator
{
    /// <summary>
    /// 根据模型 Tier 获取基础质量评分 Q \in [0.0, 1.0]。
    /// </summary>
    public static double GetModelQualityScore(ModelEndpointOptions model)
    {
        return model.Tier switch
        {
            ModelTier.Strong => 1.0,
            ModelTier.Medium => 0.8,
            ModelTier.Cheap => 0.5,
            _ => 0.7
        };
    }

    /// <summary>
    /// 评估候选模型集合并构建帕累托前沿。
    /// </summary>
    /// <param name="candidates">候选模型列表。</param>
    /// <param name="estimatedTokens">预估请求 token 数。</param>
    /// <param name="qualityWeight">质量-成本折中权重 λ \in [0, 1]。1.0=质量优先, 0.0=成本优先。</param>
    /// <returns>计算后的帕累托候选评估结构。</returns>
    public List<ParetoCandidate> EvaluateCandidates(
        IReadOnlyList<ModelEndpointOptions> candidates,
        int estimatedTokens,
        double qualityWeight)
    {
        if (candidates == null || candidates.Count == 0)
            return new List<ParetoCandidate>();

        int safeTokens = Math.Max(100, estimatedTokens);
        double lambda = Math.Clamp(qualityWeight, 0.0, 1.0);

        var list = new List<ParetoCandidate>(candidates.Count);
        double maxCost = 0.0;

        // 1. 计算每个模型的成本与质量
        foreach (var m in candidates)
        {
            double cost = (double)(safeTokens * m.InputPricePerMillion / 1_000_000m);
            double quality = GetModelQualityScore(m);
            if (cost > maxCost) maxCost = cost;

            list.Add(new ParetoCandidate(m, cost, quality, false, 0.0));
        }

        double safeMaxCost = maxCost > 0 ? maxCost : 1.0;

        // 2. 识别帕累托支配状态 (Pareto Dominated)
        // 模型 B 支配模型 A ，当且仅当: Cost(B) <= Cost(A) 且 Quality(B) >= Quality(A)，且至少一项严格优于 A
        var evaluated = new List<ParetoCandidate>(list.Count);

        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            bool isDominated = false;

            for (int j = 0; j < list.Count; j++)
            {
                if (i == j) continue;
                var b = list[j];

                bool costBetterOrEqual = b.EstimatedCostUsd <= a.EstimatedCostUsd;
                bool qualityBetterOrEqual = b.QualityScore >= a.QualityScore;
                bool strictlyBetterInOne = b.EstimatedCostUsd < a.EstimatedCostUsd || b.QualityScore > a.QualityScore;

                if (costBetterOrEqual && qualityBetterOrEqual && strictlyBetterInOne)
                {
                    isDominated = true;
                    break;
                }
            }

            // 3. 计算综合 Utility = \lambda * Quality - (1 - \lambda) * (Cost / MaxCost)
            double normCost = a.EstimatedCostUsd / safeMaxCost;
            double utility = lambda * a.QualityScore - (1.0 - lambda) * normCost;

            evaluated.Add(a with { IsParetoDominated = isDominated, UtilityScore = utility });
        }

        return evaluated;
    }
}

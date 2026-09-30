using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 时序预测主动避浪策略 (Predictive Resilience Policy)。
/// 根据历史分钟桶的时序拥塞与错误率模型，预测各候选模型在接下来的拥塞风险，
/// 主动对高风险候选进行平滑降权与避让重排。
/// </summary>
public sealed class PredictiveResiliencePolicy : IRouterPolicy
{
    private readonly PredictiveResilienceEngine _engine;

    public PredictiveResiliencePolicy(PredictiveResilienceEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Order;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var routing = context.Options.Routing;
        if (!routing.EnablePredictiveResilience)
        {
            return previous.Append("predictive-resilience", "disabled");
        }

        if (previous.Candidates.Count < 2)
        {
            return previous.Append("predictive-resilience", "<2 candidates");
        }

        var riskScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool hasHighRisk = false;

        foreach (var candidate in previous.Candidates)
        {
            double risk = _engine.PredictCongestionRisk(candidate.Name, routing.PredictiveLookaheadMinutes);
            riskScores[candidate.Name] = risk;
            if (risk >= 0.30)
            {
                hasHighRisk = true;
            }
        }

        if (!hasHighRisk)
        {
            return previous.Append("predictive-resilience", "all candidates low congestion risk");
        }

        // 按安全系数 (1.0 - Risk) 进行降序重排（低风险候选优先）
        var reordered = previous.Candidates
            .OrderByDescending(c => 1.0 - riskScores.GetValueOrDefault(c.Name, 0.0))
            .ToList();

        var withResult = previous with { Candidates = reordered };
        var top = reordered[0];
        double topRisk = riskScores.GetValueOrDefault(top.Name, 0.0);
        string reason = $"reordered by temporal safety: top='{top.Name}' (predicted_risk={topRisk:P1})";

        return withResult.Append("predictive-resilience", reason);
    }
}

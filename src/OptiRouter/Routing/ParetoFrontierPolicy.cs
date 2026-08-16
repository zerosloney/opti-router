using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 帕累托前沿 Cost-Quality 动态调节策略。
/// 基于候选模型的预估成本与质量分数，沿帕累托最优边界按 Utility 降序重新排列候选，
/// 或严格过滤掉被 Pareto 支配的劣质高价候选。
/// </summary>
public sealed class ParetoFrontierPolicy : IRouterPolicy
{
    private readonly ParetoFrontierRegulator _regulator = new();

    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Order;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var routing = context.Options.Routing;
        if (!routing.EnableParetoFrontierRegulator)
        {
            return previous.Append("pareto-frontier", "disabled");
        }

        if (previous.Candidates.Count < 2)
        {
            return previous.Append("pareto-frontier", "<2 candidates");
        }

        double lambda = routing.ParetoQualityWeight;
        var evaluated = _regulator.EvaluateCandidates(previous.Candidates, previous.EstimatedInputTokens, lambda);

        List<ParetoCandidate> candidatesToOrder = evaluated;
        if (routing.ParetoStrictFrontierFilter)
        {
            // 严格模式：过滤掉所有被 Pareto 支配的劣势模型（保证池内无“又贵又差”的模型）
            var nonDominated = evaluated.Where(c => !c.IsParetoDominated).ToList();
            if (nonDominated.Count > 0)
            {
                candidatesToOrder = nonDominated;
            }
        }

        // 按 UtilityScore 降序排列
        var reordered = candidatesToOrder
            .OrderByDescending(c => c.UtilityScore)
            .Select(c => c.Model)
            .ToList();

        var withResult = previous with { Candidates = reordered };
        string reason = routing.ParetoStrictFrontierFilter
            ? $"pareto frontier filtered (lambda={lambda:F2})"
            : $"pareto frontier utility ordered (lambda={lambda:F2})";

        return withResult.Append("pareto-frontier", reason);
    }
}

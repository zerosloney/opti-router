using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 融合四策略的路由引擎。
/// </summary>
public sealed class RouterEngine
{
    private readonly CostLedger _ledger;
    private readonly IReadOnlyList<IRouterPolicy> _policies;
    private readonly ITokenEstimator _tokenEstimator;

    /// <summary>
    /// 构造路由引擎。
    /// </summary>
    /// <param name="ledger">成本账本。</param>
    /// <param name="policies">策略链，按顺序应用。</param>
    /// <param name="tokenEstimator">token 估算器；不传则用分桶粗估（保持既有测试行为）。</param>
    public RouterEngine(CostLedger ledger, IEnumerable<IRouterPolicy> policies, ITokenEstimator? tokenEstimator = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _policies = policies?.ToList() ?? throw new ArgumentNullException(nameof(policies));
        _tokenEstimator = tokenEstimator ?? new BucketTokenEstimator();
    }

    /// <summary>
    /// 决策：给定请求和配置，返回候选模型链。
    /// </summary>
    public RouterDecision Decide(ChatRequest request, RouterOptions options, IReadOnlySet<string>? failedModels = null, string? sessionId = null)
    {
        // 1. 估算 token
        int estTokens = _tokenEstimator.Estimate(request);

        // 2. 构造初始 context
        var context = new RouterContext
        {
            Request = request,
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = estTokens,
            FailedModels = failedModels ?? new HashSet<string>(),
            SessionId = sessionId
        };

        // Keep a monotonic eligibility pool for the complete policy chain. Filter
        // policies narrow this pool; later policies can only select from it. This
        // prevents fallback/degrade policies from rebuilding candidates from the
        // original enabled-model list and undoing an earlier hard filter.
        var eligibleModels = context.AllModels.ToList();

        // 3. 初始决策：所有 enabled 模型按 tier 升序（Strong 优先）作为候选
        var initialCandidates = eligibleModels
            .OrderBy(m => TierOrder.Rank(m.Tier))
            .ThenByDescending(m => m.MaxContextTokens)
            .ToList();

        var decision = new RouterDecision
        {
            Candidates = initialCandidates,
            Reason = $"initial: {initialCandidates.Count} candidates, est {estTokens} tokens",
            EstimatedInputTokens = estTokens
        };

        // 4. 按分组依赖序应用策略（Filter→Classify→Order→Constraint），组内保留串行。
        //    分组契约（PolicyGroup）是未来并行化的地基；当前组内串行以保留叠加过滤/fallback/重排语义。
        var groups = new[] { PolicyGroup.Filter, PolicyGroup.Classify, PolicyGroup.Order, PolicyGroup.Constraint };
        foreach (var group in groups)
        {
            foreach (var policy in _policies.Where(p => p.Group == group))
            {
                // All policies see the current eligible pool. A filter policy may
                // only narrow it; every result is intersected defensively so a
                // policy that rebuilds from its own source cannot reintroduce a
                // model removed by an earlier filter.
                var policyContext = context with { AllModels = eligibleModels };
                var policyDecision = policy.Apply(policyContext, decision);
                var candidates = IntersectWithEligible(policyDecision.Candidates, eligibleModels);

                if (group == PolicyGroup.Filter)
                {
                    eligibleModels = candidates;
                }

                decision = policyDecision with { Candidates = candidates };
            }
        }

        return decision;
    }

    private static List<ModelEndpointOptions> IntersectWithEligible(
        IReadOnlyList<ModelEndpointOptions> candidates,
        IReadOnlyList<ModelEndpointOptions> eligibleModels)
    {
        var eligibleNames = eligibleModels
            .Select(model => model.Name)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(model => eligibleNames.Contains(model.Name))
            .ToList();
    }
}

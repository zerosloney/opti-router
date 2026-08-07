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

        // 3. 初始决策：所有 enabled 模型按 tier 升序（Strong 优先）作为候选
        var initialCandidates = context.AllModels
            .OrderBy(m => TierOrder.Rank(m.Tier))
            .ThenByDescending(m => m.MaxContextTokens)
            .ToList();

        var decision = new RouterDecision
        {
            Candidates = initialCandidates,
            Reason = $"initial: {initialCandidates.Count} candidates, est {estTokens} tokens",
            EstimatedInputTokens = estTokens
        };

        // 4. 顺序应用每个策略，每个策略可能调整 decision
        foreach (var policy in _policies)
        {
            decision = policy.Apply(context, decision);
        }

        return decision;
    }
}

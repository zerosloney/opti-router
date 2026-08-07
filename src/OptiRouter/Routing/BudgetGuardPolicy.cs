using System.Collections.Generic;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 预算守卫策略：检查日/会话预算是否耗尽。
/// </summary>
public sealed class BudgetGuardPolicy : IRouterPolicy
{
    private readonly CostLedger _ledger;

    /// <summary>
    /// 构造预算守卫策略。
    /// </summary>
    public BudgetGuardPolicy(CostLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableBudgetGuard)
        {
            return previous with { Reason = $"{previous.Reason}; budget-guard: disabled" };
        }

        var (dailySpend, _) = _ledger.GetSpend();
        var budget = context.Options.Budget;

        bool dailyExhausted = budget.DailyBudgetUsd > 0 && dailySpend >= budget.DailyBudgetUsd;
        // 会话预算仅在 X-Session-Id 头存在时启用；缺头时 sessionSpend 记为未超。
        decimal sessionSpend = context.SessionId is { } sid ? _ledger.GetSessionSpend(sid) : 0m;
        bool sessionExhausted = context.SessionId is not null
            && budget.SessionBudgetUsd is { } sessionBudget
            && sessionSpend >= sessionBudget;

        if (!dailyExhausted && !sessionExhausted)
        {
            string sessionInfo = context.SessionId is not null
                ? $"session={sessionSpend:F4}/{(budget.SessionBudgetUsd?.ToString("F4") ?? "inf")}"
                : "session=disabled(no-header)";
            string spendInfo = $"budget-guard: daily={dailySpend:F4}/{budget.DailyBudgetUsd:F4}, {sessionInfo}";
            return previous with { Reason = $"{previous.Reason}; {spendInfo}" };
        }

        // 预算耗尽
        if (budget.EnforceOnExhausted == BudgetExhaustionMode.Reject)
        {
            return previous with
            {
                Candidates = Array.Empty<ModelEndpointOptions>(),
                BudgetExhausted = true,
                Reason = $"{previous.Reason}; budget-guard: budget exhausted, reject (daily={dailySpend:F4}, session={sessionSpend:F4})"
            };
        }

        // Degrade：优先 Cheap tier，从全部 enabled 模型构建降级链，但排除装不下输入的模型
        // 硬下限用 EstimatedInputTokens（1.0 倍，不加余量）；LongInputPolicy 的 1.2 余量是优化建议，降级兜底接受极限。
        // intentional-simple: 与 LongInputPolicy 的上下文判断有重复，但耦合两策略成本更高；此处局部硬下限可接受。
        var cheapFitting = context.AllModels
            .Where(m => m.Tier == ModelTier.Cheap && m.MaxContextTokens >= context.EstimatedInputTokens)
            .OrderBy(m => m.InputPricePerMillion)
            .ToList();

        List<ModelEndpointOptions> degradedCandidates;
        string degradeReason;

        if (cheapFitting.Count > 0)
        {
            degradedCandidates = cheapFitting;
            var primary = degradedCandidates[0];
            degradeReason = $"budget-guard: degraded to cheap-tier chain (primary='{primary.Name}' tier={primary.Tier}, input={primary.InputPricePerMillion:F4}/M, daily={dailySpend:F4})";
        }
        else
        {
            // 没有 Cheap 能装下，放宽到任意能装下的 tier，按价格升序
            var anyFitting = context.AllModels
                .Where(m => m.MaxContextTokens >= context.EstimatedInputTokens)
                .OrderBy(m => m.InputPricePerMillion)
                .ToList();

            if (anyFitting.Count > 0)
            {
                degradedCandidates = anyFitting;
                var primary = degradedCandidates[0];
                degradeReason = $"budget-guard: no cheap-tier fits, degraded to cheapest fitting (primary='{primary.Name}' tier={primary.Tier}, input={primary.InputPricePerMillion:F4}/M, daily={dailySpend:F4})";
            }
            else
            {
                // 完全兜底：任何模型（即使装不下也比无候选强，让上游报上下文错）
                degradedCandidates = context.AllModels
                    .OrderBy(m => m.InputPricePerMillion)
                    .ToList();

                if (degradedCandidates.Count == 0)
                {
                    return previous with
                    {
                        Candidates = Array.Empty<ModelEndpointOptions>(),
                        Reason = $"{previous.Reason}; budget-guard: exhausted, no candidates available"
                    };
                }

                var primary = degradedCandidates[0];
                degradeReason = $"budget-guard: no model fits input, last-resort cheapest (primary='{primary.Name}', input={primary.InputPricePerMillion:F4}/M, daily={dailySpend:F4})";
            }
        }

        return previous with
        {
            Candidates = degradedCandidates,
            Reason = $"{previous.Reason}; {degradeReason}"
        };
    }
}

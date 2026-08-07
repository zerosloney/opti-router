using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 会话粘性路由策略：同 X-Session-Id 的多轮对话尽量命中上次成功的模型，避免风格/能力割裂。
/// </summary>
/// <remarks>
/// 决策层无 I/O 原则的例外：仅读内存缓存（IMemoryCache），不触网络/磁盘。
/// 粘性记录由 <c>ProxyOrchestrator</c> 在请求成功后回写本缓存。
/// 策略链位置在 RuleClassifier 之后、SemanticRouter 之前：粘性优先于重新分类，
/// 但 LongInput/BudgetGuard/Failover 仍可过滤掉装不下/熔断的粘性模型。
/// </remarks>
public sealed class SessionAffinityPolicy : IRouterPolicy
{
    /// <summary>缓存键前缀，与 ProxyOrchestrator 回写侧保持一致。</summary>
    public const string CacheKeyPrefix = "affinity:";

    private readonly IMemoryCache _cache;

    public SessionAffinityPolicy(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableSessionAffinity)
        {
            return previous with { Reason = $"{previous.Reason}; session-affinity: disabled" };
        }

        // 无 session 头则不参与粘性。
        if (string.IsNullOrEmpty(context.SessionId))
        {
            return previous with { Reason = $"{previous.Reason}; session-affinity: no-session" };
        }

        string key = CacheKeyPrefix + context.SessionId;
        if (!_cache.TryGetValue<string>(key, out var remembered) || string.IsNullOrEmpty(remembered))
        {
            return previous with { Reason = $"{previous.Reason}; session-affinity: no-record" };
        }

        // 记忆模型已在本请求失败 → 不强推，交给 Failover。
        if (context.FailedModels.Contains(remembered))
        {
            return previous with { Reason = $"{previous.Reason}; session-affinity: remembered '{remembered}' failed, skipped" };
        }

        // 记忆模型不在当前候选链（可能被 LongInput/Budget 过滤）→ 不破坏候选，透传。
        int idx = -1;
        for (int i = 0; i < previous.Candidates.Count; i++)
        {
            if (string.Equals(previous.Candidates[i].Name, remembered, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }

        if (idx <= 0)
        {
            // idx==0 已在首位无需调整；idx==-1 不在候选。
            string note = idx == -1 ? $"remembered '{remembered}' not in candidates" : $"already primary '{remembered}'";
            return previous with { Reason = $"{previous.Reason}; session-affinity: {note}" };
        }

        // 提升到首位，其余保持相对顺序。
        var reordered = new List<ModelEndpointOptions>(previous.Candidates.Count);
        reordered.Add(previous.Candidates[idx]);
        for (int i = 0; i < previous.Candidates.Count; i++)
        {
            if (i != idx) reordered.Add(previous.Candidates[i]);
        }

        return previous with
        {
            Candidates = reordered,
            Reason = $"{previous.Reason}; session-affinity: promoted '{remembered}' to primary"
        };
    }
}

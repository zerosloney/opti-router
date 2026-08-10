using Microsoft.Extensions.Caching.Memory;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 粘性信号强度：决定写入端是否应覆盖已有粘性记录。
/// <list type="bullet">
/// <item><see cref="Strong"/>：主链成功（明确的会话偏好），总是覆盖写入。</item>
/// <item><see cref="Weak"/>：旁路成功（Cascade/Fusion/Race），仅当已有粘性缺失或过期时才写入，
/// 避免旁路的偶发/升级路径覆盖主链的稳定偏好。</item>
/// </list>
/// </summary>
public enum AffinitySignal
{
    /// <summary>主链成功：明确的会话偏好，覆盖已有记录。</summary>
    Strong,

    /// <summary>旁路成功：仅在无有效粘性或已过期时接管，避免破坏主链偏好。</summary>
    Weak
}

/// <summary>
/// 会话粘性的存储值：模型名 + 最近成功写入时间。
/// 时间戳用于写入端判断粘性是否"新鲜"——弱信号不能在主链刚写入的粘性上覆盖。
/// </summary>
public sealed record AffinityRecord(string ModelName, DateTimeOffset UpdatedAt);

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
        if (!_cache.TryGetValue<AffinityRecord>(key, out var record) || record is null || string.IsNullOrEmpty(record.ModelName))
        {
            return previous with { Reason = $"{previous.Reason}; session-affinity: no-record" };
        }
        string remembered = record.ModelName;

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

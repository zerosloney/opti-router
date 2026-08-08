using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 延迟感知路由策略：同 tier 段内按历史平均延迟升序重排（快模型优先），跨 tier 顺序不变。
/// </summary>
/// <remarks>
/// 策略链位置在 LongInput 之后、BudgetGuard/Failover 之前：
/// 先排除装不下/熔断的模型，再按延迟择优。
/// <para>
/// 样本数低于 <see cref="RoutingOptions.LatencyMinSamples"/> 的模型不参与排序（噪声大），
/// 追加到该 tier 段尾部（保留原顺序），让有统计的模型优先。
/// 冷启动（无任何模型有统计）时整段透传，退回 MaxContextTokens 排序。
/// </para>
/// <para>
/// 策略只读 <see cref="ILatencyStatsProvider"/> 内存快照，零 I/O、零锁。
/// 后台 <c>LatencyStatsAggregatorService</c> 周期刷新快照。
/// </para>
/// </remarks>
public sealed class LatencyAwarePolicy : IRouterPolicy
{
    /// <summary>延迟分数的加性平滑地板，避免极低延迟导致分数爆炸、除零。</summary>
    private const double LatencyFloorMs = 50.0;

    private readonly ILatencyStatsProvider _statsProvider;
    private readonly ThompsonStateStore _tsStore;

    /// <summary>
    /// 构造延迟感知策略。
    /// </summary>
    /// <param name="statsProvider">延迟统计读接口（内存快照，零 I/O）。</param>
    /// <param name="tsStore">Thompson 采样参数存储中心。</param>
    public LatencyAwarePolicy(ILatencyStatsProvider statsProvider, ThompsonStateStore tsStore)
    {
        _statsProvider = statsProvider ?? throw new ArgumentNullException(nameof(statsProvider));
        _tsStore = tsStore ?? throw new ArgumentNullException(nameof(tsStore));
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableLatencyAware)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: disabled" };
        }

        if (previous.Candidates.Count < 2)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: <2 candidates" };
        }

        int minSamples = context.Options.Routing.LatencyMinSamples;

        // 按 tier 分段（与 LoadBalancePolicy 一致：候选链按 tier 升序，但下游策略可能打乱，按出现顺序分段）。
        var segments = SegmentByTier(previous.Candidates);

        var result = new List<ModelEndpointOptions>(previous.Candidates.Count);
        int segmentsReordered = 0;

        foreach (var seg in segments)
        {
            if (seg.Count < 2)
            {
                result.AddRange(seg);
                continue;
            }

            var reordered = ReorderSegment(seg, minSamples, context);
            if (!SameOrder(seg, reordered))
                segmentsReordered++;
            result.AddRange(reordered);
        }

        if (segmentsReordered == 0)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: no change" };
        }

        string extraTag = context.Options.Routing.EnableThompsonSampling ? " [Thompson Sampling]" : "";
        return previous with
        {
            Candidates = result,
            Reason = $"{previous.Reason}; latency-aware: reordered {segmentsReordered} tier segment(s){extraTag}"
        };
    }

    /// <summary>同 tier 段内：根据配置选择 Thompson 采样或延迟感知重排。</summary>
    private List<ModelEndpointOptions> ReorderSegment(List<ModelEndpointOptions> segment, int minSamples, RouterContext context)
    {
        if (context.Options.Routing.EnableThompsonSampling)
        {
            return ReorderByThompsonSampling(segment);
        }

        return ReorderByLatencyScore(segment, minSamples);
    }

    /// <summary>Beta 分布采样：Alpha/Beta 越高表示历史表现越好，采样值越高排序越靠前。</summary>
    private List<ModelEndpointOptions> ReorderByThompsonSampling(List<ModelEndpointOptions> segment)
    {
        var sampled = new List<(ModelEndpointOptions Model, double Sample)>(segment.Count);
        foreach (var m in segment)
        {
            var stats = _tsStore.GetOrAdd(m.Name);
            double alpha, beta;
            lock (stats.Lock)
            {
                alpha = stats.Alpha;
                beta = stats.Beta;
            }
            double val = ThompsonSampler.SampleBeta(alpha, beta);
            sampled.Add((m, val));
        }

        sampled.Sort((a, b) => b.Sample.CompareTo(a.Sample));
        return sampled.Select(s => s.Model).ToList();
    }

    /// <summary>同 tier 段内：有统计且样本充足的按延迟升序；无统计/样本不足的追加尾部（保持原顺序）。</summary>
    private List<ModelEndpointOptions> ReorderByLatencyScore(List<ModelEndpointOptions> segment, int minSamples)
    {
        // 拆分：ordered 参与延迟排序，tail 不参与（追加尾部保持原顺序）。
        var ordered = new List<(ModelEndpointOptions Model, double Score)>();
        var tail = new List<ModelEndpointOptions>();

        foreach (var m in segment)
        {
            var stats = _statsProvider.GetStats(m.Name);
            if (stats is not null && stats.SampleCount >= minSamples)
            {
                // 分数 = 1 / (avg + floor)。延迟越低分越高（排序时降序）。
                double score = 1.0 / (stats.AverageLatencyMs + LatencyFloorMs);
                ordered.Add((m, score));
            }
            else
            {
                tail.Add(m);
            }
        }

        if (ordered.Count == 0)
        {
            // 整段无统计：透传原顺序。
            return segment;
        }

        var result = new List<ModelEndpointOptions>(segment.Count);
        // 有统计的按分数降序（快模型在前）；并列时保持原相对顺序（List.Sort 稳定排序）。
        ordered.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach (var (model, _) in ordered)
            result.Add(model);
        result.AddRange(tail);
        return result;
    }

    private static List<List<ModelEndpointOptions>> SegmentByTier(IReadOnlyList<ModelEndpointOptions> candidates)
    {
        var segments = new List<List<ModelEndpointOptions>>();
        ModelTier? currentTier = null;
        foreach (var m in candidates)
        {
            if (currentTier is null || m.Tier != currentTier.Value)
            {
                segments.Add(new List<ModelEndpointOptions> { m });
                currentTier = m.Tier;
            }
            else
            {
                segments[^1].Add(m);
            }
        }
        return segments;
    }

    private static bool SameOrder(List<ModelEndpointOptions> a, List<ModelEndpointOptions> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!ReferenceEquals(a[i], b[i])) return false;
        }
        return true;
    }
}

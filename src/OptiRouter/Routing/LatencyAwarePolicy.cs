using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 延迟感知路由策略：同 tier 段内按历史平均延迟升序重排（快模型优先），跨 tier 顺序不变。
/// 当 <see cref="RoutingOptions.EnableThompsonSampling"/> 启用时，段内改用 Thompson Sampling 重排。
/// 当 <see cref="RoutingOptions.EnableContextualBandit"/> 启用时，段内改用 LinUCB 打分重排。
/// </summary>
/// <remarks>
/// 策略链位置在 LongInput 之后、BudgetGuard/Failover 之前：
/// 先排除装不下/熔断的模型，再按延迟择优。
/// <para>
/// 三个开关各自独立 gate：
/// <list type="bullet">
/// <item><see cref="RoutingOptions.EnableLatencyAware"/>：段内按历史平均延迟升序。</item>
/// <item><see cref="RoutingOptions.EnableThompsonSampling"/>：段内按 Beta 分布采样重排（自适应探索）。</item>
/// <item><see cref="RoutingOptions.EnableContextualBandit"/>：段内按 LinUCB 打分重排（带请求上下文）。</item>
/// </list>
/// 全部关闭时整段透传。优先级为 Contextual Bandit > Thompson > Latency（<c>ReorderSegment</c> 内判断）。
/// <see cref="RoutingOptions.EnableContextualBandit"/> 与 <see cref="RoutingOptions.EnableThompsonSampling"/>
/// 在 <c>RouterOptionsValidator</c> 启动期强制互斥（两者同时开启会被拒启动），故优先级在生产环境等价于互斥；
/// 段内的 if 顺序仅是防御性兜底，防运行时配置漂移。
/// </para>
/// <para>
/// 延迟路径下，样本数低于 <see cref="RoutingOptions.LatencyMinSamples"/> 的模型不参与排序（噪声大），
/// 追加到该 tier 段尾部（保留原顺序），让有统计的模型优先。
/// 冷启动（无任何模型有统计）时整段透传，退回 MaxContextTokens 排序。
/// </para>
/// <para>
/// 策略只读 <see cref="ILatencyStatsProvider"/> 内存快照与 <see cref="ThompsonStateStore"/>，零 I/O、零外部锁。
/// 后台 <c>LatencyStatsAggregatorService</c> 周期刷新延迟快照。
/// </para>
/// </remarks>
public sealed class LatencyAwarePolicy : IRouterPolicy
{
    /// <summary>延迟分数的加性平滑地板，避免极低延迟导致分数爆炸、除零。</summary>
    private const double LatencyFloorMs = 50.0;

    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Order;

    private readonly ILatencyStatsProvider _statsProvider;
    private readonly ThompsonStateStore _tsStore;
    private readonly ContextualBanditState? _banditStore;
    private readonly Func<double, double, double> _sampleBeta;
    private readonly Func<double> _sampleUniform;

    /// <summary>
    /// 构造延迟感知策略。
    /// </summary>
    /// <param name="statsProvider">延迟统计读接口（内存快照，零 I/O）。</param>
    /// <param name="tsStore">Thompson 采样参数存储中心。</param>
    /// <param name="sampleBeta">
    /// Thompson Beta 采样委托，默认 <see cref="ThompsonSampler.SampleBeta(double,double)"/>（线程本地 RNG）。
    /// 仅供测试注入确定性采样；生产路径留空。
    /// </param>
    /// <param name="banditStore">
    /// 上下文老虎机状态（LinUCB）。null（默认）= 不启用上下文 bandit（向后兼容）——
    /// 仅当 <see cref="RoutingOptions.EnableContextualBandit"/> 为 true 且传入非 null 时生效。
    /// </param>
    /// <param name="sampleUniform">
    /// 均匀 [0,1) 采样委托，用于 ε 探索保底，默认 <see cref="Random.Shared"/>。
    /// 仅供测试注入确定性采样；生产路径留空。
    /// </param>
    public LatencyAwarePolicy(
        ILatencyStatsProvider statsProvider,
        ThompsonStateStore tsStore,
        Func<double, double, double>? sampleBeta = null,
        ContextualBanditState? banditStore = null,
        Func<double>? sampleUniform = null)
    {
        _statsProvider = statsProvider ?? throw new ArgumentNullException(nameof(statsProvider));
        _tsStore = tsStore ?? throw new ArgumentNullException(nameof(tsStore));
        _sampleBeta = sampleBeta ?? ThompsonSampler.SampleBeta;
        _banditStore = banditStore;
        _sampleUniform = sampleUniform ?? Random.Shared.NextDouble;
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        bool latencyEnabled = context.Options.Routing.EnableLatencyAware;
        bool thompsonEnabled = context.Options.Routing.EnableThompsonSampling;
        bool banditEnabled = context.Options.Routing.EnableContextualBandit && _banditStore is not null;

        // 三个开关各自独立 gate：bandit 段内用 LinUCB、thompson 用 Beta 采样、latency 用历史均值。
        // bandit 与 thompson 在 RouterOptionsValidator 启动期互斥校验，运行时同时开启为非法配置；
        // 段内 if 顺序 bandit > thompson > latency 是防御性兜底（防配置漂移）。
        // 仅当三者都关闭时整体跳过（保持原 reason 文案对延迟感知的描述）。
        if (!latencyEnabled && !thompsonEnabled && !banditEnabled
            && context.Options.Routing.LatencyDegradeStrongP95Ms <= 0)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: disabled" };
        }

        if (previous.Candidates.Count < 2)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: <2 candidates" };
        }

        int minSamples = context.Options.Routing.LatencyMinSamples;

        // 强档延迟降级 pre-pass：把 p95 慢的 Strong 档候选移到 candidates 末尾，让段内 reorder
        // （Thompson/Bandit/SLA）和下游 failover 都不优先挑到它们。与 LongInputForceMedium 互补——
        // 后者按 prompt 长度屏蔽 Strong，本步骤按历史延迟屏蔽。与 LatencyAware 三个开关正交：
        // 即使下游 latency/thompson/bandit 全关，本步骤仍生效（因为它的语义独立）。
        int degradeThreshold = context.Options.Routing.LatencyDegradeStrongP95Ms;
        int degradedCount = 0;
        if (degradeThreshold > 0)
        {
            var (degradedCandidates, count) = DegradeSlowStrongCandidates(previous.Candidates, degradeThreshold, minSamples);
            if (count > 0)
            {
                degradedCount = count;
                previous = previous with { Candidates = degradedCandidates };
            }
        }

        // 按 tier 分段（与 LoadBalancePolicy 一致：候选链按 tier 升序，但下游策略可能打乱，按出现顺序分段）。
        var segments = SegmentByTier(previous.Candidates);

        var result = new List<ModelEndpointOptions>(previous.Candidates.Count);
        int segmentsReordered = 0;
        int exploredPromotions = 0;

        foreach (var seg in segments)
        {
            if (seg.Count < 2)
            {
                result.AddRange(seg);
                continue;
            }

            var reordered = ReorderSegment(seg, minSamples, context, previous);
            var (explored, promotedModel) = MaybePromoteTailForExploration(
                reordered,
                context.Options.Routing.ExplorationEpsilon,
                context.Options.Routing.ExplorationStarvedN);
            if (!ReferenceEquals(explored, reordered))
            {
                reordered = explored;
                exploredPromotions++;
                // 记录被提升的模型名到决策
                if (promotedModel != null)
                {
                    previous = previous with { EpsilonPromotedModel = promotedModel };
                }
            }
            if (!SameOrder(seg, reordered))
                segmentsReordered++;
            result.AddRange(reordered);
        }

        if (segmentsReordered == 0 && degradedCount == 0)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: no change" };
        }

        string extraTag = context.Options.Routing.EnableContextualBandit && _banditStore is not null
            ? " [Contextual Bandit]"
            : context.Options.Routing.EnableThompsonSampling ? " [Thompson Sampling]" : "";
        string exploreTag = exploredPromotions > 0 ? $", ε-explore promoted {exploredPromotions}" : "";
        string degradeTag = degradedCount > 0 ? $", degraded {degradedCount} slow-strong (p95>={degradeThreshold}ms)" : "";
        if (segmentsReordered == 0)
        {
            // 仅 pre-pass 生效、段内未重排：单独标 degradeTag。
            return previous with
            {
                Candidates = result,
                Reason = $"{previous.Reason}; latency-aware: no segment reorder{degradeTag}"
            };
        }
        return previous with
        {
            Candidates = result,
            Reason = $"{previous.Reason}; latency-aware: reordered {segmentsReordered} tier segment(s){extraTag}{exploreTag}{degradeTag}"
        };
    }

    /// <summary>
    /// 强档延迟降级 pre-pass：把 p95 慢的 Strong 档候选从原位移出、追加到 candidates 末尾，保持其余候选原顺序。
    /// 样本不足（&lt; minSamples）的强档不动——冷启动期间不误伤。返回 (新候选列表, 降级数量)。
    /// </summary>
    private (List<ModelEndpointOptions> Candidates, int DegradedCount) DegradeSlowStrongCandidates(
        IReadOnlyList<ModelEndpointOptions> candidates, int p95ThresholdMs, int minSamples)
    {
        if (p95ThresholdMs <= 0 || candidates.Count < 2) return (candidates.ToList(), 0);

        var keep = new List<ModelEndpointOptions>(candidates.Count);
        var degraded = new List<ModelEndpointOptions>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var m = candidates[i];
            if (m.Tier == ModelTier.Strong)
            {
                var stats = _statsProvider.GetStats(m.Name);
                if (stats is not null && stats.SampleCount >= minSamples && stats.P95LatencyMs >= p95ThresholdMs)
                {
                    degraded.Add(m);
                    continue;
                }
            }
            keep.Add(m);
        }
        if (degraded.Count == 0) return (candidates.ToList(), 0);

        var result = new List<ModelEndpointOptions>(candidates.Count);
        result.AddRange(keep);
        result.AddRange(degraded);
        return (result, degraded.Count);
    }

    /// <summary>
    /// ε 探索保底：概率 ε 把段内一个随机非首位模型提到段首。
    /// 修低流量下的"尾部锁死"反馈环——重排决定尝试顺序后，链尾模型只在异常场景被尝试，
    /// 样本全部来自异常场景，学习分长期偏低。ε>0 保证尾部模型持续获得真实流量样本。
    /// 当 starvedThreshold &gt; 0 时，优先从样本饥饿的模型中选（进程内 N 小于阈值）；
    /// 饥饿集合为空时回退均匀随机（保留探索保底语义）。
    /// 返回 (重排后的列表, 被提升的模型名)。无提升时返回 (原列表实例, null)，调用方通过引用相等区分。
    /// </summary>
    private (List<ModelEndpointOptions> Reordered, string? PromotedModel) MaybePromoteTailForExploration(
        List<ModelEndpointOptions> segment,
        double epsilon,
        long starvedThreshold)
    {
        if (epsilon <= 0 || epsilon > 1 || segment.Count < 2)
            return (segment, null);
        if (_sampleUniform() >= epsilon)
            return (segment, null);

        ModelEndpointOptions promoted;
        List<ModelEndpointOptions> result;
        int selectedIndex;

        // 定向探索：优先提升样本饥饿的模型
        if (starvedThreshold > 0)
        {
            // 筛出段内饥饿模型（N < 阈值）。N 为 long，tearing 风险忽略——读取偏差仅影响本次探索选型，不破坏正确性。
            var starvedIndices = new List<int>();
            for (int i = 0; i < segment.Count; i++)
            {
                var stats = _tsStore.GetOrAdd(segment[i].Name);
                // 只读 N，无需加锁（short lock 或不加锁均可，此处选择不加锁——单次读取性能优先）
                if (stats.N < starvedThreshold)
                    starvedIndices.Add(i);
            }

            if (starvedIndices.Count > 0)
            {
                // 饥饿集合非空：从饥饿模型中随机选一个提升（可能是当前首位，等价无变化，可接受）
                int starvedIndex = starvedIndices[(int)(_sampleUniform() * starvedIndices.Count)];
                starvedIndex = Math.Min(starvedIndex, starvedIndices.Count - 1);
                selectedIndex = starvedIndices[starvedIndex];

                promoted = segment[selectedIndex];
                result = new List<ModelEndpointOptions>(segment.Count) { promoted };
                for (int i = 0; i < segment.Count; i++)
                {
                    if (i != selectedIndex) result.Add(segment[i]);
                }
                return (result, promoted.Name);
            }

            // 饥饿集合为空：回退均匀随机（原逻辑）
        }

        // 阈值为 0 或饥饿集合为空时，保持原均匀随机逻辑
        selectedIndex = 1 + (int)(_sampleUniform() * (segment.Count - 1)); // 均匀取 [1, count-1]
        selectedIndex = Math.Min(selectedIndex, segment.Count - 1);

        promoted = segment[selectedIndex];
        result = new List<ModelEndpointOptions>(segment.Count) { promoted };
        for (int i = 0; i < segment.Count; i++)
        {
            if (i != selectedIndex) result.Add(segment[i]);
        }
        return (result, promoted.Name);
    }

    /// <summary>同 tier 段内：根据配置选择上下文 bandit / Thompson / 延迟感知 (SLA) 重排。</summary>
    private List<ModelEndpointOptions> ReorderSegment(
        List<ModelEndpointOptions> segment,
        int minSamples,
        RouterContext context,
        RouterDecision decision)
    {
        if (context.Options.Routing.EnableContextualBandit && _banditStore is not null)
        {
            return ReorderByContextualBandit(segment, decision, context.Options.Routing.ContextualBanditAlpha);
        }

        if (context.Options.Routing.EnableThompsonSampling)
        {
            return ReorderByThompsonSampling(segment);
        }

        return ReorderByLatencyScore(segment, minSamples, context.Options.Routing.DefaultSlaMode);
    }

    /// <summary>同 tier 段内：按 SLA 维度（Balanced / TTFT / TPS）计算得分降序排列。</summary>
    private List<ModelEndpointOptions> ReorderByLatencyScore(List<ModelEndpointOptions> segment, int minSamples, SlaMode slaMode)
    {
        var ordered = new List<(ModelEndpointOptions Model, double Score)>();
        var tail = new List<ModelEndpointOptions>();

        foreach (var m in segment)
        {
            var stats = _statsProvider.GetStats(m.Name);
            if (stats is not null && stats.SampleCount >= minSamples)
            {
                double score = slaMode switch
                {
                    SlaMode.Ttft => 1.0 / ((stats.AverageTtftMs > 0 ? stats.AverageTtftMs : stats.AverageLatencyMs) + LatencyFloorMs),
                    SlaMode.Tps => stats.AverageTps > 0 ? stats.AverageTps : (1000.0 / Math.Max(1.0, stats.AverageLatencyMs)),
                    _ => 1.0 / (stats.AverageLatencyMs + 0.5 * stats.P95LatencyMs + LatencyFloorMs)
                };
                ordered.Add((m, score));
            }
            else
            {
                tail.Add(m);
            }
        }

        if (ordered.Count == 0) return segment;

        var result = new List<ModelEndpointOptions>(segment.Count);
        ordered.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach (var (model, _) in ordered)
            result.Add(model);
        result.AddRange(tail);
        return result;
    }

    /// <summary>上下文老虎机（LinUCB）重排：用分类信号 + tier 构造上下文特征，每模型 LinUCB 打分降序。
    /// 修非上下文 Thompson 「只优化延迟、系统性低估 Strong」的缺陷——LinUCB 用请求特征学习「模型↔任务」匹配。
    /// </summary>
    private List<ModelEndpointOptions> ReorderByContextualBandit(
        List<ModelEndpointOptions> segment,
        RouterDecision decision,
        double alpha)
    {
        var feature = ContextualBanditFeatureBuilder.Build(decision);

        var scored = new List<(ModelEndpointOptions Model, double Score)>(segment.Count);
        foreach (var m in segment)
        {
            double score = _banditStore!.Predict(m.Name, feature, alpha);
            scored.Add((m, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => x.Model)
            .ToList();
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
            double val = _sampleBeta(alpha, beta);
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
                // 分数 = 1 / (avg + 0.5×p95 + floor)。avg 与 p95 都参与：p95 项压制「avg 稳但 tail 差」的模型。
                // 延迟越低分越高（排序时降序）。0.5 为 tail 权重，均衡平均与尾部抖动。
                double score = 1.0 / (stats.AverageLatencyMs + 0.5 * stats.P95LatencyMs + LatencyFloorMs);
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

using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 延迟感知路由策略：同 tier 段内按历史平均延迟升序重排（快模型优先），跨 tier 顺序不变。
/// 当 <see cref="RoutingOptions.EnableThompsonSampling"/> 启用时，段内改用 Thompson Sampling 重排。
/// </summary>
/// <remarks>
/// 策略链位置在 LongInput 之后、BudgetGuard/Failover 之前：
/// 先排除装不下/熔断的模型，再按延迟择优。
/// <para>
/// 两个开关各自独立 gate：
/// <list type="bullet">
/// <item><see cref="RoutingOptions.EnableLatencyAware"/>：段内按历史平均延迟升序。</item>
/// <item><see cref="RoutingOptions.EnableThompsonSampling"/>：段内按 Beta 分布采样重排（自适应探索）。</item>
/// </list>
/// 两者皆关时整段透传。两者都开时 Thompson 优先（ReorderSegment 内判断）。
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
    public LatencyAwarePolicy(
        ILatencyStatsProvider statsProvider,
        ThompsonStateStore tsStore,
        Func<double, double, double>? sampleBeta = null,
        ContextualBanditState? banditStore = null)
    {
        _statsProvider = statsProvider ?? throw new ArgumentNullException(nameof(statsProvider));
        _tsStore = tsStore ?? throw new ArgumentNullException(nameof(tsStore));
        _sampleBeta = sampleBeta ?? ThompsonSampler.SampleBeta;
        _banditStore = banditStore;
    }

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        bool latencyEnabled = context.Options.Routing.EnableLatencyAware;
        bool thompsonEnabled = context.Options.Routing.EnableThompsonSampling;
        bool banditEnabled = context.Options.Routing.EnableContextualBandit && _banditStore is not null;

        // Thompson Sampling 不再隐式依赖 EnableLatencyAware：两者各自 gate。
        // 上下文 bandit 与 Thompson 互斥（启用时段内用 LinUCB）。
        // 仅当三者都关闭时整体跳过（保持原 reason 文案对延迟感知的描述）。
        if (!latencyEnabled && !thompsonEnabled && !banditEnabled)
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

            var reordered = ReorderSegment(seg, minSamples, context, previous.ClassificationSignal, previous.ClassificationTargetTier);
            if (!SameOrder(seg, reordered))
                segmentsReordered++;
            result.AddRange(reordered);
        }

        if (segmentsReordered == 0)
        {
            return previous with { Reason = $"{previous.Reason}; latency-aware: no change" };
        }

        string extraTag = context.Options.Routing.EnableContextualBandit && _banditStore is not null
            ? " [Contextual Bandit]"
            : context.Options.Routing.EnableThompsonSampling ? " [Thompson Sampling]" : "";
        return previous with
        {
            Candidates = result,
            Reason = $"{previous.Reason}; latency-aware: reordered {segmentsReordered} tier segment(s){extraTag}"
        };
    }

    /// <summary>同 tier 段内：根据配置选择上下文 bandit / Thompson / 延迟感知重排。</summary>
    private List<ModelEndpointOptions> ReorderSegment(List<ModelEndpointOptions> segment, int minSamples, RouterContext context,
        string? classificationSignal, ModelTier? classificationTargetTier)
    {
        if (context.Options.Routing.EnableContextualBandit && _banditStore is not null)
        {
            return ReorderByContextualBandit(segment, classificationSignal, classificationTargetTier, context.Options.Routing.ContextualBanditAlpha);
        }

        if (context.Options.Routing.EnableThompsonSampling)
        {
            return ReorderByThompsonSampling(segment);
        }

        return ReorderByLatencyScore(segment, minSamples);
    }

    /// <summary>
    /// 上下文老虎机（LinUCB）重排：用分类信号 + tier 构造上下文特征，每模型 LinUCB 打分降序。
    /// 修非上下文 Thompson 「只优化延迟、系统性低估 Strong」的缺陷——LinUCB 用请求特征学习「模型↔任务」匹配。
    /// </summary>
    private List<ModelEndpointOptions> ReorderByContextualBandit(List<ModelEndpointOptions> segment,
        string? classificationSignal, ModelTier? classificationTargetTier, double alpha)
    {
        var feature = ContextualBanditFeatureBuilder.Build(classificationSignal, classificationTargetTier);

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

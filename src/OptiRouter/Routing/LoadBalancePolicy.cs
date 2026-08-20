using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 同 tier 负载均衡策略：候选链中同一 tier 段内按 MaxContextTokens 与卡尔曼滤波 P99 降权因子加权随机重排，
/// 分散同 tier 流量并抑制高尾延 Provider。跨 tier 顺序不变（保留能力择优）。
/// </summary>
public sealed class LoadBalancePolicy : IRouterPolicy
{
    private readonly KalmanLatencyTracker? _kalmanTracker;

    public LoadBalancePolicy(KalmanLatencyTracker? kalmanTracker = null)
    {
        _kalmanTracker = kalmanTracker;
    }

    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Constraint;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableLoadBalance)
        {
            return previous.Append("load-balance", "disabled");
        }

        // 不足 2 个候选无可均衡；同 tier 段需 ≥2 才有重排空间。
        if (previous.Candidates.Count < 2)
        {
            return previous.Append("load-balance", "<2 candidates");
        }

        // 按 tier 分段
        var segments = new List<List<ModelEndpointOptions>>();
        ModelTier? currentTier = null;
        foreach (var m in previous.Candidates)
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

        bool anyReordered = false;
        var result = new List<ModelEndpointOptions>(previous.Candidates.Count);
        bool useKalman = context.Options.Routing.EnableKalmanLoadBalance && _kalmanTracker != null;

        foreach (var seg in segments)
        {
            if (seg.Count > 1)
            {
                var shuffled = WeightedShuffle(seg, useKalman ? _kalmanTracker : null);
                if (!SameOrder(seg, shuffled))
                    anyReordered = true;
                result.AddRange(shuffled);
            }
            else
            {
                result.AddRange(seg);
            }
        }

        if (!anyReordered)
        {
            return previous.Append("load-balance", "no change after shuffle");
        }

        var withResult = previous with { Candidates = result };
        string reason = useKalman ? "redistributed with kalman-p99 weighting" : "redistributed within tier";
        return withResult.Append("load-balance", reason);
    }

    /// <summary>按 MaxContextTokens 与卡尔曼 P99 降权因子加权随机重排一个 tier 段。</summary>
    private static List<ModelEndpointOptions> WeightedShuffle(List<ModelEndpointOptions> segment, KalmanLatencyTracker? kalmanTracker)
    {
        var remaining = new List<ModelEndpointOptions>(segment);
        var output = new List<ModelEndpointOptions>(segment.Count);
        var rng = Random.Shared;

        while (remaining.Count > 0)
        {
            double total = 0;
            foreach (var m in remaining)
            {
                double kalmanPenalty = kalmanTracker?.GetEstimate(m.Name).PenaltyWeightFactor ?? 1.0;
                total += Math.Max(m.MaxContextTokens, 1) * Math.Max(m.Weight, 0) * kalmanPenalty;
            }

            double pick = rng.NextDouble() * total;
            double acc = 0;
            int chosenIdx = remaining.Count - 1;
            for (int i = 0; i < remaining.Count; i++)
            {
                double kalmanPenalty = kalmanTracker?.GetEstimate(remaining[i].Name).PenaltyWeightFactor ?? 1.0;
                acc += Math.Max(remaining[i].MaxContextTokens, 1) * Math.Max(remaining[i].Weight, 0) * kalmanPenalty;
                if (pick < acc)
                {
                    chosenIdx = i;
                    break;
                }
            }

            output.Add(remaining[chosenIdx]);
            remaining.RemoveAt(chosenIdx);
        }

        return output;
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

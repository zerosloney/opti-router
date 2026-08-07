using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 同 tier 负载均衡策略：候选链中同一 tier 段内按 MaxContextTokens 加权随机重排，
/// 分散同 tier 流量。跨 tier 顺序不变（保留能力择优）。
/// </summary>
/// <remarks>
/// 策略链末位（Failover 之后）：仅对熔断排除后的存活候选做均衡。
/// 权重 = MaxContextTokens：大上下文模型更可能被选中（保留质量倾向），但小模型仍有概率分摊流量。
/// intentional-simple: 加权随机 O(n) 一次遍历，同 tier 候选数通常少于 10，无需更复杂算法。
/// </remarks>
public sealed class LoadBalancePolicy : IRouterPolicy
{
    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableLoadBalance)
        {
            return previous with { Reason = $"{previous.Reason}; load-balance: disabled" };
        }

        // 不足 2 个候选无可均衡；同 tier 段需 ≥2 才有重排空间。
        if (previous.Candidates.Count < 2)
        {
            return previous with { Reason = $"{previous.Reason}; load-balance: <2 candidates" };
        }

        // 按 tier 分段（候选链按 tier 升序构造，但下游策略可能打乱，故按出现顺序分段）。
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

        // 仅重排 size>1 的段；size==1 段保持。
        bool anyReordered = false;
        var result = new List<ModelEndpointOptions>(previous.Candidates.Count);
        foreach (var seg in segments)
        {
            if (seg.Count > 1)
            {
                var shuffled = WeightedShuffle(seg);
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
            return previous with { Reason = $"{previous.Reason}; load-balance: no change after shuffle" };
        }

        return previous with
        {
            Candidates = result,
            Reason = $"{previous.Reason}; load-balance: redistributed within tier"
        };
    }

    /// <summary>按 MaxContextTokens 加权随机重排一个 tier 段。</summary>
    private static List<ModelEndpointOptions> WeightedShuffle(List<ModelEndpointOptions> segment)
    {
        var remaining = new List<ModelEndpointOptions>(segment);
        var output = new List<ModelEndpointOptions>(segment.Count);
        var rng = Random.Shared;

        while (remaining.Count > 0)
        {
            // 权重 = MaxContextTokens（>0 保证）。累加选一个，移除后继续。
            double total = 0;
            foreach (var m in remaining)
                total += Math.Max(m.MaxContextTokens, 1);

            double pick = rng.NextDouble() * total;
            double acc = 0;
            int chosenIdx = remaining.Count - 1; // 浮点兜底，默认选最后一个
            for (int i = 0; i < remaining.Count; i++)
            {
                acc += Math.Max(remaining[i].MaxContextTokens, 1);
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

using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 数据不出域/数据合规隔离路由策略：
/// 启用时，强制过滤候选模型链，仅保留标记为本地/私有部署（IsLocalOrPrivate == true，或带有 local/private/on-premise 标签）的端点，
/// 阻断敏感 Prompt 广播至外部公共云端 API，保障数据不出域。
/// </summary>
public sealed class DataSovereigntyPolicy : IRouterPolicy
{
    public PolicyGroup Group => PolicyGroup.Filter;

    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableDataSovereignty || previous.Candidates.Count == 0)
            return previous;

        var viableLocalCandidates = new List<ModelEndpointOptions>();
        int excludedCloudCandidates = 0;

        foreach (var candidate in previous.Candidates)
        {
            if (IsLocalOrPrivateCandidate(candidate))
            {
                viableLocalCandidates.Add(candidate);
            }
            else
            {
                excludedCloudCandidates++;
            }
        }

        // 如果配置了数据不出域但无任何可用本地模型，清空 Candidates 以安全阻断请求，不向云端外泄
        var updatedDecision = previous with { Candidates = viableLocalCandidates };
        return updatedDecision.Append(
            "data-sovereignty",
            $"retained_local={viableLocalCandidates.Count}, excluded_cloud={excludedCloudCandidates}");
    }

    private static bool IsLocalOrPrivateCandidate(ModelEndpointOptions candidate)
    {
        if (candidate.IsLocalOrPrivate)
            return true;

        if (candidate.Tags is null || candidate.Tags.Count == 0)
            return false;

        foreach (var tag in candidate.Tags)
        {
            if (tag.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("private", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("on-premise", StringComparison.OrdinalIgnoreCase) ||
                tag.Equals("onprem", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

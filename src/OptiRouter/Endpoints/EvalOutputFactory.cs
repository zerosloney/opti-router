using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 评测批的 <see cref="EvalRunOutput"/> 工厂：从原始响应回查模型端点并按真实 usage 计算成本。
/// 修复背景：/eval/run 此前走 OfflineEvalRunner 的裸委托适配器（仅填 Response），
/// SelectedModel 靠 body 提取兜底、成本恒为 0——横评/AB 对比的成本维度因此失真。
/// </summary>
public static class EvalOutputFactory
{
    /// <summary>
    /// 按响应 body 的 model 字段（上游真实模型 Id）回查配置的模型端点，用其定价与
    /// response.Usage 计算本次调用成本；回查不到端点或 usage 缺失时成本记 0、
    /// SelectedModel 留空（由评测器回落 body 提取）——宁可缺成本也不编造口径。
    /// </summary>
    public static EvalRunOutput FromRawResponse(RawChatResponse response, IEnumerable<ModelEndpointOptions> models)
    {
        ArgumentNullException.ThrowIfNull(response);

        var candidate = FindByUpstreamModelId(models, OfflineEvalRunner.ExtractModelName(response.Body));
        decimal cost = candidate is not null && response.Usage is not null
            ? CostCalculator.Compute(response.Usage, candidate)
            : 0m;
        return new EvalRunOutput(response, candidate?.Name, cost, RoutedCategory: null);
    }

    private static ModelEndpointOptions? FindByUpstreamModelId(IEnumerable<ModelEndpointOptions> models, string? upstreamModelId)
    {
        if (string.IsNullOrEmpty(upstreamModelId) || models is null)
            return null;

        foreach (var m in models)
        {
            if (string.Equals(m.Id, upstreamModelId, StringComparison.Ordinal)
                || string.Equals(m.Name, upstreamModelId, StringComparison.Ordinal))
            {
                return m;
            }
        }

        return null;
    }
}

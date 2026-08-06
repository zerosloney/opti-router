using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// 按 token 用量和模型价格计算成本（USD）。
/// </summary>
public static class CostCalculator
{
    /// <summary>
    /// 计算单次请求的成本。input 与 output 分别计价。
    /// </summary>
    /// <param name="usage">token 用量统计。</param>
    /// <param name="endpoint">端点价格配置。</param>
    /// <returns>成本（USD）。</returns>
    public static decimal Compute(ChatUsage usage, ModelEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(endpoint);

        var inputCost = usage.PromptTokens * endpoint.InputPricePerMillion / 1_000_000m;
        var outputCost = usage.CompletionTokens * endpoint.OutputPricePerMillion / 1_000_000m;
        return inputCost + outputCost;
    }
}

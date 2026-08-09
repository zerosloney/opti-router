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

        int promptTokens = Math.Max(0, usage.PromptTokens);
        int cachedTokens = Math.Clamp(usage.CachedInputTokens, 0, promptTokens);
        int cacheWriteTokens = Math.Clamp(usage.CacheWriteInputTokens, 0, promptTokens - cachedTokens);
        int uncachedTokens = Math.Max(0, promptTokens - cachedTokens - cacheWriteTokens);

        decimal cachedPrice = endpoint.CachedInputPricePerMillion ?? endpoint.InputPricePerMillion;
        decimal cacheWritePrice = endpoint.CacheWriteInputPricePerMillion ?? endpoint.InputPricePerMillion;
        var inputCost = (
            cachedTokens * cachedPrice
            + cacheWriteTokens * cacheWritePrice
            + uncachedTokens * endpoint.InputPricePerMillion) / 1_000_000m;
        var outputCost = Math.Max(0, usage.CompletionTokens) * endpoint.OutputPricePerMillion / 1_000_000m;
        return inputCost + outputCost;
    }
}

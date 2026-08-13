using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 两阶段匹配引擎：高置信 TF-IDF 直接返回；低置信时由第二阶段引擎作最终判定，
/// 第二阶段无结果或异常则回退 TF-IDF。两个引擎的分数不在未经校准时直接比较。
/// </summary>
public sealed class HybridSemanticVectorEngine : ISemanticVectorEngine
{
    private readonly ISemanticVectorEngine _sparseEngine;
    private readonly ISemanticVectorEngine _denseEngine;
    private readonly double _highConfidenceThreshold;

    /// <summary>
    /// 获取当前使用的稀疏向量引擎。
    /// </summary>
    public ISemanticVectorEngine SparseEngine => _sparseEngine;

    /// <summary>
    /// 获取当前使用的第二阶段引擎。
    /// </summary>
    public ISemanticVectorEngine DenseEngine => _denseEngine;

    /// <summary>
    /// 获取高置信短路阈值。
    /// </summary>
    public double HighConfidenceThreshold => _highConfidenceThreshold;

    /// <summary>
    /// 初始化混合语义向量路由引擎。
    /// </summary>
    /// <param name="sparseEngine">稀疏向量引擎（为空则默认使用 <see cref="TfIdfSemanticVectorEngine"/>）。</param>
    /// <param name="denseEngine">第二阶段引擎（为空则默认使用稳定特征哈希；可注入真正 embedding）。</param>
    /// <param name="highConfidenceThreshold">TF-IDF 高置信短路阈值（默认 0.45）。</param>
    public HybridSemanticVectorEngine(
        ISemanticVectorEngine? sparseEngine = null,
        ISemanticVectorEngine? denseEngine = null,
        double highConfidenceThreshold = 0.45)
    {
        _sparseEngine = sparseEngine ?? new TfIdfSemanticVectorEngine();
        _denseEngine = denseEngine ?? new DenseEmbeddingVectorEngine();
        _highConfidenceThreshold = highConfidenceThreshold;
    }

    /// <inheritdoc />
    public (SemanticRouteOptions? MatchedRoute, double MaxSimilarity) Match(
        string queryText,
        List<SemanticRouteOptions> routes)
    {
        if (string.IsNullOrWhiteSpace(queryText) || routes is null || routes.Count == 0)
        {
            return (null, 0.0);
        }

        // 1. 优先进行 TF-IDF 超高速稀疏匹配
        var sparseResult = _sparseEngine.Match(queryText, routes);

        // 2. 高置信短路 (Short-circuiting) —— 命中显式关键词，直接返回！
        if (sparseResult.MatchedRoute is not null && sparseResult.MaxSimilarity >= _highConfidenceThreshold)
        {
            return sparseResult;
        }

        // 两个引擎的余弦分数来自不同特征空间，不具备可比性。
        // 低置信区采用明确的阶段仲裁：第二阶段有结果即采用，否则回退第一阶段。
        try
        {
            var denseResult = _denseEngine.Match(queryText, routes);
            if (denseResult.MatchedRoute is not null)
            {
                return denseResult;
            }
        }
        catch
        {
            // 异常优雅降级：Dense 计算异常时降级返回 TF-IDF 结果
        }

        return sparseResult;
    }
}

using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 语义向量匹配引擎抽象。
/// 支持基于 CJK 增强 TF-IDF 向量空间模型与离线 Dense Embedding (如 ONNX) 向量引擎无缝切换。
/// </summary>
public interface ISemanticVectorEngine
{
    /// <summary>
    /// 对用户请求 Query 计算与候选规则列表的向量匹配，返回最高匹配度的规则与相似度分数。
    /// </summary>
    /// <param name="queryText">用户待识别意图文本。</param>
    /// <param name="routes">语义路由条目配置列表。</param>
    /// <returns>最高匹配度的路由规则与余弦相似度分数。</returns>
    (SemanticRouteOptions? MatchedRoute, double MaxSimilarity) Match(
        string queryText,
        List<SemanticRouteOptions> routes);

    /// <summary>
    /// 对输入文本计算并生成 L2 归一化的 Dense / 特征向量表示。
    /// </summary>
    /// <param name="text">待向量化的输入文本。</param>
    /// <returns>L2 归一化的单精度浮点特征向量。</returns>
    float[] Embed(string text);
}

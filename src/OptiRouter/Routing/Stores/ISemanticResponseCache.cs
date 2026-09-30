using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 深度语义响应缓存接口。
/// 允许按意图余弦相似度跳过上游 LLM 调用，实现 0 成本与亚毫秒级响应。
/// </summary>
public interface ISemanticResponseCache
{
    /// <summary>
    /// 尝试在语义向量空间中检索最高相似度的缓存响应。
    /// </summary>
    /// <param name="prompt">待匹配的用户 Prompt 文本。</param>
    /// <param name="similarityThreshold">命中的最低 Cosine 余弦相似度阈值（如 0.95）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="partitionKey">安全或上下文分区键；仅匹配同一分区。省略时使用默认分区。</param>
    /// <returns>包含是否命中、响应结果、最高相似度及命中 Prompt 的元组。</returns>
    Task<(bool Hit, RawChatResponse? Response, double Similarity, string? MatchedPrompt)> TryGetAsync(
        string prompt,
        float similarityThreshold = 0.95f,
        CancellationToken cancellationToken = default,
        string? partitionKey = null);

    /// <summary>
    /// 存入 Prompt 与对应的模型 RawChatResponse。
    /// </summary>
    /// <param name="prompt">原始 Prompt 文本。</param>
    /// <param name="response">上游返回的无损响应对象。</param>
    /// <param name="ttl">生存时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="partitionKey">安全或上下文分区键；仅与同一分区的查询匹配。省略时使用默认分区。</param>
    Task StoreAsync(
        string prompt,
        RawChatResponse response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default,
        string? partitionKey = null);

    /// <summary>
    /// 清空所有语义响应缓存。
    /// </summary>
    void Clear();
}

namespace OptiRouter.Routing;

/// <summary>
/// SLA 路由模式定义：驱动多维 SLA（首 Token 敏捷度 vs Token 吞吐率 vs 综合延迟）感知重排。
/// </summary>
public enum SlaMode
{
    /// <summary>
    /// 默认综合平衡模式（平衡平均延迟与 P95 尾部延迟）。
    /// </summary>
    Balanced = 0,

    /// <summary>
    /// 首 Token 敏捷度优先（TTFT，Fastest First Token）：优先选择首个 Chunk 到达最快的模型。
    /// </summary>
    Ttft = 1,

    /// <summary>
    /// Token 吞吐率优先（TPS，Highest Generation Speed）：优先选择每秒生成 Token 最多的模型。
    /// </summary>
    Tps = 2
}

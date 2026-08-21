namespace OptiRouter.Mesh;

/// <summary>
/// KV Cache 前缀索引跨节点同步事件。
/// </summary>
public sealed record KvCachePrefixSyncEvent(
    string SenderNodeId,
    IReadOnlyList<string> Tokens,
    string ModelName,
    DateTimeOffset Timestamp);

/// <summary>
/// 卡尔曼延迟估计跨节点同步事件。
/// </summary>
public sealed record KalmanLatencySyncEvent(
    string SenderNodeId,
    string ModelName,
    double ObservedLatencyMs,
    DateTimeOffset Timestamp);

/// <summary>
/// 成本账本跨节点同步事件。
/// </summary>
public sealed record CostLedgerSyncEvent(
    string SenderNodeId,
    decimal DeltaCost,
    string? SessionId,
    DateTimeOffset Timestamp);

/// <summary>
/// 主动弹性时序故障/波动跨节点同步事件。
/// 时序桶由接收侧从 <see cref="Timestamp"/> 的 Minute（minute-of-hour，[0,60)）派生——
/// PredictiveResilienceEngine 的分钟桶仅接受该区间。曾存在一个绝对分钟戳的 MinuteBucket
/// 字段（unixSeconds/60，千万级），全仓库无消费者且语义与接收侧不兼容，已删除。
/// </summary>
public sealed record PredictiveResilienceSyncEvent(
    string SenderNodeId,
    string ModelName,
    bool IsFailure,
    DateTimeOffset Timestamp);

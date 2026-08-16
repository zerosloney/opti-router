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
/// </summary>
public sealed record PredictiveResilienceSyncEvent(
    string SenderNodeId,
    string ModelName,
    int MinuteBucket,
    bool IsFailure,
    DateTimeOffset Timestamp);

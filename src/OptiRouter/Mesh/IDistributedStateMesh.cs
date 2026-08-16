namespace OptiRouter.Mesh;

/// <summary>
/// 分布式状态网格运行统计。
/// </summary>
public sealed record MeshStats(
    string NodeId,
    long PublishedEventsCount,
    long ReceivedEventsCount,
    int ActiveSubscriptionsCount);

/// <summary>
/// 分布式跨网关集群状态同步网格抽象 (Distributed State Mesh Interface)。
/// 支持在多网关/多节点集群环境下进行低延迟、高吞吐的状态同步与事件广播。
/// </summary>
public interface IDistributedStateMesh
{
    /// <summary>
    /// 当前网关节点唯一标识。
    /// </summary>
    string NodeId { get; }

    /// <summary>
    /// 发布指定频道的广播事件。
    /// </summary>
    Task PublishAsync<TEvent>(string channel, TEvent payload, CancellationToken ct = default);

    /// <summary>
    /// 订阅指定频道的广播事件。返回 IDisposable 用于取消订阅。
    /// </summary>
    IDisposable Subscribe<TEvent>(string channel, Action<TEvent> onReceived);

    /// <summary>
    /// 获取网格运行统计信息。
    /// </summary>
    MeshStats GetStats();
}

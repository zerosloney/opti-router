using Microsoft.Extensions.Logging;
using OptiRouter.Routing;

namespace OptiRouter.Mesh;

/// <summary>
/// 分布式集群状态同步中枢 (Distributed Mesh State Synchronizer)。
/// 负责本地关键状态变更的跨网关广播，并监听远端节点状态事件以实时合并到本地：
/// 1. KV-Cache 前缀树状态跨网关共享 (KvCachePrefixTrie)；
/// 2. 卡尔曼滤波延迟与 P99 抖动估计跨网关同步 (KalmanLatencyTracker)；
/// 3. 分布式多节点预算与成本累加 (CostLedger)；
/// 4. 时序故障与弹性断路器事件同步 (PredictiveResilienceEngine)。
/// </summary>
public sealed class DistributedMeshSynchronizer : IDisposable
{
    private readonly IDistributedStateMesh _mesh;
    private readonly KvCachePrefixTrie? _kvTrie;
    private readonly KalmanLatencyTracker? _kalmanTracker;
    private readonly CostLedger? _costLedger;
    private readonly PredictiveResilienceEngine? _resilienceEngine;
    private readonly ILogger<DistributedMeshSynchronizer>? _logger;
    private readonly List<IDisposable> _subscriptions = new();

    public const string ChannelKvCache = "optirouter:mesh:kvcache";
    public const string ChannelKalman = "optirouter:mesh:kalman";
    public const string ChannelCost = "optirouter:mesh:cost";
    public const string ChannelResilience = "optirouter:mesh:resilience";

    public DistributedMeshSynchronizer(
        IDistributedStateMesh mesh,
        KvCachePrefixTrie? kvTrie = null,
        KalmanLatencyTracker? kalmanTracker = null,
        CostLedger? costLedger = null,
        PredictiveResilienceEngine? resilienceEngine = null,
        ILogger<DistributedMeshSynchronizer>? logger = null)
    {
        _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        _kvTrie = kvTrie;
        _kalmanTracker = kalmanTracker;
        _costLedger = costLedger;
        _resilienceEngine = resilienceEngine;
        _logger = logger;

        InitializeSubscriptions();
    }

    private void InitializeSubscriptions()
    {
        // 1. 订阅远端 KV-Cache 前缀同步
        _subscriptions.Add(_mesh.Subscribe<KvCachePrefixSyncEvent>(ChannelKvCache, evt =>
        {
            if (evt == null || evt.SenderNodeId == _mesh.NodeId || _kvTrie == null) return;
            try
            {
                _kvTrie.RecordCachePrefix(evt.Tokens, evt.ModelName, evt.Timestamp);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error syncing remote KV cache prefix from node {NodeId}", evt.SenderNodeId);
            }
        }));

        // 2. 订阅远端卡尔曼延迟观测
        _subscriptions.Add(_mesh.Subscribe<KalmanLatencySyncEvent>(ChannelKalman, evt =>
        {
            if (evt == null || evt.SenderNodeId == _mesh.NodeId || _kalmanTracker == null) return;
            try
            {
                _kalmanTracker.RecordObservation(evt.ModelName, evt.ObservedLatencyMs);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error syncing remote Kalman latency from node {NodeId}", evt.SenderNodeId);
            }
        }));

        // 3. 订阅远端成本消耗事件
        _subscriptions.Add(_mesh.Subscribe<CostLedgerSyncEvent>(ChannelCost, evt =>
        {
            if (evt == null || evt.SenderNodeId == _mesh.NodeId || _costLedger == null) return;
            try
            {
                _costLedger.Record(evt.DeltaCost, evt.SessionId);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error syncing remote cost from node {NodeId}", evt.SenderNodeId);
            }
        }));

        // 4. 订阅远端主动弹性故障与波动
        _subscriptions.Add(_mesh.Subscribe<PredictiveResilienceSyncEvent>(ChannelResilience, evt =>
        {
            if (evt == null || evt.SenderNodeId == _mesh.NodeId || _resilienceEngine == null) return;
            try
            {
                _resilienceEngine.RecordObservation(evt.ModelName, !evt.IsFailure, 0, evt.Timestamp);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Error syncing remote resilience event from node {NodeId}", evt.SenderNodeId);
            }
        }));
    }

    /// <summary>
    /// 广播本地 KV-Cache 前缀索引变更。
    /// </summary>
    public Task BroadcastKvCachePrefixAsync(IReadOnlyList<string> tokens, string modelName, CancellationToken ct = default)
    {
        if (tokens == null || tokens.Count < 3 || string.IsNullOrWhiteSpace(modelName))
            return Task.CompletedTask;

        var evt = new KvCachePrefixSyncEvent(_mesh.NodeId, tokens, modelName, DateTimeOffset.UtcNow);
        return _mesh.PublishAsync(ChannelKvCache, evt, ct);
    }

    /// <summary>
    /// 广播本地卡尔曼延迟观测数据。
    /// </summary>
    public Task BroadcastKalmanLatencyAsync(string modelName, double observedLatencyMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName) || observedLatencyMs <= 0)
            return Task.CompletedTask;

        var evt = new KalmanLatencySyncEvent(_mesh.NodeId, modelName, observedLatencyMs, DateTimeOffset.UtcNow);
        return _mesh.PublishAsync(ChannelKalman, evt, ct);
    }

    /// <summary>
    /// 广播本地单次请求发生的成本消耗。
    /// </summary>
    public Task BroadcastCostAsync(decimal cost, string? sessionId = null, CancellationToken ct = default)
    {
        if (cost <= 0)
            return Task.CompletedTask;

        var evt = new CostLedgerSyncEvent(_mesh.NodeId, cost, sessionId, DateTimeOffset.UtcNow);
        return _mesh.PublishAsync(ChannelCost, evt, ct);
    }

    /// <summary>
    /// 广播本地主动弹性故障事件。
    /// </summary>
    public Task BroadcastResilienceOutcomeAsync(string modelName, bool isFailure, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return Task.CompletedTask;

        int minuteBucket = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60);
        var evt = new PredictiveResilienceSyncEvent(_mesh.NodeId, modelName, minuteBucket, isFailure, DateTimeOffset.UtcNow);
        return _mesh.PublishAsync(ChannelResilience, evt, ct);
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }
}

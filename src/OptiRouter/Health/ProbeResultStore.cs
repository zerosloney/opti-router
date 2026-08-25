using System.Collections.Concurrent;

namespace OptiRouter.Health;

/// <summary>最近一次探活结果（手动探活与后台探活统一记录）。</summary>
public sealed record ProbeStatus(
    bool Success,
    long LatencyMs,
    DateTime TimestampUtc,
    string? Message,
    string? Error);

/// <summary>
/// 探活结果留痕（进程内，按模型名保留最近一次）。
/// 手动探活（ModelsConfigHandler 测试端点）与后台探活（ModelHealthProbeService）都写入；
/// 模型配置页加载时经 GET /api/models/probe-results 预填"连通状态"列，页面刷新不丢。
/// 重启清空——探活是瞬态健康信息，重启后状态未知，待下一轮探活填充。
/// </summary>
public sealed class ProbeResultStore
{
    private readonly ConcurrentDictionary<string, ProbeStatus> _latest = new(StringComparer.Ordinal);

    public void Record(string modelName, ProbeStatus status)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(status);
        _latest[modelName] = status;
    }

    /// <summary>全量快照（复制后返回，避免枚举期间被写入方修改）。</summary>
    public IReadOnlyDictionary<string, ProbeStatus> GetAll() =>
        new Dictionary<string, ProbeStatus>(_latest, StringComparer.Ordinal);
}

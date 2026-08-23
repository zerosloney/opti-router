namespace OptiRouter.Health;

/// <summary>
/// 存储降级事件转告警历史：降级只写日志时值班者无感知（P2 修复）——
/// 降级/恢复的状态迁移点同步记入 <see cref="AlertHistory"/>，Dashboard 告警历史与 Webhook 推送可见。
/// </summary>
public static class DegradationAlerts
{
    public const string Category = "degradation";

    /// <summary>降级事件（warning）：调用方保证只在状态从正常→降级的迁移点调用一次。</summary>
    public static AlertEvent Degraded(string alertId, string message) =>
        new(DateTimeOffset.UtcNow, "alert", alertId, "warning", Category, message);

    /// <summary>恢复事件（info）：只在降级→正常的迁移点调用。</summary>
    public static AlertEvent Recovered(string alertId, string message) =>
        new(DateTimeOffset.UtcNow, "resolved", alertId, "info", Category, message);
}

namespace OptiRouter.Health;

/// <summary>一条告警历史事件（出现或恢复）。</summary>
public sealed record AlertEvent(
    DateTimeOffset Timestamp,
    string EventType,   // "alert" | "resolved"
    string AlertId,
    string Level,
    string Category,
    string Message);

/// <summary>
/// 告警历史环形缓冲（进程内，最多 <see cref="MaxEvents"/> 条）。
/// 由 <see cref="AlertWebhookNotifier"/> 的周期检查写入：告警出现记 alert、恢复记 resolved。
/// 无论是否配置 Webhook 都记录，供 Dashboard 历史查询。
/// </summary>
public sealed class AlertHistory
{
    private const int MaxEvents = 200;
    private readonly object _lock = new();
    private readonly Queue<AlertEvent> _events = new();

    public void Record(AlertEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_lock)
        {
            _events.Enqueue(evt);
            while (_events.Count > MaxEvents)
            {
                _events.Dequeue();
            }
        }
    }

    /// <summary>最近 count 条事件（按时间倒序）。</summary>
    public IReadOnlyList<AlertEvent> GetRecent(int count)
    {
        if (count <= 0) count = 50;
        lock (_lock)
        {
            return _events.Reverse().Take(count).ToList();
        }
    }
}

using System.Diagnostics;

namespace OptiRouter.Routing;

/// <summary>
/// 成本账本，线程安全。支持日预算（全局）与会话预算（按 X-Session-Id 隔离）。
/// 实际状态委托 <see cref="ICostLedgerStore"/> 持久化；内存实现用于测试，SQLite 实现用于生产。
/// 会话账户按 sessionEvictionHours 懒淘汰，防止无界增长。
/// </summary>
public class CostLedger
{
    private readonly ICostLedgerStore _store;
    private readonly object _resetLock = new();
    private DateTime _lastDailyDate = DateTime.UtcNow.Date;
    private readonly TimeSpan? _sessionEvictionAge;
    private readonly TimeSpan _evictionCheckInterval;
    private DateTime _lastEvictionCheck = DateTime.UtcNow;
    private readonly bool _evictionEnabled;

    /// <summary>
    /// 用指定存储初始化。不传则用内存存储（保持 <c>new CostLedger()</c> 现有测试零改动）。
    /// </summary>
    /// <param name="store">持久化存储；为 null 时回退到 <see cref="InMemoryCostLedgerStore"/>。</param>
    /// <param name="sessionEvictionHours">会话淘汰年龄（小时），超过此时长无活动的会话被清理。
    /// 为 null 时禁用淘汰（测试默认行为）。生产建议 24。</param>
    /// <param name="evictionCheckIntervalMinutes">淘汰检查间隔分钟，默认 60。</param>
    public CostLedger(
        ICostLedgerStore? store = null,
        int? sessionEvictionHours = null,
        int evictionCheckIntervalMinutes = 60)
    {
        _store = store ?? new InMemoryCostLedgerStore();
        _sessionEvictionAge = sessionEvictionHours is { } h && h > 0
            ? TimeSpan.FromHours(h)
            : null;
        _evictionCheckInterval = TimeSpan.FromMinutes(
            evictionCheckIntervalMinutes > 0 ? evictionCheckIntervalMinutes : 60);
        _evictionEnabled = _sessionEvictionAge is not null;

        if (_evictionEnabled)
        {
            // 启动时清理上次运行残留的过期会话。
            TryEvictStaleSessions(force: true);
        }
    }

    /// <summary>
    /// 记录一笔成本。sessionId 非空时同时累加到该会话账户。
    /// 使用 RecordAtomic 单事务写入日/总/会话三个账户，避免部分失败导致账户漂移。
    /// </summary>
    /// <param name="cost">成本（USD）。</param>
    /// <param name="sessionId">可选会话 ID。null 或空时仅记日预算。</param>
    public void Record(decimal cost, string? sessionId = null)
    {
        // 午夜边界：单次捕获 now，日切判定与记账同一时刻——两次读 UtcNow 之间跨日会把
        // 成本记到新日期而旧日累计未被归档清零。
        DateTime now = DateTime.UtcNow;
        ResetDailyIfNewDay(now);
        _store.RecordAtomic(
            now, cost, cost,
            sessionId, string.IsNullOrEmpty(sessionId) ? null : cost);

        if (!string.IsNullOrEmpty(sessionId))
        {
            TryEvictStaleSessions();
        }
    }

    /// <summary>
    /// 获取日花费和全局累计花费（自首次启动以来所有请求，不随日 reset 清零）。
    /// </summary>
    /// <returns><c>Daily</c> 为当日 UTC 花费；<c>Total</c> 为进程首次启动以来所有请求的累计花费。</returns>
    public virtual (decimal Daily, decimal Total) GetSpend()
    {
        ResetDailyIfNewDay();
        return (_store.GetDaily(DateTime.UtcNow), _store.GetTotal());
    }

    /// <summary>
    /// 获取日花费。
    /// </summary>
    public decimal GetDailySpend()
    {
        ResetDailyIfNewDay();
        return _store.GetDaily(DateTime.UtcNow);
    }

    /// <summary>
    /// 将当前日花费快照到历史归档（供趋势分析）。
    /// </summary>
    public void SnapshotDaily()
    {
        _store.SnapshotDaily(DateTime.UtcNow);
    }

    /// <summary>
    /// 获取指定会话的累计花费。会话不存在时返回 0。
    /// </summary>
    public decimal GetSessionSpend(string sessionId)
    {
        return _store.GetSession(sessionId);
    }

    // ---- in-flight 预算预留（TOCTOU 防护） ----
    // 预算守卫在请求前检查、计费在流结束后落账，中间窗口内并发请求可集体越过预算线。
    // 请求发起前按预估成本 Reserve，结束（无论成败）Release，守卫读"已入账 + 预留"有效值。
    // 预留是本地执行口径：不入库、不广播 mesh——各节点各自预留，多节点全局预算精度不变。

    private readonly object _reserveLock = new();
    private decimal _reservedDaily;
    private readonly Dictionary<string, decimal> _reservedSessions = new();

    /// <summary>
    /// 预扣一笔 in-flight 成本预留（日 + 会话两个维度，与 <see cref="Record"/> 对称）。
    /// 必须与 <see cref="Release"/> 严格配对（调用方 try/finally 或 using 保证）。
    /// </summary>
    public void Reserve(decimal amount, string? sessionId = null)
    {
        if (amount <= 0m) return;
        lock (_reserveLock)
        {
            _reservedDaily += amount;
            if (!string.IsNullOrEmpty(sessionId))
                _reservedSessions[sessionId] = _reservedSessions.GetValueOrDefault(sessionId) + amount;
        }
    }

    /// <summary>
    /// 释放预留。clamp 到 0 防配对失误（如 Arm 后重复释放）导致负数。
    /// </summary>
    public void Release(decimal amount, string? sessionId = null)
    {
        if (amount <= 0m) return;
        lock (_reserveLock)
        {
            _reservedDaily = Math.Max(0m, _reservedDaily - amount);
            if (!string.IsNullOrEmpty(sessionId))
            {
                decimal remaining = Math.Max(0m, _reservedSessions.GetValueOrDefault(sessionId) - amount);
                if (remaining == 0m) _reservedSessions.Remove(sessionId);
                else _reservedSessions[sessionId] = remaining;
            }
        }
    }

    /// <summary>
    /// 含 in-flight 预留的日花费（已入账 + 预留）。预算守卫的执行口径；
    /// 展示/告警口径继续用 <see cref="GetDailySpend"/>（只看已入账）。
    /// </summary>
    public decimal GetEffectiveDailySpend()
    {
        lock (_reserveLock)
        {
            return GetDailySpend() + _reservedDaily;
        }
    }

    /// <summary>
    /// 含 in-flight 预留的会话花费（已入账 + 预留）。
    /// </summary>
    public decimal GetEffectiveSessionSpend(string sessionId)
    {
        lock (_reserveLock)
        {
            return GetSessionSpend(sessionId) + _reservedSessions.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>
    /// 重置全局累计花费（不重置日花费和按会话账户）。
    /// </summary>
    public void ResetSession()
    {
        _store.ResetTotal();
    }

    /// <summary>
    /// 重置指定会话账户。
    /// </summary>
    public void ResetSession(string sessionId)
    {
        _store.ResetSession(sessionId);
    }

    /// <summary>
    /// 重置全部记录（日 + 所有会话）。
    /// </summary>
    public void ResetAll()
    {
        lock (_resetLock)
        {
            _store.ClearAll();
            _lastDailyDate = DateTime.UtcNow.Date;
        }
    }

    private void ResetDailyIfNewDay(DateTime? now = null)
    {
        DateTime today = (now ?? DateTime.UtcNow).Date;
        // Fast path: same day, no lock needed.
        if (today == _lastDailyDate) return;

        lock (_resetLock)
        {
            if (today == _lastDailyDate) return;
            // Archive current daily spend before clearing.
            _store.SnapshotDaily(_lastDailyDate);
            _store.ResetDaily();
            _lastDailyDate = today;
        }
    }

    private void TryEvictStaleSessions(bool force = false)
    {
        if (!_evictionEnabled) return;

        DateTime now = DateTime.UtcNow;
        if (!force && now - _lastEvictionCheck < _evictionCheckInterval)
            return;

        // intentional-simple: 每次检查的时间窗口内只淘汰一次，避免多线程重复。
        // 淘汰发生在 Record 调用路径上，无需独立定时器。
        lock (_resetLock)
        {
            if (!force && now - _lastEvictionCheck < _evictionCheckInterval)
                return;
            _lastEvictionCheck = now;
        }

        Debug.Assert(_sessionEvictionAge is not null);
        DateTime cutoff = now - _sessionEvictionAge!.Value;
        _store.EvictSessionsBefore(cutoff);
    }
}

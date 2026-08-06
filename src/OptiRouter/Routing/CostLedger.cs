using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// 内存成本账本，线程安全。支持日预算（全局）与会话预算（按 X-Session-Id 隔离）。
/// </summary>
public sealed class CostLedger
{
    private readonly object _lock = new();
    private decimal _dailySpend;
    private decimal _sessionSpend;
    private DateTime _dailyResetDate = DateTime.UtcNow.Date;
    // 会话维度花费隔离：sessionId -> spend。
    private readonly ConcurrentDictionary<string, decimal> _sessionSpends = new();

    /// <summary>
    /// 记录一笔成本。sessionId 非空时同时累加到该会话账户。
    /// </summary>
    /// <param name="cost">成本（USD）。</param>
    /// <param name="sessionId">可选会话 ID。null 或空时仅记日预算。</param>
    public void Record(decimal cost, string? sessionId = null)
    {
        lock (_lock)
        {
            ResetDailyIfNewDay();
            _dailySpend += cost;
            _sessionSpend += cost;
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            _sessionSpends.AddOrUpdate(sessionId, cost, (_, acc) => acc + cost);
        }
    }

    /// <summary>
    /// 获取日花费和全局会话花费（所有请求聚合）。
    /// </summary>
    public (decimal Daily, decimal Session) GetSpend()
    {
        lock (_lock)
        {
            ResetDailyIfNewDay();
            return (_dailySpend, _sessionSpend);
        }
    }

    /// <summary>
    /// 获取日花费。
    /// </summary>
    public decimal GetDailySpend()
    {
        lock (_lock)
        {
            ResetDailyIfNewDay();
            return _dailySpend;
        }
    }

    /// <summary>
    /// 获取指定会话的累计花费。会话不存在时返回 0。
    /// </summary>
    public decimal GetSessionSpend(string sessionId)
    {
        return _sessionSpends.TryGetValue(sessionId, out decimal spend) ? spend : 0m;
    }

    /// <summary>
    /// 重置全局会话花费（不重置日花费和按会话账户）。
    /// </summary>
    public void ResetSession()
    {
        lock (_lock)
        {
            _sessionSpend = 0;
        }
    }

    /// <summary>
    /// 重置指定会话账户。
    /// </summary>
    public void ResetSession(string sessionId)
    {
        _sessionSpends.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// 重置日花费和全局会话花费（不清理按会话账户，需单独调用 <see cref="ResetSession(string)"/>）。
    /// </summary>
    public void ResetAll()
    {
        lock (_lock)
        {
            _dailySpend = 0;
            _sessionSpend = 0;
            _dailyResetDate = DateTime.UtcNow.Date;
        }
    }

    private void ResetDailyIfNewDay()
    {
        if (DateTime.UtcNow.Date > _dailyResetDate)
        {
            _dailySpend = 0;
            _dailyResetDate = DateTime.UtcNow.Date;
        }
    }
}

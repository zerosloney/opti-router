namespace OptiRouter.Routing;

/// <summary>
/// 内存成本账本，线程安全。
/// </summary>
public sealed class CostLedger
{
    private readonly object _lock = new();
    private decimal _dailySpend;
    private decimal _sessionSpend;
    private DateTime _dailyResetDate = DateTime.UtcNow.Date;

    /// <summary>
    /// 记录一笔成本。
    /// </summary>
    public void Record(decimal cost)
    {
        lock (_lock)
        {
            ResetDailyIfNewDay();
            _dailySpend += cost;
            _sessionSpend += cost;
        }
    }

    /// <summary>
    /// 获取日花费和会话花费。
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
    /// 重置会话花费（不重置日花费）。
    /// </summary>
    public void ResetSession()
    {
        lock (_lock)
        {
            _sessionSpend = 0;
        }
    }

    /// <summary>
    /// 重置日花费和会话花费。
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

using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Routing;

/// <summary>
/// 单条告警记录。
/// </summary>
public sealed record AlertRecord(
    string Id,
    string Level,       // "warning" | "critical"
    string Category,    // "budget" | "circuit" | "failure-rate"
    string Message,
    DateTime Timestamp);

/// <summary>
/// 告警引擎：检查预算、断路器、失败率等条件，返回当前活跃告警列表。
/// </summary>
public sealed class AlertEngine
{
    private readonly CostLedger _ledger;
    private readonly ModelHealthTracker _healthTracker;
    private readonly IRequestAuditStore _auditStore;
    private readonly IOptionsMonitor<RouterOptions> _routerOptions;

    /// <summary>
    /// 构造告警引擎。
    /// </summary>
    public AlertEngine(
        CostLedger ledger,
        ModelHealthTracker healthTracker,
        IRequestAuditStore auditStore,
        IOptionsMonitor<RouterOptions> routerOptions)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(healthTracker);
        ArgumentNullException.ThrowIfNull(auditStore);
        ArgumentNullException.ThrowIfNull(routerOptions);

        _ledger = ledger;
        _healthTracker = healthTracker;
        _auditStore = auditStore;
        _routerOptions = routerOptions;
    }

    /// <summary>
    /// 检查所有告警条件，返回当前活跃告警列表。
    /// </summary>
    public IReadOnlyList<AlertRecord> Check()
    {
        var alerts = new List<AlertRecord>();
        var now = DateTime.UtcNow;

        // 1. Budget depletion warning (>= 80% of daily limit).
        CheckBudget(alerts, now);

        // 2. Circuit breaker open alerts.
        CheckCircuitBreakers(alerts);

        // 3. High failure rate (last 5 minutes).
        CheckFailureRate(alerts, now);

        return alerts;
    }

    private void CheckBudget(List<AlertRecord> alerts, DateTime now)
    {
        decimal dailyBudget = _routerOptions.CurrentValue.Budget.DailyBudgetUsd;
        if (dailyBudget <= 0) return;

        var (dailySpend, _) = _ledger.GetSpend();
        double ratio = (double)(dailySpend / dailyBudget);

        if (ratio >= 1.0)
        {
            alerts.Add(new AlertRecord(
                Id: "budget-exhausted",
                Level: "critical",
                Category: "budget",
                Message: $"Daily budget exhausted: ${dailySpend:F4} / ${dailyBudget:F4} (100%)",
                Timestamp: now));
        }
        else if (ratio >= 0.8)
        {
            alerts.Add(new AlertRecord(
                Id: "budget-warning",
                Level: "warning",
                Category: "budget",
                Message: $"Daily budget near limit: ${dailySpend:F4} / ${dailyBudget:F4} ({ratio:P0})",
                Timestamp: now));
        }
    }

    private void CheckCircuitBreakers(List<AlertRecord> alerts)
    {
        var snapshot = _healthTracker.GetCircuitsSnapshot();
        foreach (var (model, (state, failures, _)) in snapshot)
        {
            if (state == CircuitState.Open)
            {
                alerts.Add(new AlertRecord(
                    Id: $"circuit-open-{model}",
                    Level: "critical",
                    Category: "circuit",
                    Message: $"Model '{model}' circuit breaker OPEN (failures={failures})",
                    Timestamp: DateTime.UtcNow));
            }
            else if (state == CircuitState.HalfOpen && failures > 0)
            {
                alerts.Add(new AlertRecord(
                    Id: $"circuit-halfopen-{model}",
                    Level: "warning",
                    Category: "circuit",
                    Message: $"Model '{model}' circuit breaker HALF-OPEN (probing, failures={failures})",
                    Timestamp: DateTime.UtcNow));
            }
        }
    }

    private void CheckFailureRate(List<AlertRecord> alerts, DateTime now)
    {
        // Only check if failover is enabled.
        if (!_routerOptions.CurrentValue.Routing.EnableFailover) return;

        DateTime from = now.AddMinutes(-5);
        // 分子(failures)与分母(totalCount)必须同源：limit 截断 items 但不截 totalCount，
        // 大流量下分子被压低、分母不缩，失败率被系统性低估，故障越大越不报警。
        // intentional-simple: 拉全量进内存计数。5 分钟窗口 + Dashboard 1s 缓存下内存压力可忽略。
        // 若窗口内请求量增长到数万级，升级为 IRequestAuditStore.GetFailureCount/GetTotalCount 专用聚合接口。
        var (items, totalCount) = _auditStore.GetByTimeRange(from, now, int.MaxValue, 0);

        if (totalCount < 10) return; // Need enough samples.

        int failures = items.Count(r => !r.Success);
        if (failures == 0) return;

        double failureRate = (double)failures / totalCount;
        if (failureRate > 0.5)
        {
            alerts.Add(new AlertRecord(
                Id: "high-failure-rate",
                Level: "critical",
                Category: "failure-rate",
                Message: $"High failure rate: {failures}/{totalCount} requests failed in last 5 min ({failureRate:P0})",
                Timestamp: now));
        }
    }
}

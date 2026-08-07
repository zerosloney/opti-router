using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 告警引擎测试：验证预算告警、断路器告警、失败率告警的触发与恢复。
/// </summary>
public class AlertEngineTests
{
    [Fact]
    public void Check_BudgetExhausted_ReturnsCriticalAlert()
    {
        var ledger = new FakeCostLedger(daily: 100m, total: 100m);
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = false }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.Single(alerts);
        Assert.Equal("budget-exhausted", alerts[0].Id);
        Assert.Equal("critical", alerts[0].Level);
        Assert.Equal("budget", alerts[0].Category);
    }

    [Fact]
    public void Check_BudgetWarning_80Percent_ReturnsWarningAlert()
    {
        var ledger = new FakeCostLedger(daily: 80m, total: 80m);
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = false }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.Single(alerts);
        Assert.Equal("budget-warning", alerts[0].Id);
        Assert.Equal("warning", alerts[0].Level);
    }

    [Fact]
    public void Check_BudgetUnder80Percent_NoBudgetAlert()
    {
        var ledger = new FakeCostLedger(daily: 50m, total: 50m);
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = false }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.DoesNotContain(alerts, a => a.Category == "budget");
    }

    [Fact]
    public void Check_CircuitBreakerOpen_ReturnsCriticalAlert()
    {
        var ledger = new FakeCostLedger();
        var tracker = new ModelHealthTracker();
        tracker.RecordFailure("gpt-4o", threshold: 3, cooldownSeconds: 60);
        tracker.RecordFailure("gpt-4o", threshold: 3, cooldownSeconds: 60);
        tracker.RecordFailure("gpt-4o", threshold: 3, cooldownSeconds: 60); // trips to Open

        var auditStore = new InMemoryRequestAuditStore();
        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = true }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.Contains(alerts, a => a.Id == "circuit-open-gpt-4o" && a.Level == "critical");
    }

    [Fact]
    public void Check_HighFailureRate_ReturnsCriticalAlert()
    {
        var ledger = new FakeCostLedger();
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var now = DateTime.UtcNow;

        // Inject 20 failures out of 20 total in last 5 minutes.
        for (int i = 0; i < 20; i++)
        {
            auditStore.Append(new RequestAuditRecord(
                Timestamp: now.AddMinutes(-i * 0.1),
                RequestId: "req-" + i,
                Model: "gpt-4o",
                EstimatedInputTokens: 100,
                PromptTokens: 80,
                CompletionTokens: 40,
                Cost: 0.001m,
                LatencyMs: 200,
                SessionId: null,
                RoutingReason: "test",
                Success: false,
                ErrorMessage: "fail",
                IsStreaming: false));
        }

        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = true }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.Contains(alerts, a => a.Id == "high-failure-rate" && a.Level == "critical");
    }

    [Fact]
    public void Check_HighFailureRate_DisabledFailover_NoAlert()
    {
        var ledger = new FakeCostLedger();
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var now = DateTime.UtcNow;

        for (int i = 0; i < 20; i++)
        {
            auditStore.Append(new RequestAuditRecord(
                Timestamp: now.AddMinutes(-i * 0.1),
                RequestId: "req-" + i,
                Model: "gpt-4o",
                EstimatedInputTokens: 100,
                PromptTokens: 80,
                CompletionTokens: 40,
                Cost: 0.001m,
                LatencyMs: 200,
                SessionId: null,
                RoutingReason: "test",
                Success: false,
                ErrorMessage: "fail",
                IsStreaming: false));
        }

        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = false }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.DoesNotContain(alerts, a => a.Category == "failure-rate");
    }

    [Fact]
    public void Check_HighFailureRate_TooFewSamples_NoAlert()
    {
        var ledger = new FakeCostLedger();
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var now = DateTime.UtcNow;

        // Only 5 failures — below the 10-sample threshold.
        for (int i = 0; i < 5; i++)
        {
            auditStore.Append(new RequestAuditRecord(
                Timestamp: now.AddMinutes(-i * 0.1),
                RequestId: "req-" + i,
                Model: "gpt-4o",
                EstimatedInputTokens: 100,
                PromptTokens: 80,
                CompletionTokens: 40,
                Cost: 0.001m,
                LatencyMs: 200,
                SessionId: null,
                RoutingReason: "test",
                Success: false,
                ErrorMessage: "fail",
                IsStreaming: false));
        }

        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = true }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        var alerts = engine.Check();

        Assert.DoesNotContain(alerts, a => a.Category == "failure-rate");
    }

    [Fact]
    public void Check_NoAlerts_ReturnsEmpty()
    {
        var ledger = new FakeCostLedger(daily: 10m, total: 10m);
        var tracker = new ModelHealthTracker();
        var auditStore = new InMemoryRequestAuditStore();
        var options = new RouterOptions
        {
            Budget = new BudgetOptions { DailyBudgetUsd = 100m },
            Routing = new RoutingOptions { EnableFailover = false }
        };
        var monitor = new FakeOptionsMonitor<RouterOptions>(options);

        var engine = new AlertEngine(ledger, tracker, auditStore, monitor);
        Assert.Empty(engine.Check());
    }

    // ---- Helpers ----

    private sealed class FakeCostLedger : CostLedger
    {
        private readonly (decimal Daily, decimal Total) _spend;

        public FakeCostLedger(decimal daily = 0m, decimal total = 0m)
            : base(new InMemoryCostLedgerStore())
        {
            _spend = (daily, total);
        }

        public override (decimal Daily, decimal Total) GetSpend() => _spend;
    }

    private sealed class FakeOptionsMonitor<T> : IOptionsMonitor<T> where T : class, new()
    {
        private readonly T _current;

        public FakeOptionsMonitor(T current) => _current = current;

        public T Get(string? name) => _current;
        public T CurrentValue => _current;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

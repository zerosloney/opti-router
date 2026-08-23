using OptiRouter.Health;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 存储降级事件接入告警历史：降级只写日志时值班者无感知——降级/恢复迁移点
/// 必须同步记入 AlertHistory（Dashboard 告警历史与 Webhook 推送可见）。
/// </summary>
public class DegradationAlertTests
{
    [Fact]
    public void RedisCostLedger_ConnectionFailure_RecordsDegradationAlert()
    {
        var history = new AlertHistory();
        int connectTimeoutPort = 1; // 不可达端口：连接必然失败

        using var store = new RedisCostLedgerStore(
            $"localhost:{connectTimeoutPort},connectTimeout=100,abortConnect=true",
            alertHistory: history);

        var events = history.GetRecent(10);
        var alert = Assert.Single(events, e => e.AlertId == "cost-ledger-redis");
        Assert.Equal("alert", alert.EventType);
        Assert.Equal("warning", alert.Level);
        Assert.Equal(DegradationAlerts.Category, alert.Category);
    }

    [Fact]
    public void RedisCostLedger_NoConnectionString_NoAlert()
    {
        var history = new AlertHistory();

        using var store = new RedisCostLedgerStore(connectionString: null, alertHistory: history);

        Assert.Empty(history.GetRecent(10));
    }
}

using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// in-flight 预算预留（Reserve/Release）语义：TOCTOU 防护的核心机制。
/// 守卫读"已入账 + 预留"（GetEffective*），展示口径（GetSpend/GetDailySpend）不受影响。
/// </summary>
public class CostLedgerReservationTests
{
    [Fact]
    public void Reserve_AppliesToEffectiveDailySpend_Only()
    {
        var ledger = new CostLedger();
        ledger.Record(1m);

        ledger.Reserve(0.5m);

        Assert.Equal(1m, ledger.GetDailySpend());
        Assert.Equal(1.5m, ledger.GetEffectiveDailySpend());
    }

    [Fact]
    public void Release_DropsReservationFromEffectiveSpend()
    {
        var ledger = new CostLedger();
        ledger.Reserve(0.5m);

        ledger.Release(0.5m);

        Assert.Equal(0m, ledger.GetEffectiveDailySpend());
    }

    [Fact]
    public void ReserveRelease_SessionDimension_TracksPerSession()
    {
        var ledger = new CostLedger();
        ledger.Record(0.6m, "session-a");

        ledger.Reserve(0.5m, "session-a");

        Assert.Equal(1.1m, ledger.GetEffectiveSessionSpend("session-a"));
        Assert.Equal(0m, ledger.GetEffectiveSessionSpend("session-b"));

        ledger.Release(0.5m, "session-a");

        Assert.Equal(0.6m, ledger.GetEffectiveSessionSpend("session-a"));
    }

    [Fact]
    public void Release_MoreThanReserved_ClampsToZero_NoNegativeSpend()
    {
        var ledger = new CostLedger();
        ledger.Reserve(2m);

        ledger.Release(3m);

        Assert.Equal(0m, ledger.GetEffectiveDailySpend());
    }

    [Fact]
    public void Reserve_ZeroOrNegativeAmount_HasNoEffect()
    {
        var ledger = new CostLedger();
        ledger.Record(1m);

        ledger.Reserve(0m);
        ledger.Reserve(-1m);
        ledger.Release(0m);

        Assert.Equal(1m, ledger.GetEffectiveDailySpend());
    }

    [Fact]
    public void Concurrent_ReserveRecordRelease_NetReservationReturnsToZero()
    {
        var ledger = new CostLedger();
        const int iterations = 200;
        const int parallelism = 8;

        Parallel.For(0, parallelism, _ =>
        {
            for (int i = 0; i < iterations; i++)
            {
                ledger.Reserve(0.01m, "s");
                ledger.Record(0.005m, "s");
                ledger.Release(0.01m, "s");
            }
        });

        decimal committedDaily = ledger.GetDailySpend();
        Assert.Equal(0m, ledger.GetEffectiveDailySpend() - committedDaily);
        Assert.Equal(0m, ledger.GetEffectiveSessionSpend("s") - ledger.GetSessionSpend("s"));
    }
}

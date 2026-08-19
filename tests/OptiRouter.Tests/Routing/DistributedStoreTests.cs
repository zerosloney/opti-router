using System.Reflection;
using System.Runtime.CompilerServices;
using StackExchange.Redis;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class DistributedStoreTests
{
    [Fact]
    public void RedisCostLedgerStore_WhenNoConnection_FallsBackToInMemory()
    {
        using var store = new RedisCostLedgerStore(connectionString: null);

        decimal total1 = store.AddTotal(1.5m);
        Assert.Equal(1.5m, total1);

        decimal fetchedTotal = store.GetTotal();
        Assert.Equal(1.5m, fetchedTotal);

        decimal daily = store.AddDaily(DateTime.UtcNow, 2.0m);
        Assert.Equal(2.0m, daily);

        store.SaveCircuitState("gpt-4o", CircuitState.Open, 5, DateTime.UtcNow.AddMinutes(5));
        var states = store.LoadCircuitStates();
        Assert.True(states.ContainsKey("gpt-4o"));
        Assert.Equal(CircuitState.Open, states["gpt-4o"].State);
    }

    [Fact]
    public void PostgresCostLedgerStore_WhenNoConnection_FallsBackToInMemory()
    {
        using var store = new PostgresCostLedgerStore(connectionString: null);

        decimal total = store.AddTotal(3.0m);
        Assert.Equal(3.0m, total);

        decimal fetched = store.GetTotal();
        Assert.Equal(3.0m, fetched);

        decimal session = store.AddSession("sess-100", 0.8m);
        Assert.Equal(0.8m, session);
    }

    [Fact]
    public void PostgresCostLedgerStore_ResetDaily_WhenConnected_DoesNotClearDatePartition()
    {
        var fallback = new InMemoryCostLedgerStore();
        var today = DateTime.UtcNow;
        fallback.AddDaily(today, 2.5m);

        var store = Uninitialized<PostgresCostLedgerStore>();
        SetField(store, "_connectionString", "configured");
        SetField(store, "_fallback", fallback);
        SetField(store, "_degraded", 0);

        using (store)
        {
            store.ResetDaily();
            Assert.Equal(2.5m, fallback.GetDaily(today));
        }
    }

    [Fact]
    public void PostgresCostLedgerStore_ResetDaily_WhenDegraded_ClearsFallback()
    {
        var fallback = new InMemoryCostLedgerStore();
        var today = DateTime.UtcNow;
        fallback.AddDaily(today, 2.5m);

        var store = Uninitialized<PostgresCostLedgerStore>();
        SetField(store, "_connectionString", "configured");
        SetField(store, "_fallback", fallback);
        SetField(store, "_degraded", 1);

        using (store)
        {
            store.ResetDaily();
            Assert.Equal(0m, fallback.GetDaily(today));
        }
    }

    [Fact]
    public void RedisCostLedgerStore_ResetDaily_WhenConnected_DoesNotDeleteDateKey()
    {
        var fallback = new InMemoryCostLedgerStore();
        var today = DateTime.UtcNow;
        fallback.AddDaily(today, 3.5m);

        var store = Uninitialized<RedisCostLedgerStore>();
        SetField(store, "_db", DispatchProxy.Create<IDatabase, ThrowOnInvocationProxy>());
        SetField(store, "_fallback", fallback);

        using (store)
        {
            store.ResetDaily();
            Assert.Equal(3.5m, fallback.GetDaily(today));
        }
    }

    [Fact]
    public void RedisCostLedgerStore_ResetDaily_WhenDisconnected_ClearsFallback()
    {
        var fallback = new InMemoryCostLedgerStore();
        var today = DateTime.UtcNow;
        fallback.AddDaily(today, 3.5m);

        using var store = new RedisCostLedgerStore(connectionString: null, fallback: fallback);
        store.ResetDaily();

        Assert.Equal(0m, fallback.GetDaily(today));
    }

    [Fact]
    public void PostgresRequestAuditStore_WhenNoConnection_FallsBackToInMemory()
    {
        using var store = new PostgresRequestAuditStore(connectionString: null);

        var record = new RequestAuditRecord(
            Timestamp: DateTime.UtcNow,
            RequestId: "req-test-1",
            Model: "gpt-4o-mini",
            EstimatedInputTokens: 100,
            PromptTokens: 100,
            CompletionTokens: 50,
            Cost: 0.001m,
            LatencyMs: 120,
            SessionId: "sess-1",
            RoutingReason: "test",
            Success: true,
            ErrorMessage: null,
            IsStreaming: false
        );

        store.Append(record);

        var recent = store.GetRecent(10);
        Assert.Single(recent);
        Assert.Equal("req-test-1", recent[0].RequestId);
    }

    [Fact]
    public void RoutingOptions_OtlpTracingDefaults_AreCorrect()
    {
        var options = new RoutingOptions();

        Assert.False(options.EnableOtlpTracing);
        Assert.Equal("http://localhost:4317", options.OtlpEndpoint);
        Assert.Equal("grpc", options.OtlpProtocol);
        Assert.Equal("OptiRouter", options.OtlpServiceName);
    }

    private static T Uninitialized<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static void SetField<T>(T instance, string name, object? value)
    {
        FieldInfo field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(T).FullName, name);
        field.SetValue(instance, value);
    }

    private class ThrowOnInvocationProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("Connected Redis ResetDaily must not invoke Redis commands");
    }
}

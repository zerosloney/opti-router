using Microsoft.Extensions.Diagnostics.HealthChecks;
using OptiRouter.Health;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Health;

public class CostLedgerHealthCheckTests
{
    [Fact]
    public async Task CheckHealth_WhenStoreHealthy_ReturnsHealthy()
    {
        using var store = new InMemoryCostLedgerStore();
        var check = new CostLedgerHealthCheck(store);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CheckHealth_WhenStoreThrows_ReturnsUnhealthy()
    {
        using var store = new InMemoryCostLedgerStore();
        store.Dispose(); // 关闭后再次调用应抛异常
        var check = new CostLedgerHealthCheck(store);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task CheckHealth_SqliteHealthy_ReturnsHealthy()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".db");
        try
        {
            using var store = new SqliteCostLedgerStore(path);
            var check = new CostLedgerHealthCheck(store);

            var result = await check.CheckHealthAsync(new HealthCheckContext());

            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            // 尽力清理，不因残留文件导致测试失败。
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
            try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
        }
    }
}
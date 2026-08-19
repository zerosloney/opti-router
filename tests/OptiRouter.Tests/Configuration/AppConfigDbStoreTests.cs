using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

public sealed class AppConfigDbStoreTests
{
    [Fact]
    public void RoutingBudgetDocuments_StaleVersionCannotOverwriteNewerSnapshot()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-config-cas-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new AppConfigDbStore(dbPath);
            store.SaveDocument(AppConfigDbStore.RoutingScope, "{\"enableFailover\":true}");
            store.SaveDocument(AppConfigDbStore.BudgetScope, "{\"dailyBudgetUsd\":10}");
            var original = store.LoadRoutingBudgetSnapshot();

            bool firstSaved = store.TrySaveRoutingBudgetDocuments(
                original.Version,
                "{\"enableFailover\":false}",
                "{\"dailyBudgetUsd\":20}",
                out string savedVersion);
            bool staleSaved = store.TrySaveRoutingBudgetDocuments(
                original.Version,
                "{\"enableFailover\":true}",
                "{\"dailyBudgetUsd\":30}",
                out string currentVersion);

            Assert.True(firstSaved);
            Assert.NotEqual(original.Version, savedVersion);
            Assert.False(staleSaved);
            Assert.Equal(savedVersion, currentVersion);
            Assert.Equal("{\"enableFailover\":false}", store.LoadDocument(AppConfigDbStore.RoutingScope));
            Assert.Equal("{\"dailyBudgetUsd\":20}", store.LoadDocument(AppConfigDbStore.BudgetScope));
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public void RoutingBudgetSnapshot_VersionChangesWhenEitherDocumentChanges()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-config-version-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new AppConfigDbStore(dbPath);
            var empty = store.LoadRoutingBudgetSnapshot();
            store.SaveDocument(AppConfigDbStore.RoutingScope, "{}");
            var routing = store.LoadRoutingBudgetSnapshot();
            store.SaveDocument(AppConfigDbStore.BudgetScope, "{}");
            var budget = store.LoadRoutingBudgetSnapshot();

            Assert.NotEqual(empty.Version, routing.Version);
            Assert.NotEqual(routing.Version, budget.Version);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public void ConfigChangeHistory_AppendLoadDescending_AndPruneTo200()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-config-history-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new AppConfigDbStore(dbPath);
            for (int i = 1; i <= 205; i++)
            {
                store.AppendConfigChange("admin", $"[{{\"key\":\"Routing:N{i}\",\"from\":true,\"to\":false}}]");
            }

            var changes = store.LoadConfigChanges(5);
            Assert.Equal(5, changes.Count);
            // 倒序：最新（N205）在最前
            Assert.Contains("Routing:N205", changes[0].Summary, StringComparison.Ordinal);
            Assert.Equal("admin", changes[0].Actor);
            Assert.All(changes, c => Assert.False(string.IsNullOrEmpty(c.Ts)));

            // 裁剪到 200 条：最旧一条应为 N6（N1..N5 已淘汰），最新为 N205
            var all = store.LoadConfigChanges(300);
            Assert.Equal(200, all.Count);
            Assert.Contains("Routing:N205", all[0].Summary, StringComparison.Ordinal);
            Assert.Contains("Routing:N6\"", all[^1].Summary, StringComparison.Ordinal);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public void EvalBatches_SaveLoadRoundTrip_AndPruneToMax()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-eval-batches-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new AppConfigDbStore(dbPath);
            store.SaveEvalBatch("batch-1", "2026-01-01T00:00:00.0000000Z", "{\"batchId\":\"batch-1\",\"totalCases\":4}", maxBatches: 2);
            store.SaveEvalBatch("batch-2", "2026-01-02T00:00:00.0000000Z", "{\"batchId\":\"batch-2\",\"totalCases\":4}", maxBatches: 2);
            store.SaveEvalBatch("batch-3", "2026-01-03T00:00:00.0000000Z", "{\"batchId\":\"batch-3\",\"totalCases\":4}", maxBatches: 2);

            var batches = store.LoadEvalBatches();
            // 裁剪到 2 批且按时间倒序
            Assert.Equal(2, batches.Count);
            Assert.Equal("batch-3", batches[0].BatchId);
            Assert.Equal("batch-2", batches[1].BatchId);
            Assert.Contains("\"totalCases\":4", batches[0].ReportJson, StringComparison.Ordinal);
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

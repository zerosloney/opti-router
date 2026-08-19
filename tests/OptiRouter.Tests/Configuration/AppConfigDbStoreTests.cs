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

    private static void DeleteSqliteFiles(string dbPath)
    {
        foreach (string path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}

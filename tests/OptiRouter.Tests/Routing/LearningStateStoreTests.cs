using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class LearningStateStoreTests
{
    // SQLite on Windows holds file handles briefly after Dispose(); avoid deleting
    // temp DB files in finally blocks (same approach as CostLedgerStoreTests).
    private static string TempDbPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "optirouter-learning-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "learning.db");
    }

    [Fact]
    public void Thompson_SaveAndLoad_RoundTrips()
    {
        string path = TempDbPath();
        using var store = new SqliteLearningStateStore(path);
        IThompsonStateStore ts = store;
        ts.Save("model-a", 2.0, 1.0);
        ts.Save("model-b", 5.0, 3.0);

        var all = ts.LoadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal((2.0, 1.0), all["model-a"]);
        Assert.Equal((5.0, 3.0), all["model-b"]);
    }

    [Fact]
    public void Thompson_Overwrite_UpdatesExisting()
    {
        string path = TempDbPath();
        using var store = new SqliteLearningStateStore(path);
        IThompsonStateStore ts = store;
        ts.Save("model-a", 1.0, 1.0);
        ts.Save("model-a", 10.0, 2.0);

        var all = ts.LoadAll();
        Assert.Single(all);
        Assert.Equal((10.0, 2.0), all["model-a"]);
    }

    [Fact]
    public void Thompson_PersistsAcrossStoreInstances()
    {
        string path = TempDbPath();
        using (var store1 = new SqliteLearningStateStore(path))
        {
            IThompsonStateStore ts = store1;
            ts.Save("model-a", 3.0, 2.0);
        }

        using var store2 = new SqliteLearningStateStore(path);
        IThompsonStateStore ts2 = store2;
        var all = ts2.LoadAll();
        Assert.Single(all);
        Assert.Equal((3.0, 2.0), all["model-a"]);
    }

    [Fact]
    public void Bandit_SaveAndLoad_RoundTrips()
    {
        string path = TempDbPath();
        using var store = new SqliteLearningStateStore(path);
        IBanditStateStore bs = store;
        int dim = 3;
        var a = new double[dim, dim];
        for (int i = 0; i < dim; i++) a[i, i] = 1.0;
        var b = new double[] { 0.1, 0.2, 0.3 };
        bs.Save("model-a", dim, a, b, 5);

        var all = bs.LoadAll();
        Assert.Single(all);
        var (loadedDim, loadedA, loadedB, n) = all["model-a"];
        Assert.Equal(dim, loadedDim);
        Assert.Equal(5, n);
        Assert.Equal(b, loadedB);
        for (int i = 0; i < dim; i++)
            Assert.Equal(1.0, loadedA[i, i]);
    }

    [Fact]
    public void Bandit_PersistsAcrossStoreInstances()
    {
        string path = TempDbPath();
        int dim = 2;
        var a = new double[dim, dim];
        a[0, 0] = 1.0;
        a[0, 1] = 0.5;
        a[1, 0] = 0.5;
        a[1, 1] = 1.0;
        var b = new double[] { 0.1, 0.2 };

        using (var store1 = new SqliteLearningStateStore(path))
        {
            IBanditStateStore bs = store1;
            bs.Save("model-a", dim, a, b, 10);
        }

        using var store2 = new SqliteLearningStateStore(path);
        IBanditStateStore bs2 = store2;
        var all = bs2.LoadAll();
        Assert.Single(all);
        var (loadedDim, loadedA, loadedB, n) = all["model-a"];
        Assert.Equal(dim, loadedDim);
        Assert.Equal(10, n);
        Assert.Equal(b, loadedB);
        Assert.Equal(a[0, 0], loadedA[0, 0]);
        Assert.Equal(a[0, 1], loadedA[0, 1]);
        Assert.Equal(a[1, 0], loadedA[1, 0]);
        Assert.Equal(a[1, 1], loadedA[1, 1]);
    }

    [Fact]
    public void Bandit_MismatchedDim_SkipsInvalidRecord()
    {
        string path = TempDbPath();
        using var store = new SqliteLearningStateStore(path);
        IBanditStateStore bs = store;
        // 写入 dim=2 的记录
        var a2 = new double[2, 2];
        a2[0, 0] = 1.0;
        a2[1, 1] = 1.0;
        var b2 = new double[] { 0.1, 0.2 };
        bs.Save("model-a", 2, a2, b2, 1);

        // 用 dim=11 构造 ContextualBanditState，dim 不匹配应跳过
        var bandit = new ContextualBanditState(11, store);
        Assert.Equal(0, bandit.Count);
    }

    [Fact]
    public void NullLearningStateStore_IsNoOp()
    {
        var store = NullLearningStateStore.Instance;
        IThompsonStateStore ts = store;
        ts.Save("model-a", 1.0, 2.0);
        var thompson = ts.LoadAll();
        Assert.Empty(thompson);
        IBanditStateStore bs = store;
        var bandit = bs.LoadAll();
        Assert.Empty(bandit);
    }

    [Fact]
    public void ThompsonStateStore_LoadsFromPersistenceOnConstruction()
    {
        string path = TempDbPath();
        using (var persistence = new SqliteLearningStateStore(path))
        {
            IThompsonStateStore ts = persistence;
            ts.Save("model-a", 2.0, 3.0);
            ts.Save("model-b", 1.0, 1.0);
        }

        var store = new ThompsonStateStore(new SqliteLearningStateStore(path));
        var stats = store.GetOrAdd("model-a");
        Assert.Equal(2.0, stats.Alpha);
        Assert.Equal(3.0, stats.Beta);

        var statsB = store.GetOrAdd("model-b");
        Assert.Equal(1.0, statsB.Alpha);
        Assert.Equal(1.0, statsB.Beta);
    }

    [Fact]
    public void ContextualBanditState_LoadsFromPersistenceOnConstruction()
    {
        string path = TempDbPath();
        int dim = 3;
        var a = new double[dim, dim];
        for (int i = 0; i < dim; i++) a[i, i] = 2.0;
        var b = new double[] { 0.1, 0.2, 0.3 };

        using (var persistence = new SqliteLearningStateStore(path))
        {
            IBanditStateStore bs = persistence;
            bs.Save("model-a", dim, a, b, 7);
        }

        var bandit = new ContextualBanditState(dim, new SqliteLearningStateStore(path));
        var arm = bandit.GetOrAdd("model-a");
        Assert.Equal(7, arm.N);
        for (int i = 0; i < dim; i++)
            Assert.Equal(2.0, arm.A[i, i]);
        Assert.Equal(b, arm.B);
    }

    [Fact]
    public void ThompsonStateStore_RecordOutcome_PersistsAfterUpdate()
    {
        string path = TempDbPath();
        using var persistence = new SqliteLearningStateStore(path);
        var store = new ThompsonStateStore(persistence);
        store.RecordOutcome("model-a", true, 1.0);  // good => alpha += 1.0
        store.RecordOutcome("model-a", false, 1.0); // bad => beta += 1.0

        // 重建 store，验证持久化
        using var persistence2 = new SqliteLearningStateStore(path);
        var store2 = new ThompsonStateStore(persistence2);
        var stats = store2.GetOrAdd("model-a");
        Assert.Equal(2.0, stats.Alpha);
        Assert.Equal(2.0, stats.Beta);
    }

    [Fact]
    public void BanditStateStore_Update_PersistsAfterUpdate()
    {
        string path = TempDbPath();
        int dim = 2;
        using var persistence = new SqliteLearningStateStore(path);
        var bandit = new ContextualBanditState(dim, persistence);
        var ctx = new double[] { 1.0, 0.0 };
        bandit.Update("model-a", ctx, 1.0, 1.0);

        // 重建 store，验证持久化
        using var persistence2 = new SqliteLearningStateStore(path);
        var bandit2 = new ContextualBanditState(dim, persistence2);
        var arm = bandit2.GetOrAdd("model-a");
        Assert.Equal(1, arm.N);
        // A = I + x*x^T = [[2,0],[0,1]] after one update with x=[1,0]
        Assert.Equal(2.0, arm.A[0, 0]);
        Assert.Equal(0.0, arm.A[0, 1]);
        Assert.Equal(0.0, arm.A[1, 0]);
        Assert.Equal(1.0, arm.A[1, 1]);
        Assert.Equal(1.0, arm.B[0]);
        Assert.Equal(0.0, arm.B[1]);
    }
}

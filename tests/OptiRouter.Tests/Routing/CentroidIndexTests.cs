using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public sealed class CentroidIndexTests
{
    private static float[] UnitVector(int seed, int dim = 1024)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        double sumSq = 0;
        for (int i = 0; i < dim; i++)
        {
            v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            sumSq += v[i] * v[i];
        }
        float norm = (float)Math.Sqrt(sumSq);
        for (int i = 0; i < dim; i++) v[i] /= norm;
        return v;
    }

    /// <summary>对向量叠加小扰动后归一化：epsilon 越大相似度越低。</summary>
    private static float[] Perturb(float[] v, double epsilon, int seed = 7)
    {
        var rng = new Random(seed);
        var r = new float[v.Length];
        double sumSq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            r[i] = v[i] + (float)(epsilon * (rng.NextDouble() * 2.0 - 1.0));
            sumSq += r[i] * r[i];
        }
        float norm = (float)Math.Sqrt(sumSq);
        for (int i = 0; i < r.Length; i++) r[i] /= norm;
        return r;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return Math.Clamp(dot, -1.0, 1.0);
    }

    [Fact]
    public void Search_HighSimilarityVector_FindsInsertedKey()
    {
        var index = new CentroidIndex(maxCentroids: 64, topBuckets: 2);
        var v = UnitVector(1);
        index.Add("target", v);

        // 高相似向量（余弦 ~0.94）应命中目标质心桶
        var query = Perturb(v, epsilon: 0.02);
        Assert.True(Cosine(v, query) > 0.9);
        var candidates = index.Search(query);

        Assert.Contains("target", candidates);
    }

    [Fact]
    public void Search_ModerateSimilarity_FindsKeyViaTopBuckets()
    {
        // 单个质心场景：无论相似度高低，查询只走唯一质心桶 → 命中
        var index = new CentroidIndex(maxCentroids: 64, topBuckets: 2);
        var v = UnitVector(1);
        index.Add("target", v);

        var query = Perturb(v, epsilon: 0.15); // 余弦约 0.7
        var candidates = index.Search(query);

        Assert.Contains("target", candidates);
    }

    [Fact]
    public void Search_UnrelatedVector_DoesNotMatch()
    {
        // 多质心场景：目标归入其最近质心桶，与目标区域无关的查询不应命中该桶
        var index = new CentroidIndex(maxCentroids: 64, topBuckets: 2);
        for (int i = 0; i < 32; i++)
        {
            index.Add($"seed{i}", UnitVector(100 + i));
        }
        var v = UnitVector(1);
        index.Add("target", v);

        var unrelated = UnitVector(999);
        var candidates = index.Search(unrelated);

        Assert.DoesNotContain("target", candidates);
    }

    [Fact]
    public void Search_ManyEntries_TopBucketsCoverNearbyKey()
    {
        // 64 质心、多条目：与目标高相似的查询应能在 top-2 质心桶中找到目标
        var index = new CentroidIndex(maxCentroids: 64, topBuckets: 2);

        // 前 64 个作为质心（种子）
        for (int i = 0; i < 64; i++)
        {
            index.Add($"seed{i}", UnitVector(100 + i));
        }

        // 目标条目归入其最近质心桶
        var target = UnitVector(1);
        index.Add("target", target);

        // 高相似查询（同一区域）
        var query = Perturb(target, epsilon: 0.02);
        var candidates = index.Search(query);

        Assert.Contains("target", candidates);
    }

    [Fact]
    public void Remove_KeyNoLongerFound()
    {
        var index = new CentroidIndex();
        var v = UnitVector(3);
        index.Add("gone", v);
        index.Remove("gone", v);

        var candidates = index.Search(Perturb(v, epsilon: 0.01));
        Assert.DoesNotContain("gone", candidates);
    }

    [Fact]
    public async Task SemanticResponseCache_IndexedAndLinearPaths_ReturnSameBestMatch()
    {
        // 同一数据集下，索引路径与线性扫描路径的最佳命中一致（含典型中文相似文本）。
        var indexedCache = new SemanticResponseCache(enableAnnIndex: true);
        var linearCache = new SemanticResponseCache(enableAnnIndex: false);

        string[] prompts =
        [
            "请解释什么是面向对象编程中的多态性",
            "How does distributed consensus work in modern systems",
            "Best practices for prompt engineering with large language models",
            "What is the capital of France and its population",
            "Explain TCP congestion control algorithms like Vegas and Reno",
            "How to implement a semantic cache with vector similarity search",
            "Guide to Kubernetes networking and service mesh architecture",
            "History and culture of ancient Rome"
        ];

        foreach (var p in prompts)
        {
            var resp = new OptiRouter.Clients.RawChatResponse($"{{\"content\":\"answer-{p}\"}}", null);
            await indexedCache.StoreAsync(p, resp, TimeSpan.FromMinutes(30));
            await linearCache.StoreAsync(p, resp, TimeSpan.FromMinutes(30));
        }

        // 与第一条中文 prompt 相似但不同的改写
        string query = "请说明面向对象编程里的多态性是什么";
        var (idxHit, idxResp, _, idxMatched) = await indexedCache.TryGetAsync(query, 0.50f);
        var (linHit, linResp, _, linMatched) = await linearCache.TryGetAsync(query, 0.50f);

        Assert.Equal(linHit, idxHit);
        Assert.Equal(linMatched, idxMatched);
        Assert.True(idxHit, "Indexed path should hit for the similar Chinese prompt");
        Assert.NotNull(idxResp);
    }

    [Fact]
    public async Task SemanticResponseCache_ExpiredEntries_AreLazilyCleanedFromIndex()
    {
        var cache = new SemanticResponseCache(enableAnnIndex: true, maxEntries: 100);

        await cache.StoreAsync("stale prompt about baking sourdough bread", new OptiRouter.Clients.RawChatResponse("{}", null), TimeSpan.FromMilliseconds(-1));
        await cache.StoreAsync("history of ancient rome and its culture", new OptiRouter.Clients.RawChatResponse("{}", null), TimeSpan.FromMinutes(30));

        // 过期条目不应被命中；查询不抛异常（与存活条目文本差异大，避免误命中）
        var (hit, resp, _, _) = await cache.TryGetAsync("stale prompt about baking sourdough bread", 0.9f);
        Assert.False(hit);
    }
}

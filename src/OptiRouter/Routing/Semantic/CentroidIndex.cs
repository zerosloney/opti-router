namespace OptiRouter.Routing;

/// <summary>
/// 质心分桶 ANN 索引 (Centroid Index)。
/// 前 K 个条目作为质心（贪心种子），后续条目归入最近质心桶；查询时与全部质心做余弦
/// 比较取 top-m 桶并集精排，把语义缓存的 O(n) 全量扫描降为 O(K·dim + 候选·dim)。
/// </summary>
/// <remarks>
/// 对"相似但不同文本"（余弦 0.6~0.95）的召回远优于随机投影 LSH——语义缓存的典型
/// 查询正是同义改写，质心分桶在低维近邻语义下保持稳健。漏检的后果仅是缓存 miss 走上游，
/// 无正确性风险；top-m 与质心数可调平衡召回与精排开销。
/// </remarks>
internal sealed class CentroidIndex
{
    private readonly int _maxCentroids;
    private readonly int _topBuckets;
    private readonly List<float[]> _centroids = new();
    private readonly List<HashSet<string>> _buckets = new();
    private readonly Dictionary<string, int> _assignment = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public int CentroidCount => _centroids.Count;

    public CentroidIndex(int maxCentroids = 64, int topBuckets = 2)
    {
        _maxCentroids = Math.Max(1, maxCentroids);
        _topBuckets = Math.Max(1, topBuckets);
    }

    /// <summary>
    /// 插入条目：质心未满时作为新质心，否则归入最近质心桶。
    /// </summary>
    public void Add(string key, float[] vector)
    {
        lock (_lock)
        {
            if (_centroids.Count < _maxCentroids)
            {
                _centroids.Add(vector);
                var bucket = new HashSet<string>(StringComparer.Ordinal) { key };
                _buckets.Add(bucket);
                _assignment[key] = _centroids.Count - 1;
            }
            else
            {
                int nearest = NearestCentroid(vector);
                _buckets[nearest].Add(key);
                _assignment[key] = nearest;
            }
        }
    }

    /// <summary>
    /// 移除条目（需与原向量同源；按分配记录定位桶，用于惰性清理淘汰项）。
    /// </summary>
    public void Remove(string key, float[] vector)
    {
        lock (_lock)
        {
            if (_assignment.TryGetValue(key, out int index) && index >= 0 && index < _buckets.Count)
            {
                _buckets[index].Remove(key);
                _assignment.Remove(key);
            }
        }
    }

    /// <summary>
    /// 查询候选集：与全部质心余弦比较，取最近 top-m 桶的并集。
    /// </summary>
    public List<string> Search(float[] vector)
    {
        lock (_lock)
        {
            if (_centroids.Count == 0)
            {
                return new List<string>();
            }

            // 与全部质心比较（归一化向量的点积即余弦）
            var scored = new (int Index, float Score)[_centroids.Count];
            for (int i = 0; i < _centroids.Count; i++)
            {
                scored[i] = (i, Dot(vector, _centroids[i]));
            }
            Array.Sort(scored, (a, b) => b.Score.CompareTo(a.Score));

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            int bucketCount = Math.Min(_topBuckets, scored.Length);
            for (int i = 0; i < bucketCount; i++)
            {
                candidates.UnionWith(_buckets[scored[i].Index]);
            }
            return candidates.ToList();
        }
    }

    private int NearestCentroid(float[] vector)
    {
        int best = 0;
        float bestScore = float.MinValue;
        for (int i = 0; i < _centroids.Count; i++)
        {
            float score = Dot(vector, _centroids[i]);
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }
        return best;
    }

    private static float Dot(float[] a, float[] b)
    {
        float dot = 0f;
        int length = Math.Min(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }
}

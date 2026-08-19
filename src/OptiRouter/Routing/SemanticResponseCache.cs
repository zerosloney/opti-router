using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.Intrinsics;
using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 深度语义响应缓存实现。
/// 支持基于 CJK 字符 n-gram TF-IDF 与 SIMD 加速的余弦相似度极速检索。
/// </summary>
public sealed class SemanticResponseCache : ISemanticResponseCache
{
    private readonly ConcurrentDictionary<string, CacheItem> _store = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private readonly ISemanticVectorEngine? _vectorEngine;
    private readonly CentroidIndex? _index;

    private sealed record CacheItem(
        string Prompt,
        float[] NormalizedVector,
        RawChatResponse Response,
        DateTime ExpiresAtUtc,
        DateTime CreatedAtUtc);

    /// <summary>
    /// 初始化语义响应缓存。
    /// </summary>
    /// <param name="logger">可选日志，向量化降级时留诊断线索。</param>
    /// <param name="maxEntries">最大条目数。</param>
    /// <param name="vectorEngine">可选的向量匹配引擎（如 ONNX 或 DenseEmbeddingVectorEngine）。为空时使用内置快速 CJK 特征投影。</param>
    /// <param name="enableAnnIndex">是否启用质心分桶索引加速查询（将 O(n) 扫描降为 O(候选集)）。默认开启。</param>
    public SemanticResponseCache(int maxEntries = 10000, ISemanticVectorEngine? vectorEngine = null, bool enableAnnIndex = true,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        _maxEntries = Math.Max(100, maxEntries);
        _vectorEngine = vectorEngine;
        _index = enableAnnIndex ? new CentroidIndex() : null;
        _logger = logger;
    }

    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    /// <inheritdoc />
    public Task<(bool Hit, RawChatResponse? Response, double Similarity, string? MatchedPrompt)> TryGetAsync(
        string prompt,
        float similarityThreshold = 0.95f,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || _store.IsEmpty)
        {
            return Task.FromResult<(bool, RawChatResponse?, double, string?)>((false, null, 0.0, null));
        }

        DateTime now = DateTime.UtcNow;
        var queryVector = GetVector(prompt);

        double maxSim = 0.0;
        CacheItem? bestMatch = null;

        if (_index is not null)
        {
            // 质心分桶候选集精排：只对候选桶做精确余弦，淘汰条目惰性清理（桶内死 key 在
            // TryGetValue 失败或过期时从索引移除，避免死 key 占满候选集）。
            var candidates = _index.Search(queryVector);
            foreach (var key in candidates)
            {
                if (!_store.TryGetValue(key, out var item))
                {
                    _index.Remove(key, queryVector);
                    continue;
                }
                if (item.ExpiresAtUtc <= now)
                {
                    _store.TryRemove(key, out _);
                    _index.Remove(key, queryVector);
                    continue;
                }

                double sim = ComputeCosineSimilarity(queryVector, item.NormalizedVector);
                if (sim > maxSim)
                {
                    maxSim = sim;
                    bestMatch = item;
                }
            }
        }
        else
        {
            foreach (var kvp in _store)
            {
                var item = kvp.Value;
                if (item.ExpiresAtUtc <= now)
                {
                    _store.TryRemove(kvp.Key, out _);
                    continue;
                }

                double sim = ComputeCosineSimilarity(queryVector, item.NormalizedVector);
                if (sim > maxSim)
                {
                    maxSim = sim;
                    bestMatch = item;
                }
            }
        }

        if (bestMatch != null && maxSim >= similarityThreshold)
        {
            return Task.FromResult<(bool, RawChatResponse?, double, string?)>((true, bestMatch.Response, maxSim, bestMatch.Prompt));
        }

        return Task.FromResult<(bool, RawChatResponse?, double, string?)>((false, null, maxSim, null));
    }

    /// <inheritdoc />
    public Task StoreAsync(
        string prompt,
        RawChatResponse response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt) || response == null)
        {
            return Task.CompletedTask;
        }

        DateTime now = DateTime.UtcNow;

        // 容量控制：如达到上限则清理过期项及早期的 20% 条目
        if (_store.Count >= _maxEntries)
        {
            EvictExpiredOrOldest(now);
        }

        var vector = GetVector(prompt);
        var item = new CacheItem(prompt, vector, response, now.Add(ttl), now);

        _store[prompt] = item;
        _index?.Add(prompt, vector);
        return Task.CompletedTask;
    }

    private float[] GetVector(string prompt)
    {
        if (_vectorEngine != null)
        {
            try
            {
                var vec = _vectorEngine.Embed(prompt);
                if (vec != null && vec.Length > 0) return vec;
            }
            catch (Exception ex)
            {
                // 降级使用内置向量投影；留日志使 ONNX/向量化故障可观测
                _logger?.LogWarning(ex, "SemanticCache vector embedding failed; falling back to hash projection");
            }
        }
        return BuildNormalizedVector(prompt);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _store.Clear();
    }

    private void EvictExpiredOrOldest(DateTime now)
    {
        foreach (var kvp in _store)
        {
            if (kvp.Value.ExpiresAtUtc <= now)
            {
                _store.TryRemove(kvp.Key, out _);
            }
        }

        if (_store.Count >= _maxEntries)
        {
            int toRemove = _store.Count / 5;
            // 按创建时间最旧驱逐（修复前对 ConcurrentDictionary.Keys.Take 取任意序，可能随机驱逐热条目）
            foreach (var key in _store.OrderBy(k => k.Value.CreatedAtUtc).Take(toRemove).Select(k => k.Key))
            {
                _store.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// 将文本转化为归一化特征向量（结合 CJK 单字与 2-gram 散列映射至定长向量）。
    /// </summary>
    private static float[] BuildNormalizedVector(string text, int vectorSize = 1024)
    {
        float[] vector = new float[vectorSize];
        string cleaned = text.Trim().ToLowerInvariant();
        if (cleaned.Length == 0) return vector;

        // 1. CJK 单字频率统计（权重 1.0）
        for (int i = 0; i < cleaned.Length; i++)
        {
            int index = Math.Abs((cleaned[i] * 31) ^ 0x55555555) % vectorSize;
            vector[index] += 1.0f;
        }

        // 2. 2-gram 散列补充（权重 1.5）
        for (int i = 0; i < cleaned.Length - 1; i++)
        {
            int hash = HashTwoChars(cleaned[i], cleaned[i + 1]);
            int index = Math.Abs(hash) % vectorSize;
            vector[index] += 1.5f;
        }

        // 3. L2 SIMD 归一化 (使向量模长为 1，此时点积等价于余弦相似度)
        float sumSq = 0.0f;
        for (int i = 0; i < vectorSize; i++)
        {
            sumSq += vector[i] * vector[i];
        }

        if (sumSq > 1e-6f)
        {
            float norm = MathF.Sqrt(sumSq);
            for (int i = 0; i < vectorSize; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    private static int HashTwoChars(char c1, char c2)
    {
        return (c1 << 16) | c2;
    }

    /// <summary>
    /// 使用 SIMD 极速计算两个归一化向量的点积（即余弦相似度）。
    /// </summary>
    private static double ComputeCosineSimilarity(float[] v1, float[] v2)
    {
        if (v1.Length != v2.Length) return 0.0;

        float dot = 0.0f;
        int size = v1.Length;
        int simdLength = Vector<float>.Count;
        int i = 0;

        for (; i <= size - simdLength; i += simdLength)
        {
            var va = new Vector<float>(v1, i);
            var vb = new Vector<float>(v2, i);
            dot += Vector.Dot(va, vb);
        }

        for (; i < size; i++)
        {
            dot += v1[i] * v2[i];
        }

        return Math.Clamp(dot, 0.0f, 1.0f);
    }
}

using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 128 维特征哈希匹配引擎。默认实现是稳定的词法特征投影，不是训练得到的语义 embedding；
/// 可注入真正的 embedding 函数替代默认投影。零外部依赖并使用 SIMD 计算余弦相似度。
/// </summary>
public sealed class DenseEmbeddingVectorEngine : ISemanticVectorEngine
{
    private const int EmbeddingDimension = 128;
    private readonly Func<string, float[]>? _customEmbedFunc;
    private readonly ConcurrentDictionary<string, float[]> _phraseCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 初始化特征哈希匹配引擎。
    /// </summary>
    /// <param name="customEmbedFunc">可选的自定义向量生成函数（如 ONNX 或外部 API）。为 null 时使用内置稳定特征哈希。</param>
    public DenseEmbeddingVectorEngine(Func<string, float[]>? customEmbedFunc = null)
    {
        _customEmbedFunc = customEmbedFunc;
    }

    /// <inheritdoc />
    public (SemanticRouteOptions? MatchedRoute, double MaxSimilarity) Match(
        string queryText,
        List<SemanticRouteOptions> routes)
    {
        if (string.IsNullOrWhiteSpace(queryText) || routes is null || routes.Count == 0)
        {
            return (null, 0.0);
        }

        var queryVector = GetEmbedding(queryText);
        if (queryVector is null || queryVector.Length == 0)
        {
            return (null, 0.0);
        }

        double maxSimilarity = 0.0;
        SemanticRouteOptions? bestRoute = null;

        foreach (var route in routes)
        {
            if (route.Phrases is null || route.Phrases.Count == 0) continue;

            foreach (var phrase in route.Phrases)
            {
                if (string.IsNullOrWhiteSpace(phrase)) continue;

                var phraseVector = _phraseCache.GetOrAdd(phrase, GetEmbedding);
                if (phraseVector is null || phraseVector.Length == 0) continue;

                double sim = CosineSimilarity(queryVector, phraseVector);
                if (sim > maxSimilarity)
                {
                    maxSimilarity = sim;
                    bestRoute = route;
                }
            }
        }

        return (bestRoute, maxSimilarity);
    }

    /// <summary>
    /// 对输入文本计算 L2 归一化向量。默认是词法特征哈希；注入函数时由调用方决定语义。
    /// </summary>
    public float[] GetEmbedding(string text)
    {
        if (_customEmbedFunc is not null)
        {
            var customVector = _customEmbedFunc(text);
            NormalizeInPlace(customVector);
            return customVector;
        }

        return ComputeDefaultEmbedding(text);
    }

    private static float[] ComputeDefaultEmbedding(string text)
    {
        float[] vector = new float[EmbeddingDimension];
        if (string.IsNullOrWhiteSpace(text)) return vector;

        string normalized = text.Trim().ToLowerInvariant();
        var tokens = TfIdfSemanticVectorEngine.Tokenize(normalized);

        // FNV-1a 固定 UTF-8 字节序列，避免 string.GetHashCode 的进程随机种子导致重启后路由漂移。
        foreach (var token in tokens)
        {
            uint h = ComputeStableHash(token);
            int dim1 = (int)(h % EmbeddingDimension);
            vector[dim1] += 1.0f;
        }

        // 2. CJK 连续 Bigram 语义特征（仅作用于 CJK 字符，避免英文杂讯碰撞）
        for (int i = 0; i < normalized.Length - 1; i++)
        {
            char c1 = normalized[i];
            char c2 = normalized[i + 1];
            if (IsCjk(c1) && IsCjk(c2))
            {
                int bigramHash = (int)(ComputeStableHash(string.Concat(c1, c2)) % EmbeddingDimension);
                vector[bigramHash] += 0.8f;
            }
        }

        NormalizeInPlace(vector);
        return vector;
    }

    private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;

    internal static uint ComputeStableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(value.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    private static void NormalizeInPlace(float[] vector)
    {
        if (vector is null || vector.Length == 0) return;

        float sumSq = 0f;
        int vLen = Vector<float>.Count;
        int i = 0;

        if (Vector.IsHardwareAccelerated && vector.Length >= vLen)
        {
            var vSumSq = Vector<float>.Zero;
            for (; i <= vector.Length - vLen; i += vLen)
            {
                var v = new Vector<float>(vector, i);
                vSumSq += v * v;
            }
            sumSq = Vector.Dot(vSumSq, Vector<float>.One);
        }

        for (; i < vector.Length; i++)
        {
            sumSq += vector[i] * vector[i];
        }

        if (sumSq < 1e-9f) return;

        float norm = MathF.Sqrt(sumSq);
        float invNorm = 1.0f / norm;

        i = 0;
        if (Vector.IsHardwareAccelerated && vector.Length >= vLen)
        {
            var vInvNorm = new Vector<float>(invNorm);
            for (; i <= vector.Length - vLen; i += vLen)
            {
                var v = new Vector<float>(vector, i);
                (v * vInvNorm).CopyTo(vector, i);
            }
        }

        for (; i < vector.Length; i++)
        {
            vector[i] *= invNorm;
        }
    }

    /// <summary>
    /// 使用 SIMD 硬件指令加速计算两个向量的余弦相似度。
    /// </summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a is null || b is null || a.Length != b.Length || a.Length == 0) return 0.0;

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;

        int vLen = Vector<float>.Count;
        int i = 0;

        if (Vector.IsHardwareAccelerated && a.Length >= vLen)
        {
            var vDot = Vector<float>.Zero;
            var vNormA = Vector<float>.Zero;
            var vNormB = Vector<float>.Zero;

            for (; i <= a.Length - vLen; i += vLen)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);

                vDot += va * vb;
                vNormA += va * va;
                vNormB += vb * vb;
            }

            dot = Vector.Dot(vDot, Vector<float>.One);
            normA = Vector.Dot(vNormA, Vector<float>.One);
            normB = Vector.Dot(vNormB, Vector<float>.One);
        }

        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA <= 1e-9f || normB <= 1e-9f) return 0.0;
        double sim = dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        return Math.Clamp(sim, 0.0, 1.0);
    }
}

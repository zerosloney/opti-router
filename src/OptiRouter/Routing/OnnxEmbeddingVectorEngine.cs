using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 基于本地 ONNX 轻量级 Transformer 模型（如 bge-small-zh / all-MiniLM-L6-v2）的 Dense Embedding 语义匹配引擎。
/// 零云端依赖，通过嵌入式 ONNX Runtime 执行轻量级推理，利用 Mean Pooling 与 SIMD 浮点归一化提供深层隐式语义路由能力。
/// 当模型未配置、路径不存在或推理异常时，提供透明平滑降级（退回默认词法特征哈希/TF-IDF）。
/// </summary>
public sealed class OnnxEmbeddingVectorEngine : ISemanticVectorEngine, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly ISemanticVectorEngine _fallbackEngine;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, float[]> _phraseCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, float[]> _queryCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// 获取当前 ONNX 会话是否成功初始化并可用。
    /// </summary>
    public bool IsAvailable => _session is not null;

    /// <summary>
    /// 初始化 ONNX 向量嵌入路由引擎。
    /// </summary>
    /// <param name="modelPath">ONNX 模型文件绝对路径或相对路径。</param>
    /// <param name="executionProvider">执行提供者（如 "CPU" 或 "CUDA"）。</param>
    /// <param name="fallbackEngine">失败或路径不存在时的回退引擎（默认为 <see cref="DenseEmbeddingVectorEngine"/>）。</param>
    /// <param name="logger">日志记录器。</param>
    public OnnxEmbeddingVectorEngine(
        string? modelPath,
        string executionProvider = "CPU",
        ISemanticVectorEngine? fallbackEngine = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _fallbackEngine = fallbackEngine ?? new DenseEmbeddingVectorEngine();

        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            _logger?.LogWarning("ONNX Embedding model file not found at path '{ModelPath}'. Falling back to default dense feature hash engine.", modelPath);
            _session = null;
            return;
        }

        try
        {
            var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
            if (string.Equals(executionProvider, "CUDA", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    options.AppendExecutionProvider_CUDA();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to append CUDA execution provider for ONNX Runtime. Falling back to CPU.");
                    options.AppendExecutionProvider_CPU();
                }
            }
            else
            {
                options.AppendExecutionProvider_CPU();
            }

            _session = new InferenceSession(modelPath, options);
            _logger?.LogInformation("Successfully initialized ONNX Embedding InferenceSession from '{ModelPath}'.", modelPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize ONNX InferenceSession from '{ModelPath}'. Falling back to default engine.", modelPath);
            _session = null;
        }
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

        if (_session is null)
        {
            return _fallbackEngine.Match(queryText, routes);
        }

        try
        {
            var queryVector = GetEmbedding(queryText);
            if (queryVector is null || queryVector.Length == 0)
            {
                return _fallbackEngine.Match(queryText, routes);
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

                    double sim = DenseEmbeddingVectorEngine.CosineSimilarity(queryVector, phraseVector);
                    if (sim > maxSimilarity)
                    {
                        maxSimilarity = sim;
                        bestRoute = route;
                    }
                }
            }

            return (bestRoute, maxSimilarity);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception occurred during ONNX embedding inference. Falling back to default engine.");
            return _fallbackEngine.Match(queryText, routes);
        }
    }

    /// <summary>
    /// 对给定文本计算 ONNX 密向量表示。
    /// </summary>
    public float[] GetEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<float>();
        if (_session is null) return _fallbackEngine is DenseEmbeddingVectorEngine d ? d.GetEmbedding(text) : Array.Empty<float>();

        return _queryCache.GetOrAdd(text, t =>
        {
            try
            {
                return InferEmbedding(t);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to infer ONNX embedding for text snippet. Falling back to default feature hash.");
                return new DenseEmbeddingVectorEngine().GetEmbedding(t);
            }
        });
    }

    private float[] InferEmbedding(string text)
    {
        if (_session is null) return Array.Empty<float>();

        var tokens = TokenizeToIds(text, maxSeqLen: 128);
        long[] inputIds = tokens.InputIds;
        long[] attentionMask = tokens.AttentionMask;
        long[] tokenTypeIds = tokens.TokenTypeIds;

        int seqLen = inputIds.Length;
        var container = new List<NamedOnnxValue>();

        foreach (var inputNode in _session.InputMetadata)
        {
            string nodeName = inputNode.Key;
            var elementDataType = inputNode.Value.ElementType;

            long[] tensorData = nodeName.ToLowerInvariant() switch
            {
                var n when n.Contains("attention") => attentionMask,
                var n when n.Contains("type") => tokenTypeIds,
                _ => inputIds
            };

            if (elementDataType == typeof(int))
            {
                int[] intData = Array.ConvertAll(tensorData, i => (int)i);
                var denseTensor = new DenseTensor<int>(intData, new[] { 1, seqLen });
                container.Add(NamedOnnxValue.CreateFromTensor(nodeName, denseTensor));
            }
            else
            {
                var denseTensor = new DenseTensor<long>(tensorData, new[] { 1, seqLen });
                container.Add(NamedOnnxValue.CreateFromTensor(nodeName, denseTensor));
            }
        }

        using var results = _session.Run(container);
        using var outputTensor = results.First();

        return ExtractAndMeanPool(outputTensor, attentionMask);
    }

    private static (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) TokenizeToIds(string text, int maxSeqLen)
    {
        string normalized = text.Trim().ToLowerInvariant();
        var rawTokens = TfIdfSemanticVectorEngine.Tokenize(normalized);

        var idList = new List<long> { 101L }; // [CLS]
        foreach (var tok in rawTokens)
        {
            if (idList.Count >= maxSeqLen - 1) break;
            uint h = DenseEmbeddingVectorEngine.ComputeStableHash(tok);
            long id = (h % 29000) + 1000;
            idList.Add(id);
        }

        for (int i = 0; i < normalized.Length - 1 && idList.Count < maxSeqLen - 1; i++)
        {
            char c1 = normalized[i];
            char c2 = normalized[i + 1];
            if (c1 >= 0x4E00 && c1 <= 0x9FFF && c2 >= 0x4E00 && c2 <= 0x9FFF)
            {
                uint bh = DenseEmbeddingVectorEngine.ComputeStableHash(string.Concat(c1, c2));
                idList.Add((bh % 29000) + 1000);
            }
        }

        idList.Add(102L); // [SEP]

        int count = idList.Count;
        long[] inputIds = idList.ToArray();
        long[] attentionMask = new long[count];
        Array.Fill(attentionMask, 1L);
        long[] tokenTypeIds = new long[count];

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private static float[] ExtractAndMeanPool(DisposableNamedOnnxValue outputValue, long[] attentionMask)
    {
        var tensor = outputValue.AsTensor<float>();
        var dimensions = tensor.Dimensions;

        if (dimensions.Length == 2)
        {
            float[] vec = tensor.ToArray();
            NormalizeInPlace(vec);
            return vec;
        }

        if (dimensions.Length == 3)
        {
            int batch = dimensions[0];
            int seqLen = dimensions[1];
            int hiddenDim = dimensions[2];

            float[] pooled = new float[hiddenDim];
            float sumMask = 0f;

            for (int s = 0; s < seqLen && s < attentionMask.Length; s++)
            {
                if (attentionMask[s] == 0) continue;
                sumMask += 1.0f;

                for (int d = 0; d < hiddenDim; d++)
                {
                    pooled[d] += tensor[0, s, d];
                }
            }

            if (sumMask > 0)
            {
                for (int d = 0; d < hiddenDim; d++)
                {
                    pooled[d] /= sumMask;
                }
            }

            NormalizeInPlace(pooled);
            return pooled;
        }

        float[] raw = tensor.ToArray();
        NormalizeInPlace(raw);
        return raw;
    }

    private static void NormalizeInPlace(float[] vector)
    {
        if (vector is null || vector.Length == 0) return;
        float sumSq = 0f;
        foreach (float val in vector) sumSq += val * val;
        if (sumSq < 1e-9f) return;
        float invNorm = 1.0f / MathF.Sqrt(sumSq);
        for (int i = 0; i < vector.Length; i++) vector[i] *= invNorm;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}

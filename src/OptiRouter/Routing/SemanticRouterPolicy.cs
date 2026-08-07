using OptiRouter.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace OptiRouter.Routing;

/// <summary>
/// 向量空间语义路由策略。采用局域轻量级词袋模型（Vector Space Model）计算余弦相似度，
/// 支撑 100% 离线、高吞吐、零延迟、Native AOT 兼容的智能意图路由。
/// </summary>
public sealed class SemanticRouterPolicy : IRouterPolicy
{
    private sealed class CompiledRoutes
    {
        public Dictionary<string, int> Vocabulary { get; }
        public List<(SemanticRouteOptions Route, double[] NormalizedVector)> PhraseVectors { get; }

        public CompiledRoutes(Dictionary<string, int> vocabulary, List<(SemanticRouteOptions, double[])> phraseVectors)
        {
            Vocabulary = vocabulary;
            PhraseVectors = phraseVectors;
        }
    }

    private readonly object _compileLock = new();
    private List<SemanticRouteOptions>? _lastRoutes;
    private CompiledRoutes? _compiledCache;

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        var options = context.Options.Routing;
        if (!options.EnableSemanticRouter || options.SemanticRoutes is null || options.SemanticRoutes.Count == 0)
        {
            return previous with { Reason = $"{previous.Reason}; semantic-router: disabled" };
        }

        // 1. 获取（或热编译）语义库
        var compiled = GetOrCreateCache(options.SemanticRoutes);
        if (compiled.Vocabulary.Count == 0 || compiled.PhraseVectors.Count == 0)
        {
            return previous with { Reason = $"{previous.Reason}; semantic-router: empty-database" };
        }

        // 2. 提取待匹配的 Query 文本（最近一条用户输入文本）
        string queryText = GetQueryText(context.Request);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return previous;
        }

        // 3. 将 Query 向量化并归一化
        var queryVector = VectorizeAndNormalize(queryText, compiled.Vocabulary);
        if (queryVector is null)
        {
            return previous with { Reason = $"{previous.Reason}; semantic-router: no-match(zero-vector)" };
        }

        // 4. 计算余弦相似度（由于向量均已归一化，余弦相似度简化为点积计算）
        double maxSimilarity = 0;
        SemanticRouteOptions? matchedRoute = null;

        foreach (var (route, phraseVector) in compiled.PhraseVectors)
        {
            double sim = DotProduct(queryVector, phraseVector);
            if (sim > maxSimilarity)
            {
                maxSimilarity = sim;
                matchedRoute = route;
            }
        }

        // 5. 命中阈值后覆盖之前的候选模型，完成路由分流
        if (matchedRoute is not null && maxSimilarity >= options.SemanticSimilarityThreshold)
        {
            var candidates = FilterByTier(context.AllModels, matchedRoute.TargetTier);
            if (candidates.Count > 0)
            {
                // 按最大上下文数降序
                candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();
                string reason = $"semantic-router: matched={matchedRoute.Name}(sim={maxSimilarity:F4}, tier={matchedRoute.TargetTier}), {candidates.Count} candidates";

                return previous with
                {
                    Candidates = candidates,
                    Reason = $"{previous.Reason}; {reason}"
                };
            }
        }

        return previous with { Reason = $"{previous.Reason}; semantic-router: no-match(max_sim={maxSimilarity:F4})" };
    }

    private CompiledRoutes GetOrCreateCache(List<SemanticRouteOptions> routes)
    {
        lock (_compileLock)
        {
            // 校验路由项实例是否发生变化，实现完全线程安全的实时热更新（Reload）
            if (_compiledCache is not null && ReferenceEquals(_lastRoutes, routes))
            {
                return _compiledCache;
            }

            _compiledCache = Compile(routes);
            _lastRoutes = routes;
            return _compiledCache;
        }
    }

    private static CompiledRoutes Compile(List<SemanticRouteOptions> routes)
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int termIndex = 0;

        // Step 1: 建立全局特征词库字典
        foreach (var route in routes)
        {
            if (route.Phrases is null) continue;
            foreach (var phrase in route.Phrases)
            {
                var tokens = Tokenize(phrase);
                foreach (var token in tokens)
                {
                    if (!vocabulary.ContainsKey(token))
                    {
                        vocabulary[token] = termIndex++;
                    }
                }
            }
        }

        // Step 2: 将各短语进行特征向量化并进行 L2 范数归一化
        var phraseVectors = new List<(SemanticRouteOptions, double[])>();
        foreach (var route in routes)
        {
            if (route.Phrases is null) continue;
            foreach (var phrase in route.Phrases)
            {
                var vector = Vectorize(phrase, vocabulary);
                Normalize(vector);
                phraseVectors.Add((route, vector));
            }
        }

        return new CompiledRoutes(vocabulary, phraseVectors);
    }

    private static string GetQueryText(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return string.Empty;

        // 获取最后一条 User 发送的文本作为主要意图识别对象
        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg is not null && msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                var text = msg.GetText();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }

        return request.Messages[^1]?.GetText() ?? string.Empty;
    }

    private static double[] Vectorize(string text, Dictionary<string, int> vocabulary)
    {
        var vector = new double[vocabulary.Count];
        var tokens = Tokenize(text);
        foreach (var token in tokens)
        {
            if (vocabulary.TryGetValue(token, out int idx))
            {
                vector[idx]++;
            }
        }
        return vector;
    }

    private static double[]? VectorizeAndNormalize(string text, Dictionary<string, int> vocabulary)
    {
        var vector = Vectorize(text, vocabulary);
        return Normalize(vector) ? vector : null;
    }

    private static bool Normalize(double[] vector)
    {
        double sumSq = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sumSq += vector[i] * vector[i];
        }

        if (sumSq < 1e-9) return false;

        double length = Math.Sqrt(sumSq);
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] /= length;
        }
        return true;
    }

    private static double DotProduct(double[] a, double[] b)
    {
        double sum = 0;
        int len = a.Length;
        for (int i = 0; i < len; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static readonly Regex TokenRegex = new(@"\w+", RegexOptions.Compiled);

    private static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(text)) return list;

        var matches = TokenRegex.Matches(text);
        foreach (Match m in matches)
        {
            list.Add(m.Value.ToLowerInvariant());
        }
        return list;
    }

    private static List<ModelEndpointOptions> FilterByTier(IReadOnlyList<ModelEndpointOptions> models, ModelTier tier)
    {
        return models.Where(m => m.Enabled && m.Tier == tier).ToList();
    }
}

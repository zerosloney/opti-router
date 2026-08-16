using System.Text.RegularExpressions;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 CJK N-Gram 增强分词与 TF-IDF 向量空间模型（Vector Space Model）的 100% 离线语义匹配引擎。
/// 支撑高吞吐、零延迟、Native AOT 兼容的中英文多语言语义路由。
/// </summary>
public sealed class TfIdfSemanticVectorEngine : ISemanticVectorEngine
{
    private sealed class CompiledRoutes
    {
        public Dictionary<string, int> Vocabulary { get; }
        public double[] Idf { get; }
        public List<(SemanticRouteOptions Route, double[] NormalizedVector)> PhraseVectors { get; }

        public CompiledRoutes(Dictionary<string, int> vocabulary, double[] idf, List<(SemanticRouteOptions, double[])> phraseVectors)
        {
            Vocabulary = vocabulary;
            Idf = idf;
            PhraseVectors = phraseVectors;
        }
    }

    private static readonly Regex LatinTokenRegex = new(@"[a-zA-Z0-9]+", RegexOptions.Compiled);

    private readonly object _compileLock = new();
    private List<SemanticRouteOptions>? _lastRoutes;
    private CompiledRoutes? _compiledCache;

    /// <inheritdoc />
    public (SemanticRouteOptions? MatchedRoute, double MaxSimilarity) Match(
        string queryText,
        List<SemanticRouteOptions> routes)
    {
        if (string.IsNullOrWhiteSpace(queryText) || routes is null || routes.Count == 0)
        {
            return (null, 0);
        }

        var compiled = GetOrCreateCache(routes);
        if (compiled.Vocabulary.Count == 0 || compiled.PhraseVectors.Count == 0)
        {
            return (null, 0);
        }

        var queryVector = VectorizeAndNormalize(queryText, compiled.Vocabulary, compiled.Idf);
        if (queryVector is null)
        {
            return (null, 0);
        }

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

        return (matchedRoute, maxSimilarity);
    }

    /// <inheritdoc />
    public float[] Embed(string text) => new DenseEmbeddingVectorEngine().GetEmbedding(text);

    private CompiledRoutes GetOrCreateCache(List<SemanticRouteOptions> routes)
    {
        lock (_compileLock)
        {
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

        var allPhrases = new List<string>();
        var phraseDocFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in routes)
        {
            if (route.Phrases is null) continue;
            foreach (var phrase in route.Phrases)
            {
                allPhrases.Add(phrase);
                var tokens = Tokenize(phrase);
                var uniqueTokens = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
                foreach (var token in uniqueTokens)
                {
                    if (!vocabulary.ContainsKey(token))
                    {
                        vocabulary[token] = termIndex++;
                    }
                    phraseDocFreq[token] = phraseDocFreq.TryGetValue(token, out int c) ? c + 1 : 1;
                }
            }
        }

        double n = allPhrases.Count;
        var idf = new double[vocabulary.Count];
        foreach (var (term, idx) in vocabulary)
        {
            int df = phraseDocFreq.TryGetValue(term, out int d) ? d : 1;
            idf[idx] = Math.Log((n + 1) / df);
        }

        var phraseVectors = new List<(SemanticRouteOptions, double[])>();
        foreach (var route in routes)
        {
            if (route.Phrases is null) continue;
            foreach (var phrase in route.Phrases)
            {
                var vector = Vectorize(phrase, vocabulary, idf);
                Normalize(vector);
                phraseVectors.Add((route, vector));
            }
        }

        return new CompiledRoutes(vocabulary, idf, phraseVectors);
    }

    private static double[] Vectorize(string text, Dictionary<string, int> vocabulary, double[] idf)
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
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] *= idf[i];
        }
        return vector;
    }

    private static double[]? VectorizeAndNormalize(string text, Dictionary<string, int> vocabulary, double[] idf)
    {
        var vector = Vectorize(text, vocabulary, idf);
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

    /// <summary>
    /// 混合分词器：英文/数字提取 Word Tokens，CJK 连续段提取 Bigram（单字段段兜底 Unigram）。
    /// 解决中英文混合意图在 TF-IDF 向量空间无空格分词导致的全句匹配失败问题。
    /// <para>
    /// CJK 仅产 Bigram（Unigram+Bigram 会让 CJK 文本 token 数约为 Latin 的 2x，
    /// TF/df 权重不对称，跨语言路由库 cosine 有偏）。Bigram 已隐含 Unigram 信息。
    /// 单字段孤立 CJK 段产 Unigram 兜底，避免零 token。
    /// </para>
    /// </summary>
    public static List<string> Tokenize(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        // 1. 提取英文 / 数字单词 (小写)
        var matches = LatinTokenRegex.Matches(text);
        foreach (Match m in matches)
        {
            list.Add(m.Value.ToLowerInvariant());
        }

        // 2. CJK 连续段提取 Bigram（按 CJK 连续性分段，非 CJK 字符断段）。
        var run = new List<char>();
        foreach (char c in text)
        {
            if (IsCjkCharacter(c))
            {
                run.Add(c);
            }
            else
            {
                FlushCjkRun(run, list);
                run.Clear();
            }
        }
        FlushCjkRun(run, list);

        return list;
    }

    /// <summary>
    /// 输出一个 CJK 连续段的 token：多字段段产相邻 Bigram，单字段段产 Unigram 兜底。
    /// </summary>
    private static void FlushCjkRun(List<char> run, List<string> tokens)
    {
        if (run.Count == 0) return;
        if (run.Count == 1)
        {
            tokens.Add(run[0].ToString()); // 单字兜底，避免零 token。
            return;
        }
        for (int i = 0; i < run.Count - 1; i++)
        {
            tokens.Add(new string(new[] { run[i], run[i + 1] }));
        }
    }

    private static bool IsCjkCharacter(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) || // CJK Unified Ideographs
               (c >= 0x3400 && c <= 0x4DBF) || // CJK Extension A
               (c >= 0x3040 && c <= 0x30FF) || // Hiragana / Katakana
               (c >= 0xAC00 && c <= 0xD7AF);   // Hangul Syllables
    }
}

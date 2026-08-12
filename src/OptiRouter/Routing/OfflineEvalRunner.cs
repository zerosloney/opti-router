using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 离线评测测试用例定义。
/// </summary>
public sealed record EvalTestCase(
    string Id,
    string Question,
    string ExpectedAnswer,
    string Category = "general",
    long MaxLatencyThresholdMs = 5000);

/// <summary>
/// 单个测试用例离线回归结果。
/// </summary>
public sealed record EvalTestResult(
    EvalTestCase TestCase,
    string ActualAnswer,
    double SimilarityScore,
    bool Passed,
    long LatencyMs,
    int PromptTokens,
    int CompletionTokens,
    string? ErrorMessage);

/// <summary>
/// 批次离线评测总结报告。
/// </summary>
public sealed class BatchEvalReport
{
    public required string BatchId { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public int TotalCases { get; init; }
    public int PassedCases { get; init; }
    public double AccuracyRate => TotalCases > 0 ? (double)PassedCases / TotalCases : 0.0;
    public double AvgLatencyMs { get; init; }
    public int TotalTokens { get; init; }
    public IReadOnlyList<EvalTestResult> Results { get; init; } = Array.Empty<EvalTestResult>();
}

/// <summary>
/// Golden Dataset 离线回归评测运行器：
/// 用于更换模型端点、调优融合/级联 Prompt 或版本升级前，跑自动化回归评测，
/// 计算准确率率、耗时分布与 Token 消耗对比。
/// </summary>
public static class OfflineEvalRunner
{
    /// <summary>
    /// 执行一批离线 Golden Dataset 评测。
    /// </summary>
    public static async Task<BatchEvalReport> RunBatchEvalAsync(
        string batchId,
        IReadOnlyList<EvalTestCase> dataset,
        Func<ChatRequest, CancellationToken, Task<RawChatResponse>> modelRunner,
        double similarityThreshold = 0.6,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(modelRunner);

        var results = new List<EvalTestResult>(dataset.Count);
        int passedCount = 0;
        long totalLatency = 0;
        int totalTokens = 0;

        foreach (var testCase in dataset)
        {
            var req = new ChatRequest
            {
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", testCase.Question) }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await modelRunner(req, ct).ConfigureAwait(false);
                sw.Stop();

                string actualAnswer = ResponseConfidenceChecker.ExtractAssistantText(response);
                double similarity = CalculateSimilarity(actualAnswer, testCase.ExpectedAnswer);
                bool passed = similarity >= similarityThreshold && sw.ElapsedMilliseconds <= testCase.MaxLatencyThresholdMs;

                if (passed) passedCount++;

                int pTokens = response.Usage?.PromptTokens ?? 0;
                int cTokens = response.Usage?.CompletionTokens ?? 0;
                totalTokens += (pTokens + cTokens);
                totalLatency += sw.ElapsedMilliseconds;

                results.Add(new EvalTestResult(
                    testCase,
                    actualAnswer,
                    similarity,
                    passed,
                    sw.ElapsedMilliseconds,
                    pTokens,
                    cTokens,
                    null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                // 失败/超时也计入延迟累计：否则 AvgLatencyMs 分母含失败分子却排除，系统性低估。
                totalLatency += sw.ElapsedMilliseconds;
                results.Add(new EvalTestResult(
                    testCase,
                    string.Empty,
                    0.0,
                    false,
                    sw.ElapsedMilliseconds,
                    0,
                    0,
                    ex.Message));
            }
        }

        return new BatchEvalReport
        {
            BatchId = batchId,
            TotalCases = dataset.Count,
            PassedCases = passedCount,
            AvgLatencyMs = dataset.Count > 0 ? (double)totalLatency / dataset.Count : 0.0,
            TotalTokens = totalTokens,
            Results = results
        };
    }

    /// <summary>
    /// 计算两个文本间的词重叠 Jaccard 相似度（0.0 至 1.0）。
    /// </summary>
    public static double CalculateSimilarity(string textA, string textB)
    {
        if (string.IsNullOrWhiteSpace(textA) || string.IsNullOrWhiteSpace(textB))
            return string.Equals(textA?.Trim(), textB?.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;

        var wordsA = new HashSet<string>(Tokenize(textA), StringComparer.OrdinalIgnoreCase);
        var wordsB = new HashSet<string>(Tokenize(textB), StringComparer.OrdinalIgnoreCase);

        if (wordsA.Count == 0 && wordsB.Count == 0) return 1.0;
        if (wordsA.Count == 0 || wordsB.Count == 0) return 0.0;

        int intersection = wordsA.Count(w => wordsB.Contains(w));
        int union = wordsA.Union(wordsB, StringComparer.OrdinalIgnoreCase).Count();

        return union > 0 ? (double)intersection / union : 0.0;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        // 按原有分隔符切段，再对每段内的 CJK 连续游程做字符 bigram：
        // 中文无词边界，整段单 token 会使任意两句相似度≈0，CJK 评测全部误判失败。
        foreach (var seg in text.Split(new[] { ' ', '\t', '\r', '\n', ',', '.', '，', '。', '！', '？', ':', '：', '\'', '"', '-' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            int i = 0;
            while (i < seg.Length)
            {
                bool cjk = IsCjk(seg[i]);
                int start = i;
                while (i < seg.Length && IsCjk(seg[i]) == cjk) i++;
                int len = i - start;
                if (cjk && len >= 2)
                {
                    for (int k = 0; k < len - 1; k++)
                        tokens.Add(seg.Substring(start + k, 2));
                }
                else
                {
                    tokens.Add(seg.Substring(start, len));
                }
            }
        }
        return tokens;
    }

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||  // CJK 统一表意文字
        (ch >= 0x3040 && ch <= 0x30FF) ||  // 平假名/片假名
        (ch >= 0xAC00 && ch <= 0xD7AF);    // 韩文音节
}

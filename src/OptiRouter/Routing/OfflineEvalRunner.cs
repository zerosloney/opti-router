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
    string? ErrorMessage)
{
    public bool QualityPassed { get; init; }
    public bool LatencyPassed { get; init; }
    public string QualityMetric { get; init; } = "token-jaccard";
    public string? SelectedModel { get; init; }
    public decimal Cost { get; init; }
    public string Category { get; init; } = TestCase.Category;
}

/// <summary>评测执行器返回的响应与路由元数据；调用方只填真实可观测值。</summary>
public sealed record EvalRunOutput(
    RawChatResponse Response,
    string? SelectedModel = null,
    decimal Cost = 0m,
    string? RoutedCategory = null);

public sealed record CategoryEvalSummary(
    string Category,
    int TotalCases,
    int QualityPassedCases,
    int LatencyPassedCases,
    int PassedCases,
    double AvgLatencyMs,
    int TotalTokens,
    decimal TotalCost);

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
    public int QualityPassedCases { get; init; }
    public double QualityPassRate => TotalCases > 0 ? (double)QualityPassedCases / TotalCases : 0.0;
    public int LatencyPassedCases { get; init; }
    public double LatencyPassRate => TotalCases > 0 ? (double)LatencyPassedCases / TotalCases : 0.0;
    public double AvgLatencyMs { get; init; }
    public int TotalTokens { get; init; }
    public decimal TotalCost { get; init; }
    public IReadOnlyList<CategoryEvalSummary> Categories { get; init; } = Array.Empty<CategoryEvalSummary>();
    public IReadOnlyList<EvalTestResult> Results { get; init; } = Array.Empty<EvalTestResult>();
}

public sealed record PairedEvalCaseComparison(
    string TestCaseId,
    bool BaselinePassed,
    bool CandidatePassed,
    double QualityScoreDelta,
    long LatencyDeltaMs,
    int TokenDelta,
    decimal CostDelta);

public sealed class PairedEvalReport
{
    public required string BaselineBatchId { get; init; }
    public required string CandidateBatchId { get; init; }
    public int PairedCases { get; init; }
    public int CandidateWins { get; init; }
    public int CandidateLosses { get; init; }
    public int Ties { get; init; }
    public double PassRateDelta { get; init; }
    public double QualityPassRateDelta { get; init; }
    public double LatencyPassRateDelta { get; init; }
    public double AvgLatencyDeltaMs { get; init; }
    public int TotalTokenDelta { get; init; }
    public decimal TotalCostDelta { get; init; }
    public IReadOnlyList<PairedEvalCaseComparison> Cases { get; init; } = Array.Empty<PairedEvalCaseComparison>();
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
        => await RunBatchEvalAsync(
            batchId,
            dataset,
            async (request, token) => new EvalRunOutput(await modelRunner(request, token).ConfigureAwait(false)),
            similarityThreshold,
            ct).ConfigureAwait(false);

    /// <summary>执行评测并接收真实的模型、成本与路由类别元数据。</summary>
    public static async Task<BatchEvalReport> RunBatchEvalAsync(
        string batchId,
        IReadOnlyList<EvalTestCase> dataset,
        Func<ChatRequest, CancellationToken, Task<EvalRunOutput>> modelRunner,
        double similarityThreshold = 0.6,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(modelRunner);

        var results = new List<EvalTestResult>(dataset.Count);
        int passedCount = 0;
        int qualityPassedCount = 0;
        int latencyPassedCount = 0;
        long totalLatency = 0;
        int totalTokens = 0;
        decimal totalCost = 0m;

        foreach (var testCase in dataset)
        {
            var req = new ChatRequest
            {
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", testCase.Question) }
            };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var output = await modelRunner(req, ct).ConfigureAwait(false);
                var response = output.Response;
                sw.Stop();

                string actualAnswer = ResponseConfidenceChecker.ExtractAssistantText(response);
                double similarity = CalculateSimilarity(actualAnswer, testCase.ExpectedAnswer);
                bool qualityPassed = similarity >= similarityThreshold;
                bool latencyPassed = sw.ElapsedMilliseconds <= testCase.MaxLatencyThresholdMs;
                bool passed = qualityPassed && latencyPassed;

                if (passed) passedCount++;
                if (qualityPassed) qualityPassedCount++;
                if (latencyPassed) latencyPassedCount++;

                int pTokens = response.Usage?.PromptTokens ?? 0;
                int cTokens = response.Usage?.CompletionTokens ?? 0;
                totalTokens += (pTokens + cTokens);
                totalLatency += sw.ElapsedMilliseconds;
                totalCost += output.Cost;

                results.Add(new EvalTestResult(
                    testCase,
                    actualAnswer,
                    similarity,
                    passed,
                    sw.ElapsedMilliseconds,
                    pTokens,
                    cTokens,
                    null)
                {
                    QualityPassed = qualityPassed,
                    LatencyPassed = latencyPassed,
                    SelectedModel = output.SelectedModel ?? ExtractModelName(response.Body),
                    Cost = output.Cost,
                    Category = output.RoutedCategory ?? testCase.Category
                });
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
                    ex.Message)
                {
                    QualityPassed = false,
                    LatencyPassed = false,
                    Category = testCase.Category
                });
            }
        }

        var categories = results
            .GroupBy(result => result.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CategoryEvalSummary(
                group.Key,
                group.Count(),
                group.Count(result => result.QualityPassed),
                group.Count(result => result.LatencyPassed),
                group.Count(result => result.Passed),
                group.Average(result => (double)result.LatencyMs),
                group.Sum(result => result.PromptTokens + result.CompletionTokens),
                group.Sum(result => result.Cost)))
            .OrderBy(summary => summary.Category, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BatchEvalReport
        {
            BatchId = batchId,
            TotalCases = dataset.Count,
            PassedCases = passedCount,
            QualityPassedCases = qualityPassedCount,
            LatencyPassedCases = latencyPassedCount,
            AvgLatencyMs = dataset.Count > 0 ? (double)totalLatency / dataset.Count : 0.0,
            TotalTokens = totalTokens,
            TotalCost = totalCost,
            Categories = categories,
            Results = results
        };
    }

    /// <summary>按相同用例 ID 对 baseline 与 candidate 做成对比较。</summary>
    public static PairedEvalReport Compare(BatchEvalReport baseline, BatchEvalReport candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var candidateById = candidate.Results.ToDictionary(result => result.TestCase.Id, StringComparer.Ordinal);
        var pairedResults = baseline.Results
            .Where(result => candidateById.ContainsKey(result.TestCase.Id))
            .Select(baselineResult =>
            {
                var candidateResult = candidateById[baselineResult.TestCase.Id];
                return (Baseline: baselineResult, Candidate: candidateResult);
            })
            .ToList();
        var pairs = pairedResults
            .Select(pair => new PairedEvalCaseComparison(
                pair.Baseline.TestCase.Id,
                pair.Baseline.Passed,
                pair.Candidate.Passed,
                pair.Candidate.SimilarityScore - pair.Baseline.SimilarityScore,
                pair.Candidate.LatencyMs - pair.Baseline.LatencyMs,
                (pair.Candidate.PromptTokens + pair.Candidate.CompletionTokens)
                    - (pair.Baseline.PromptTokens + pair.Baseline.CompletionTokens),
                pair.Candidate.Cost - pair.Baseline.Cost))
            .ToList();

        static double Rate<T>(IReadOnlyList<T> items, Func<T, bool> predicate)
            => items.Count == 0 ? 0.0 : (double)items.Count(predicate) / items.Count;

        return new PairedEvalReport
        {
            BaselineBatchId = baseline.BatchId,
            CandidateBatchId = candidate.BatchId,
            PairedCases = pairs.Count,
            CandidateWins = pairs.Count(pair => !pair.BaselinePassed && pair.CandidatePassed),
            CandidateLosses = pairs.Count(pair => pair.BaselinePassed && !pair.CandidatePassed),
            Ties = pairs.Count(pair => pair.BaselinePassed == pair.CandidatePassed),
            PassRateDelta = Rate(pairedResults, pair => pair.Candidate.Passed)
                - Rate(pairedResults, pair => pair.Baseline.Passed),
            QualityPassRateDelta = Rate(pairedResults, pair => pair.Candidate.QualityPassed)
                - Rate(pairedResults, pair => pair.Baseline.QualityPassed),
            LatencyPassRateDelta = Rate(pairedResults, pair => pair.Candidate.LatencyPassed)
                - Rate(pairedResults, pair => pair.Baseline.LatencyPassed),
            AvgLatencyDeltaMs = pairs.Count == 0 ? 0.0 : pairs.Average(pair => (double)pair.LatencyDeltaMs),
            TotalTokenDelta = pairs.Sum(pair => pair.TokenDelta),
            TotalCostDelta = pairs.Sum(pair => pair.CostDelta),
            Cases = pairs
        };
    }

    private static string? ExtractModelName(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("model", out var model)
                && model.ValueKind == System.Text.Json.JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
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

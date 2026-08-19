using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class P3StageTests
{
    // PromptTemplateManager 与 HybridSpeculativeOrchestrator 已随 d68bd0f 移除，
    // 对应测试一并删除；本文件保留 OfflineEvalRunner 的回归测试。

    [Fact]
    public async Task OfflineEvalRunner_CalculatesJaccardSimilarityAndBatchReport()
    {
        string textA = "The capital of France is Paris.";
        string textB = "France's capital city is Paris.";

        double similarity = OfflineEvalRunner.CalculateSimilarity(textA, textB);
        Assert.True(similarity > 0.4);

        var dataset = new List<EvalTestCase>
        {
            new("tc-1", "2+2=", "4"),
            new("tc-2", "Capital of France?", "Paris")
        };

        var report = await OfflineEvalRunner.RunBatchEvalAsync(
            "batch-001",
            dataset,
            (req, ct) => Task.FromResult(new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"4\"}}]}", new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 })),
            similarityThreshold: 0.1);

        Assert.Equal("batch-001", report.BatchId);
        Assert.Equal(2, report.TotalCases);
        Assert.True(report.AccuracyRate >= 0.5);
    }

    [Fact]
    public async Task OfflineEvalRunner_SeparatesQualityLatencyAndCapturesRoutingMetadata()
    {
        var dataset = new List<EvalTestCase>
        {
            new("quality-only", "q1", "correct answer", "reasoning", -1),
            new("latency-only", "q2", "different", "translation", 5000)
        };

        var report = await OfflineEvalRunner.RunBatchEvalAsync(
            "candidate",
            dataset,
            (req, ct) => Task.FromResult(new EvalRunOutput(
                new RawChatResponse(
                    "{\"model\":\"fallback-model\",\"choices\":[{\"message\":{\"content\":\"correct answer\"}}]}",
                    new ChatUsage { PromptTokens = 4, CompletionTokens = 2, TotalTokens = 6 }),
                SelectedModel: "selected-model",
                Cost: 0.25m,
                RoutedCategory: "semantic-route")),
            similarityThreshold: 0.9);

        Assert.Equal(1, report.QualityPassedCases);
        Assert.Equal(1, report.LatencyPassedCases);
        Assert.Equal(0, report.PassedCases);
        Assert.Equal(0.5m, report.TotalCost);
        Assert.All(report.Results, result => Assert.Equal("selected-model", result.SelectedModel));
        Assert.All(report.Results, result => Assert.Equal("semantic-route", result.Category));
        Assert.Single(report.Categories);
        Assert.Equal(12, report.TotalTokens);
    }

    [Fact]
    public async Task OfflineEvalRunner_CancellationStopsRemainingCases()
    {
        var dataset = new List<EvalTestCase>
        {
            new("first", "q1", "a"),
            new("second", "q2", "a")
        };
        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var cts = new CancellationTokenSource();
        Func<ChatRequest, CancellationToken, Task<RawChatResponse>> modelRunner = async (request, ct) =>
        {
            Interlocked.Increment(ref calls);
            firstCallStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Cancellation test should not return a response.");
        };

        var run = OfflineEvalRunner.RunBatchEvalAsync(
            "cancelled",
            dataset,
            modelRunner,
            ct: cts.Token);

        await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void OfflineEvalRunner_Compare_ReportsPairedDeltas()
    {
        var testCase = new EvalTestCase("same-id", "q", "a");
        var baselineResult = new EvalTestResult(testCase, "bad", 0.1, false, 100, 10, 5, null)
        {
            QualityPassed = false,
            LatencyPassed = true,
            Cost = 1m
        };
        var candidateResult = new EvalTestResult(testCase, "a", 1.0, true, 80, 8, 4, null)
        {
            QualityPassed = true,
            LatencyPassed = true,
            Cost = 0.5m
        };
        var baseline = new BatchEvalReport
        {
            BatchId = "baseline",
            TotalCases = 1,
            PassedCases = 0,
            QualityPassedCases = 0,
            LatencyPassedCases = 1,
            AvgLatencyMs = 100,
            TotalTokens = 15,
            TotalCost = 1m,
            Results = new[] { baselineResult }
        };
        var candidate = new BatchEvalReport
        {
            BatchId = "candidate",
            TotalCases = 1,
            PassedCases = 1,
            QualityPassedCases = 1,
            LatencyPassedCases = 1,
            AvgLatencyMs = 80,
            TotalTokens = 12,
            TotalCost = 0.5m,
            Results = new[] { candidateResult }
        };

        var comparison = OfflineEvalRunner.Compare(baseline, candidate);

        Assert.Equal(1, comparison.PairedCases);
        Assert.Equal(1, comparison.CandidateWins);
        Assert.Equal(1.0, comparison.PassRateDelta);
        Assert.Equal(-20, comparison.AvgLatencyDeltaMs);
        Assert.Equal(-3, comparison.TotalTokenDelta);
        Assert.Equal(-0.5m, comparison.TotalCostDelta);
        Assert.Equal(0.9, comparison.Cases[0].QualityScoreDelta, precision: 8);
    }

    [Fact]
    public void OfflineEvalRunner_Compare_ExcludesUnpairedCasesFromSummaryDeltas()
    {
        var shared = new EvalTestCase("shared", "q", "a");
        var baselineOnly = new EvalTestCase("baseline-only", "q", "a");
        var candidateOnly = new EvalTestCase("candidate-only", "q", "a");
        var baselineShared = new EvalTestResult(shared, "bad", 0.0, false, 100, 10, 0, null)
        {
            QualityPassed = false,
            LatencyPassed = true,
            Cost = 1m
        };
        var candidateShared = new EvalTestResult(shared, "a", 1.0, true, 80, 8, 0, null)
        {
            QualityPassed = true,
            LatencyPassed = true,
            Cost = 0.5m
        };
        var baselineExtra = new EvalTestResult(baselineOnly, "a", 1.0, true, 1, 1, 0, null)
        {
            QualityPassed = true,
            LatencyPassed = true,
            Cost = 100m
        };
        var candidateExtra = new EvalTestResult(candidateOnly, "bad", 0.0, false, 1000, 100, 0, null)
        {
            QualityPassed = false,
            LatencyPassed = false,
            Cost = 200m
        };
        var baseline = new BatchEvalReport
        {
            BatchId = "baseline",
            TotalCases = 2,
            PassedCases = 1,
            QualityPassedCases = 1,
            LatencyPassedCases = 2,
            AvgLatencyMs = 50.5,
            TotalTokens = 11,
            TotalCost = 101m,
            Results = new[] { baselineShared, baselineExtra }
        };
        var candidate = new BatchEvalReport
        {
            BatchId = "candidate",
            TotalCases = 2,
            PassedCases = 1,
            QualityPassedCases = 1,
            LatencyPassedCases = 1,
            AvgLatencyMs = 540,
            TotalTokens = 108,
            TotalCost = 200.5m,
            Results = new[] { candidateShared, candidateExtra }
        };

        var comparison = OfflineEvalRunner.Compare(baseline, candidate);

        Assert.Equal(1, comparison.PairedCases);
        Assert.Equal(1.0, comparison.PassRateDelta);
        Assert.Equal(1.0, comparison.QualityPassRateDelta);
        Assert.Equal(0.0, comparison.LatencyPassRateDelta);
        Assert.Equal(-20, comparison.AvgLatencyDeltaMs);
        Assert.Equal(-2, comparison.TotalTokenDelta);
        Assert.Equal(-0.5m, comparison.TotalCostDelta);
    }
}

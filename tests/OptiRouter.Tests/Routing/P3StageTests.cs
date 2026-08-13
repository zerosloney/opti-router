using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class P3StageTests
{
    [Fact]
    public void PromptTemplateManager_RegistersAndRendersVariables()
    {
        var manager = new PromptTemplateManager();
        manager.Register("custom_analyst", "v2", "Analyze question: {{question}} and answers: {{answers}}.");

        var variables = new Dictionary<string, string>
        {
            ["question"] = "What is 2+2?",
            ["answers"] = "4, 4"
        };

        string rendered = manager.Render("custom_analyst", "v2", variables);
        Assert.Equal("Analyze question: What is 2+2? and answers: 4, 4.", rendered);
    }

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

    [Fact]
    public async Task HybridSpeculativeOrchestrator_GeneratesDraftAndPassesToVerifier()
    {
        var mockProvider = new TestModelClientProvider();
        string draftModelCallCount = "0";
        string verifierModelCallCount = "0";

        var draftEndpoint = new ModelEndpointOptions { Name = "local-draft-1b" };
        var verifierEndpoint = new ModelEndpointOptions { Name = "cloud-verifier-gpt4" };

        mockProvider.Clients["local-draft-1b"] = new TestModelClient(draftEndpoint, (req, ct) =>
        {
            draftModelCallCount = "1";
            return Task.FromResult(new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"Draft outline answer\"}}]}", new ChatUsage { PromptTokens = 5, CompletionTokens = 5, TotalTokens = 10 }));
        });

        mockProvider.Clients["cloud-verifier-gpt4"] = new TestModelClient(verifierEndpoint, (req, ct) =>
        {
            verifierModelCallCount = "1";
            string userMsg = req.Messages?[^1]?.GetText() ?? "";
            Assert.Contains("Draft outline answer", userMsg);
            return Task.FromResult(new RawChatResponse("{\"choices\":[{\"message\":{\"content\":\"Final verifier answer\"}}]}", new ChatUsage { PromptTokens = 20, CompletionTokens = 20, TotalTokens = 40 }));
        });

        var orchestrator = new HybridSpeculativeOrchestrator(mockProvider);
        var request = new ChatRequest { Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Explain quantum computing.") } };

        var finalResp = await orchestrator.ExecuteSpeculativeAsync(request, draftEndpoint, verifierEndpoint);

        Assert.Equal("1", draftModelCallCount);
        Assert.Equal("1", verifierModelCallCount);
        Assert.Contains("Final verifier answer", finalResp.Body);
    }

    private class TestModelClientProvider : IModelClientProvider
    {
        public Dictionary<string, IModelClient> Clients { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IModelClient GetClient(ModelEndpointOptions endpoint)
        {
            return Clients[endpoint.Name];
        }
    }

    private class TestModelClient : IModelClient
    {
        private readonly ModelEndpointOptions _endpoint;
        private readonly Func<ChatRequest, CancellationToken, Task<RawChatResponse>> _handler;

        public TestModelClient(ModelEndpointOptions endpoint, Func<ChatRequest, CancellationToken, Task<RawChatResponse>> handler)
        {
            _endpoint = endpoint;
            _handler = handler;
        }

        public ModelEndpointOptions Endpoint => _endpoint;

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => _handler(request, cancellationToken);

        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelHealthResult(true, 10));
    }
}

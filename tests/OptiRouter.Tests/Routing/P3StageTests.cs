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

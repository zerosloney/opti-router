using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class RagAwareRoutingPolicyTests
{
    private static RouterContext CreateContext(ChatRequest request, bool enableRag = true)
    {
        var options = new RouterOptions
        {
            Routing = new RoutingOptions
            {
                EnableRagAwareRouting = enableRag,
                RagHighSufficiencyThreshold = 0.70,
                RagLowSufficiencyThreshold = 0.35
            }
        };

        var models = new List<ModelEndpointOptions>
        {
            new() { Id = "strong-1", Name = "gpt-4o", Tier = ModelTier.Strong, Enabled = true },
            new() { Id = "medium-1", Name = "gpt-4o-mini", Tier = ModelTier.Medium, Enabled = true },
            new() { Id = "cheap-1", Name = "deepseek-chat", Tier = ModelTier.Cheap, Enabled = true }
        };

        return new RouterContext
        {
            Request = request,
            Options = options,
            AllModels = models
        };
    }

    [Fact]
    public void Apply_Disabled_DoesNotModifyCandidates()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "<context>Doc 1: Simple text</context>Simple query")
            }
        };

        var context = CreateContext(request, enableRag: false);
        var initialDecision = new RouterDecision
        {
            Candidates = context.AllModels.ToList(),
            Reason = "initial"
        };

        var policy = new RagAwareRoutingPolicy();
        var result = policy.Apply(context, initialDecision);

        Assert.Equal("initial", result.Reason);
        Assert.Equal(initialDecision.Candidates, result.Candidates);
    }

    [Fact]
    public void Apply_HighSufficiency_PrioritizesCheapAndMediumTiers()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", @"<context>
Doc 1: Customer support refund policy allows 30 days full refund.
Doc 2: Refund requirement specifies order ID.
</context>
What is the customer support refund policy days and requirement?")
            }
        };

        var context = CreateContext(request, enableRag: true);
        var initialDecision = new RouterDecision
        {
            Candidates = context.AllModels.ToList(), // strong, medium, cheap
            Reason = "initial"
        };

        var policy = new RagAwareRoutingPolicy();
        var result = policy.Apply(context, initialDecision);

        Assert.Equal(ModelTier.Cheap, result.Candidates[0].Tier);
        Assert.Contains(result.ReasonEvents, e => e.Policy == "rag-aware");
        Assert.Contains("prioritized Cheap/Medium", result.Reason);
    }

    [Fact]
    public void Apply_ConflictingKnowledge_PrioritizesStrongTier()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", @"<context>
Doc 1: The product release date is August 2026.
Doc 2: However reports contradict and state release date is delayed to December.
</context>
When is the true release date?")
            }
        };

        var context = CreateContext(request, enableRag: true);
        var initialDecision = new RouterDecision
        {
            // Initial ordering: cheap first
            Candidates = context.AllModels.OrderBy(m => m.Tier).ToList(),
            Reason = "initial"
        };

        var policy = new RagAwareRoutingPolicy();
        var result = policy.Apply(context, initialDecision);

        Assert.Equal(ModelTier.Strong, result.Candidates[0].Tier);
        Assert.Contains(result.ReasonEvents, e => e.Policy == "rag-aware");
        Assert.Contains("prioritized Strong", result.Reason);
    }
}

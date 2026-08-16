using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ReasoningEffortControllerTests
{
    [Fact]
    public void EstimatePromptComplexity_ClassifiesSimpleQuery()
    {
        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hello, who are you?") }
        };

        double complexity = ReasoningEffortController.EstimatePromptComplexity(req);
        Assert.True(complexity < 0.35, $"Simple query complexity {complexity} should be < 0.35");
    }

    [Fact]
    public void EstimatePromptComplexity_ClassifiesComplexMathAlgorithmQuery()
    {
        var req = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Please prove the complexity of Dijkstra algorithm with Fibonacci Heap and provide refactor code.") }
        };

        double complexity = ReasoningEffortController.EstimatePromptComplexity(req);
        Assert.True(complexity >= 0.70, $"Complex algorithm query complexity {complexity} should be >= 0.70");
    }

    [Fact]
    public void CalculateBudget_AssignsEffortAndTokens()
    {
        var controller = new ReasoningEffortController();
        var options = new RouterOptions
        {
            Routing = new RoutingOptions
            {
                EnableReasoningBudgetController = true,
                ReasoningLowMaxTokens = 512,
                ReasoningHighMaxTokens = 8192
            }
        };

        var simpleReq = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") }
        };

        var simpleBudget = controller.CalculateBudget(simpleReq, options);
        Assert.Equal("low", simpleBudget.ReasoningEffort);
        Assert.Equal(512, simpleBudget.RecommendedMaxTokens);

        var complexReq = new ChatRequest
        {
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Prove mathematical induction theorem and analyze algorithm complexity in calculus.") }
        };

        var complexBudget = controller.CalculateBudget(complexReq, options);
        Assert.Equal("high", complexBudget.ReasoningEffort);
        Assert.Equal(8192, complexBudget.RecommendedMaxTokens);
    }
}

using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class MultiAgentDagRouterTests
{
    private static List<ModelEndpointOptions> GetTestModels() => new()
    {
        new ModelEndpointOptions { Name = "claude-3-5-sonnet", Tier = ModelTier.Strong, Enabled = true },
        new ModelEndpointOptions { Name = "gpt-4o-mini", Tier = ModelTier.Medium, Enabled = true },
        new ModelEndpointOptions { Name = "deepseek-v3-fast", Tier = ModelTier.Cheap, Enabled = true }
    };

    [Fact]
    public void IsEligibleForDagDecomposition_DetectsComplexWorkflow()
    {
        var complexReq = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "Please plan and design a distributed consensus architecture, implement the raft algorithm code in C#, and review all edge cases and unit tests.")
            }
        };

        bool eligible = MultiAgentDagRouter.IsEligibleForDagDecomposition(complexReq);
        Assert.True(eligible, "Complex multi-stage request should be eligible for DAG decomposition");
    }

    [Fact]
    public void IsEligibleForDagDecomposition_RejectsSimpleQuery()
    {
        var simpleReq = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "What is the square root of 144?")
            }
        };

        bool eligible = MultiAgentDagRouter.IsEligibleForDagDecomposition(simpleReq);
        Assert.False(eligible, "Simple single-turn request should not be decomposed into DAG");
    }

    [Fact]
    public void BuildExecutionPlan_CreatesStructuredDagWithHeterogeneousTiers()
    {
        var router = new MultiAgentDagRouter();
        var models = GetTestModels();

        var complexReq = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "Step by step plan the system architecture, write the core engine code, and review and test edge cases thoroughly.")
            }
        };

        var plan = router.BuildExecutionPlan(complexReq, models);

        Assert.True(plan.IsMultiAgentEligible);
        Assert.Equal(3, plan.Nodes.Count);
        Assert.Equal(3, plan.ExecutionStages.Count);

        var planNode = plan.Nodes[0];
        Assert.Equal(DagTaskNodeType.Planning, planNode.NodeType);
        Assert.Equal(ModelTier.Strong, planNode.RequiredTier);
        Assert.Equal("claude-3-5-sonnet", planNode.AssignedModel?.Name);

        var codeGenNode = plan.Nodes[1];
        Assert.Equal(DagTaskNodeType.CodeGeneration, codeGenNode.NodeType);
        Assert.Equal(ModelTier.Medium, codeGenNode.RequiredTier);
        Assert.Equal("gpt-4o-mini", codeGenNode.AssignedModel?.Name);

        var reviewNode = plan.Nodes[2];
        Assert.Equal(DagTaskNodeType.Reflection, reviewNode.NodeType);
        Assert.Equal(ModelTier.Cheap, reviewNode.RequiredTier);
        Assert.Equal("deepseek-v3-fast", reviewNode.AssignedModel?.Name);
    }
}

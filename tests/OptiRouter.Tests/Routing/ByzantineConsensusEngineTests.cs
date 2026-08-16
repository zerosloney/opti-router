using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class ByzantineConsensusEngineTests
{
    [Fact]
    public void EvaluateConsensus_IdentifiesOutlierAndPicksConsensusWinner()
    {
        var engine = new ByzantineConsensusEngine();

        // 3 candidates: modelA and modelB produce agreeing financial audits; modelC produces hallucinated nonsense
        var candidates = new List<ModelResponseCandidate>
        {
            new("gpt-4o", "Q3 Revenue increased by 15% YoY with EBITDA margin reaching 28.5%."),
            new("claude-3-5-sonnet", "Q3 Revenue grew 15% YoY with EBITDA margin of 28.5%."),
            new("rogue-model", "The quick brown fox jumps over the lazy dog in Paris.")
        };

        var result = engine.EvaluateConsensus(candidates, outlierThreshold: 0.50);

        Assert.True(result.ConsensusAchieved);
        Assert.True(result.WinningModelName is "gpt-4o" or "claude-3-5-sonnet");
        Assert.Contains("rogue-model", result.OutlierModels);
        Assert.DoesNotContain("gpt-4o", result.OutlierModels);
        Assert.DoesNotContain("claude-3-5-sonnet", result.OutlierModels);
    }

    [Fact]
    public void EvaluateConsensus_SingleCandidate_ReturnsAchieved()
    {
        var engine = new ByzantineConsensusEngine();
        var candidates = new List<ModelResponseCandidate>
        {
            new("deepseek-v3", "Standard answer.")
        };

        var result = engine.EvaluateConsensus(candidates);

        Assert.True(result.ConsensusAchieved);
        Assert.Equal("deepseek-v3", result.WinningModelName);
        Assert.Empty(result.OutlierModels);
    }

    [Fact]
    public void EvaluateConsensus_WithDenseVectorEngine_CorrectlyIdentifiesOutlier()
    {
        var vectorEngine = new DenseEmbeddingVectorEngine();
        var engine = new ByzantineConsensusEngine(vectorEngine);

        var candidates = new List<ModelResponseCandidate>
        {
            new("modelA", "Deep learning transformer models require self-attention mechanism and multi-head projection."),
            new("modelB", "Deep learning transformer architecture utilizes self-attention and multi-head feedforward projection."),
            new("modelC", "Cooking pasta with tomato sauce and fresh basil on low temperature.")
        };

        var result = engine.EvaluateConsensus(candidates, outlierThreshold: 0.40);

        Assert.True(result.ConsensusAchieved);
        Assert.Contains("modelC", result.OutlierModels);
        Assert.True(result.WinningModelName is "modelA" or "modelB");
    }
}

using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class RagContextDensityAnalyzerTests
{
    private readonly RagContextDensityAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_NoRagContext_ReturnsFalse()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "What is the capital of France?")
            }
        };

        var result = _analyzer.Analyze(request);

        Assert.False(result.HasRagContext);
        Assert.Equal(RagSufficiency.None, result.Sufficiency);
    }

    [Fact]
    public void Analyze_HighSufficiency_DetectsHighAndRecommendsCheapTier()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", @"<context>
Doc 1: The capital of France is Paris. It has a population of over 2 million residents in the city proper.
Doc 2: Paris has been the cultural and economic center of France for centuries.
</context>
What is the capital of France and what is its population?")
            }
        };

        var result = _analyzer.Analyze(request);

        Assert.True(result.HasRagContext);
        Assert.True(result.QueryCoverageRatio >= 0.70);
        Assert.Equal(RagSufficiency.High, result.Sufficiency);
        Assert.Equal(ModelTier.Cheap, result.RecommendedTier);
    }

    [Fact]
    public void Analyze_ConflictingKnowledge_DetectsConflictAndRecommendsStrongTier()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", @"[Reference Material]
[1] The company profit margin grew by 12% in 2025.
[2] However contradictory audit reports disagree and report a margin decline of 4%.
What is the true profit margin growth?")
            }
        };

        var result = _analyzer.Analyze(request);

        Assert.True(result.HasRagContext);
        Assert.Equal(RagSufficiency.Conflict, result.Sufficiency);
        Assert.Equal(ModelTier.Strong, result.RecommendedTier);
    }

    [Fact]
    public void Analyze_LowCoverage_RecommendsStrongTier()
    {
        var request = new ChatRequest
        {
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", @"参考资料:
关于量子计算机超导量子比特低温制冷机制的说明文档。
如何使用 Python 爬虫下载网易云音乐的歌词？")
            }
        };

        var result = _analyzer.Analyze(request);

        Assert.True(result.HasRagContext);
        Assert.True(result.QueryCoverageRatio <= 0.35);
        Assert.Equal(RagSufficiency.Low, result.Sufficiency);
        Assert.Equal(ModelTier.Strong, result.RecommendedTier);
    }
}

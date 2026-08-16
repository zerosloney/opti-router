using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class OnnxEmbeddingVectorEngineTests
{
    [Fact]
    public void WhenModelPathNullOrNotExists_GracefullyFallsBackToDefaultEngine()
    {
        using var engine = new OnnxEmbeddingVectorEngine(modelPath: null, executionProvider: "CPU");

        Assert.False(engine.IsAvailable);

        var routes = new List<SemanticRouteOptions>
        {
            new() { Name = "code-route", TargetTier = ModelTier.Strong, Phrases = new List<string> { "写代码", "快排" } }
        };

        var (matchedRoute, similarity) = engine.Match("帮我写个快速排序算法", routes);

        Assert.NotNull(matchedRoute);
        Assert.Equal("code-route", matchedRoute.Name);
        Assert.True(similarity > 0.0);
    }

    [Fact]
    public void GetEmbedding_WhenNoModel_ReturnsFallbackNormalizedEmbedding()
    {
        using var engine = new OnnxEmbeddingVectorEngine(modelPath: "non_existent_file.onnx");

        float[] embedding = engine.GetEmbedding("测试语义向量");

        Assert.NotNull(embedding);
        Assert.True(embedding.Length > 0);

        // Verify L2 norm = 1.0
        float norm = 0f;
        foreach (var v in embedding) norm += v * v;
        Assert.InRange(MathF.Sqrt(norm), 0.99f, 1.01f);
    }

    [Fact]
    public void HybridSemanticVectorEngine_WithOnnxEngine_ReturnsMatch()
    {
        using var onnxEngine = new OnnxEmbeddingVectorEngine(modelPath: null);
        var hybridEngine = new HybridSemanticVectorEngine(
            sparseEngine: new TfIdfSemanticVectorEngine(),
            denseEngine: onnxEngine,
            highConfidenceThreshold: 0.85);

        var routes = new List<SemanticRouteOptions>
        {
            new() { Name = "math-route", TargetTier = ModelTier.Strong, Phrases = new List<string> { "微积分", "导数", "求导" } }
        };

        var (matchedRoute, similarity) = hybridEngine.Match("请计算这个函数的导数", routes);

        Assert.NotNull(matchedRoute);
        Assert.Equal("math-route", matchedRoute.Name);
        Assert.True(similarity > 0.0);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesSafely()
    {
        var engine = new OnnxEmbeddingVectorEngine(modelPath: null);
        engine.Dispose();
        engine.Dispose();
    }
}

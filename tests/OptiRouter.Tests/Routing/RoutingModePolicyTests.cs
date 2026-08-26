using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

public class RoutingModePolicyTests
{
    private static List<ModelEndpointOptions> Models() => new()
    {
        new() { Name = "strong-a", Tier = ModelTier.Strong, Enabled = true, MaxContextTokens = 128000 },
        new() { Name = "medium-a", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 64000 },
        new() { Name = "medium-b", Tier = ModelTier.Medium, Enabled = true, MaxContextTokens = 32000 },
        new() { Name = "cheap-a", Tier = ModelTier.Cheap, Enabled = true, MaxContextTokens = 32000 },
    };

    private static RouterContext Context(string model) => new()
    {
        Request = new ChatRequest
        {
            Model = model,
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hello") }
        },
        AllModels = Models(),
        Options = new RouterOptions(),
        FailedModels = new HashSet<string>()
    };

    private static RouterDecision Previous() => new()
    {
        Candidates = Models(),
        Reason = "init"
    };

    [Fact]
    public void Group_IsFilter()
    {
        Assert.Equal(PolicyGroup.Filter, new RoutingModePolicy().Group);
    }

    [Theory]
    [InlineData("auto:cost", RoutingMode.Cost, ModelTier.Cheap)]
    [InlineData("auto:balanced", RoutingMode.Balanced, ModelTier.Medium)]
    [InlineData("auto:intel", RoutingMode.Intelligence, ModelTier.Strong)]
    [InlineData("auto:intelligence", RoutingMode.Intelligence, ModelTier.Strong)]
    [InlineData("AUTO:COST", RoutingMode.Cost, ModelTier.Cheap)]
    [InlineData("Auto:Intel", RoutingMode.Intelligence, ModelTier.Strong)]
    public void Preset_SetsModeAndFiltersToTargetTier(string model, RoutingMode expectedMode, ModelTier expectedTier)
    {
        var result = new RoutingModePolicy().Apply(Context(model), Previous());

        Assert.Equal(expectedMode, result.RoutingMode);
        Assert.Equal(expectedTier, result.TargetTier);
        Assert.All(result.Candidates, m => Assert.Equal(expectedTier, m.Tier));
        Assert.Contains($"preset={expectedMode}", result.Reason);
    }

    [Fact]
    public void BalancedTargetTier_IncludesAllMediumEndpoints()
    {
        var result = new RoutingModePolicy().Apply(Context("auto:balanced"), Previous());

        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void TargetTierEmpty_KeepsAllCandidates()
    {
        // 无 Cheap 模型时 auto:cost 不清空候选：保留全量兜底，模式标记仍生效。
        var models = Models().Where(m => m.Tier != ModelTier.Cheap).ToList();
        var context = new RouterContext
        {
            Request = new ChatRequest
            {
                Model = "auto:cost",
                Messages = new List<ChatMessage> { ChatMessage.FromText("user", "hi") }
            },
            AllModels = models,
            Options = new RouterOptions(),
            FailedModels = new HashSet<string>()
        };
        var previous = new RouterDecision { Candidates = models, Reason = "init" };

        var result = new RoutingModePolicy().Apply(context, previous);

        Assert.Equal(RoutingMode.Cost, result.RoutingMode);
        Assert.Equal(ModelTier.Cheap, result.TargetTier);
        Assert.Equal(models.Count, result.Candidates.Count);
        Assert.Contains("keeping all", result.Reason);
    }

    [Theory]
    [InlineData("auto:unknown")]
    [InlineData("auto:cheap-mode")]
    [InlineData("auto:")]
    public void UnknownPreset_PassthroughWithoutMode(string model)
    {
        // 未知预设按默认 balanced 处理：不设模式标记，候选不动。
        var result = new RoutingModePolicy().Apply(Context(model), Previous());

        Assert.Null(result.RoutingMode);
        Assert.Null(result.TargetTier);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Contains("no mode preset", result.Reason);
        Assert.Null(RoutingModePolicy.TryResolveMode(model));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("gpt-4o")]
    [InlineData("deepseek/deepseek-chat #2")]
    public void NonPresetModel_Passthrough(string model)
    {
        var result = new RoutingModePolicy().Apply(Context(model), Previous());

        Assert.Null(result.RoutingMode);
        Assert.Equal(4, result.Candidates.Count);
        Assert.Contains("no mode preset", result.Reason);
    }

    [Theory]
    [InlineData("auto:cost", RoutingMode.Cost)]
    [InlineData("auto:intel", RoutingMode.Intelligence)]
    [InlineData("auto:intelligence", RoutingMode.Intelligence)]
    [InlineData("auto:balanced", RoutingMode.Balanced)]
    [InlineData("AUTO:COST", RoutingMode.Cost)]
    public void TryResolveMode_Presets(string model, RoutingMode expected)
    {
        Assert.Equal(expected, RoutingModePolicy.TryResolveMode(model));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("auto:unknown")]
    [InlineData("strong-a")]
    public void TryResolveMode_NonPresets_ReturnNull(string? model)
    {
        Assert.Null(RoutingModePolicy.TryResolveMode(model));
    }

    [Fact]
    public void AdjustCompression_Cost_MoreAggressive()
    {
        var compression = new OptiRouter.Compression.PromptCompressionOptions
        {
            MinTokensToTrigger = 300,
            TargetReductionRatio = 0.30,
            PreserveRecentTurns = 3,
            PreserveCodeAndJson = false
        };

        var adjusted = RoutingModePolicy.AdjustCompression(compression, RoutingMode.Cost);

        Assert.Equal(150, adjusted.MinTokensToTrigger);
        Assert.Equal(0.60, adjusted.TargetReductionRatio, precision: 2);
        // 内容保护规则逐字段原样拷贝。
        Assert.Equal(3, adjusted.PreserveRecentTurns);
        Assert.False(adjusted.PreserveCodeAndJson);
    }

    [Fact]
    public void AdjustCompression_Intelligence_MoreConservative()
    {
        var compression = new OptiRouter.Compression.PromptCompressionOptions
        {
            MinTokensToTrigger = 300,
            TargetReductionRatio = 0.30
        };

        var adjusted = RoutingModePolicy.AdjustCompression(compression, RoutingMode.Intelligence);

        Assert.Equal(600, adjusted.MinTokensToTrigger);
        Assert.Equal(0.15, adjusted.TargetReductionRatio, precision: 2);
    }

    [Fact]
    public void AdjustCompression_BalancedOrNull_KeepsOriginal()
    {
        var compression = new OptiRouter.Compression.PromptCompressionOptions();

        Assert.Same(compression, RoutingModePolicy.AdjustCompression(compression, null));
        Assert.Same(compression, RoutingModePolicy.AdjustCompression(compression, RoutingMode.Balanced));
    }
}

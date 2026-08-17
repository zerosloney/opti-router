using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// 路由预设应用器测试：preset 填充未显式配置项、显式配置优先、类型转换正确。
/// </summary>
public class RoutingPresetTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?>? values = null)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

    private static void Apply(RoutingOptions routing, IConfiguration config)
        => RoutingPreset.Apply(routing, config, NullLogger.Instance);

    [Fact]
    public void Apply_CostFirst_FillsUnconfiguredKeysWithEnumAndDouble()
    {
        var routing = new RoutingOptions { Preset = "cost-first" };

        Apply(routing, BuildConfig());

        Assert.True(routing.EnableThompsonSampling);
        Assert.True(routing.EnableLatencyAware);
        Assert.True(routing.EnableResponseCache);
        Assert.Equal(0.05, routing.ExplorationEpsilon);
        Assert.Equal(ModelTier.Cheap, routing.DefaultTier);
    }

    [Fact]
    public void Apply_ExplicitConfig_Wins_OverPreset()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OptiRouter:Routing:EnableThompsonSampling"] = "false"
        });
        // 模拟真实管线：Bind 先生效，PostConfigure(Apply) 后运行
        var routing = new RoutingOptions { Preset = "balanced" };
        config.GetSection("OptiRouter:Routing").Bind(routing);

        Apply(routing, config);

        // 显式 false 优先于 preset 的 true
        Assert.False(routing.EnableThompsonSampling);
        // 未显式配置的项仍被填充
        Assert.True(routing.EnableCascadeUpgrade);
        Assert.Equal(0.1, routing.CascadeUpgradeSampleRate);
    }

    [Fact]
    public void Apply_QualityFirst_PartialExplicit_MixedApplication()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["OptiRouter:Routing:DefaultTier"] = "Cheap"
        });
        var routing = new RoutingOptions { Preset = "quality-first" };
        config.GetSection("OptiRouter:Routing").Bind(routing);

        Apply(routing, config);

        // 显式 Cheap 保持，不被 preset 覆盖为 Strong
        Assert.Equal(ModelTier.Cheap, routing.DefaultTier);
        // 其余项正常填充
        Assert.True(routing.EnableFusionRouter);
        Assert.True(routing.EnableByzantineConsensus);
        Assert.True(routing.EnableCascadeUpgrade);
        Assert.Equal(0.3, routing.CascadeUpgradeSampleRate);
    }

    [Fact]
    public void Apply_UnknownPreset_NoChanges()
    {
        var routing = new RoutingOptions { Preset = "does-not-exist" };

        Apply(routing, BuildConfig());

        Assert.False(routing.EnableThompsonSampling);
        Assert.Equal(ModelTier.Medium, routing.DefaultTier);
        Assert.False(routing.EnableResponseCache);
    }

    [Fact]
    public void Apply_NullPreset_NoChanges()
    {
        var routing = new RoutingOptions { Preset = null };

        Apply(routing, BuildConfig());

        Assert.False(routing.EnableThompsonSampling);
        Assert.Equal(ModelTier.Medium, routing.DefaultTier);
    }

    [Fact]
    public void Apply_PresetName_CaseInsensitive()
    {
        var routing = new RoutingOptions { Preset = "  Balanced  " };

        Apply(routing, BuildConfig());

        Assert.True(routing.EnableThompsonSampling);
        Assert.True(routing.EnableCascadeUpgrade);
    }
}

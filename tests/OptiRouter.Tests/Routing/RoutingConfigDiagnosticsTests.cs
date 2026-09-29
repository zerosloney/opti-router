using OptiRouter.Configuration;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// 路由配置组合诊断规则测试：每条规则一个「触发」与一个「干净」断言，
/// 规则行为依据与 RoutingConfigDiagnostics 注释中的代码证据位置一一对应。
/// </summary>
public sealed class RoutingConfigDiagnosticsTests
{
    private static RouterOptions Options(Action<RoutingOptions>? configure = null)
    {
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions
        {
            Name = "cloud-a",
            BaseUrl = "https://api.example.com/v1",
            Tier = ModelTier.Strong,
            Enabled = true
        });
        options.Models.Add(new ModelEndpointOptions
        {
            Name = "local-a",
            BaseUrl = "http://localhost:11434/v1",
            Tier = ModelTier.Medium,
            Enabled = true,
            IsLocalOrPrivate = true
        });
        configure?.Invoke(options.Routing);
        return options;
    }

    private static string? SingleCode(RouterOptions options, string code)
    {
        var hit = RoutingConfigDiagnostics.Analyze(options)
            .FirstOrDefault(d => d.Code == code);
        return hit is null ? null : hit.Severity;
    }

    [Fact]
    public void ByzantineConsensus_WithoutFusionRouter_Warns()
    {
        var triggered = Options(r => r.EnableByzantineConsensus = true);
        Assert.Equal("warning", SingleCode(triggered, "byzantine-without-fusion"));

        var clean = Options(r =>
        {
            r.EnableByzantineConsensus = true;
            r.EnableFusionRouter = true;
        });
        Assert.Null(SingleCode(clean, "byzantine-without-fusion"));
    }

    [Fact]
    public void FusionAndRace_BothEnabled_Infos()
    {
        var triggered = Options(r =>
        {
            r.EnableFusionRouter = true;
            r.EnableFusionMode = true;
        });
        Assert.Equal("info", SingleCode(triggered, "fusion-and-race"));

        var clean = Options(r => r.EnableFusionRouter = true);
        Assert.Null(SingleCode(clean, "fusion-and-race"));
    }

    [Fact]
    public void QualityJudge_WithoutModel_Warns()
    {
        var triggered = Options(r => r.EnableQualityJudge = true);
        Assert.Equal("warning", SingleCode(triggered, "quality-judge-no-model"));

        var clean = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "cloud-a";
        });
        Assert.Null(SingleCode(clean, "quality-judge-no-model"));
    }

    [Fact]
    public void QualityJudge_UnresolvableModel_Warns_AndResolvableDoesNot()
    {
        var triggered = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "no-such-model";
        });
        Assert.Equal("warning", SingleCode(triggered, "quality-judge-model-unresolved"));

        var clean = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "cloud-a";
        });
        Assert.Null(SingleCode(clean, "quality-judge-model-unresolved"));
    }

    [Fact]
    public void QualityJudge_SovereigntyFilteredJudge_Warns()
    {
        var triggered = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "cloud-a";
            r.EnableDataSovereignty = true;
        });
        Assert.Equal("warning", SingleCode(triggered, "quality-judge-sovereignty"));

        var clean = Options(r =>
        {
            r.EnableQualityJudge = true;
            r.QualityJudgeModel = "local-a";
            r.EnableDataSovereignty = true;
        });
        Assert.Null(SingleCode(clean, "quality-judge-sovereignty"));
    }

    [Fact]
    public void Hedge_WithoutFailover_Warns()
    {
        // EnableFailover 默认 true，触发场景需显式关闭。
        var triggered = Options(r =>
        {
            r.StreamHedgeDelayMs = 500;
            r.EnableFailover = false;
        });
        Assert.Equal("warning", SingleCode(triggered, "hedge-without-failover"));

        var clean = Options(r => r.StreamHedgeDelayMs = 500);
        Assert.Null(SingleCode(clean, "hedge-without-failover"));
    }

    [Fact]
    public void Sovereignty_WithParallelModes_AndFewPrivateEndpoints_Warns()
    {
        // 本地端点 1 个（< 2）：融合/竞速凑不齐候选。
        var triggered = Options(r =>
        {
            r.EnableDataSovereignty = true;
            r.EnableFusionRouter = true;
        });
        Assert.Equal("warning", SingleCode(triggered, "sovereignty-starves-parallel"));

        // 干净：再补一个本地端点凑齐 2 个。
        var clean = Options(r =>
        {
            r.EnableDataSovereignty = true;
            r.EnableFusionRouter = true;
        });
        clean.Models.Add(new ModelEndpointOptions
        {
            Name = "local-b",
            BaseUrl = "http://localhost:11435/v1",
            Tier = ModelTier.Cheap,
            Enabled = true,
            IsLocalOrPrivate = true
        });
        Assert.Null(SingleCode(clean, "sovereignty-starves-parallel"));
    }

    [Fact]
    public void CascadeVerifier_UnresolvableModel_Warns()
    {
        var triggered = Options(r =>
        {
            r.EnableCascadeUpgrade = true;
            r.CascadeUpgradeVerifierModel = "no-such-model";
        });
        Assert.Equal("warning", SingleCode(triggered, "cascade-verifier-unresolved"));

        var clean = Options(r =>
        {
            r.EnableCascadeUpgrade = true;
            r.CascadeUpgradeVerifierModel = "cloud-a";
        });
        Assert.Null(SingleCode(clean, "cascade-verifier-unresolved"));
    }

    [Fact]
    public void RegenerateFeedback_AlwaysInfos()
    {
        var triggered = Options(r => r.EnableRegenerateFeedback = true);
        Assert.Equal("info", SingleCode(triggered, "regenerate-fixed-prompt"));

        var clean = Options();
        Assert.Null(SingleCode(clean, "regenerate-fixed-prompt"));
    }

    [Fact]
    public void Epsilon_WithoutLatencyAware_Infos()
    {
        var triggered = Options(r => r.ExplorationEpsilon = 0.05);
        Assert.Equal("info", SingleCode(triggered, "epsilon-without-latency-aware"));

        var clean = Options(r =>
        {
            r.ExplorationEpsilon = 0.05;
            r.EnableLatencyAware = true;
        });
        Assert.Null(SingleCode(clean, "epsilon-without-latency-aware"));
    }

    [Fact]
    public void DefaultOptions_ProduceNoDiagnostics()
    {
        Assert.Empty(RoutingConfigDiagnostics.Analyze(Options()));
    }
}

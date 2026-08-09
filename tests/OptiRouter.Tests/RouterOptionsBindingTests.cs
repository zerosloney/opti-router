using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;

namespace OptiRouter.Tests;

public class RouterOptionsBindingTests
{
    [Fact]
    public void Bind_ShouldMapAllFieldsFromConfiguration()
    {
        // Arrange
        var settings = new Dictionary<string, string?>
        {
            ["OptiRouter:Models:0:Name"] = "gpt-4o",
            ["OptiRouter:Models:0:BaseUrl"] = "https://api.openai.com/v1",
            ["OptiRouter:Models:0:ApiKey"] = "sk-test",
            ["OptiRouter:Models:0:Tier"] = "Strong",
            ["OptiRouter:Models:0:MaxContextTokens"] = "128000",
            ["OptiRouter:Models:0:InputPricePerMillion"] = "2.5",
            ["OptiRouter:Models:0:OutputPricePerMillion"] = "10.0",
            ["OptiRouter:Models:0:TimeoutSeconds"] = "120",
            ["OptiRouter:Models:0:MaxRetries"] = "0",
            ["OptiRouter:Models:0:Enabled"] = "true",
            ["OptiRouter:Models:0:Tags:0"] = "vision",
            ["OptiRouter:Models:0:Tags:1"] = "tool-use",
            ["OptiRouter:Budget:DailyBudgetUsd"] = "10.0",
            ["OptiRouter:Budget:SessionBudgetUsd"] = "5.0",
            ["OptiRouter:Budget:EnforceOnExhausted"] = "Degrade",
            ["OptiRouter:Routing:EnableRuleClassifier"] = "true",
            ["OptiRouter:Routing:EnableTokenEstimator"] = "true",
            ["OptiRouter:Routing:EnableBudgetGuard"] = "true",
            ["OptiRouter:Routing:EnableFailover"] = "true",
            ["OptiRouter:Routing:LongInputThresholdTokens"] = "32000",
            ["OptiRouter:Routing:DefaultTier"] = "Medium",
            ["OptiRouter:Routing:FailoverFailureThreshold"] = "3",
            ["OptiRouter:Routing:FailoverCooldownSeconds"] = "60",
            ["OptiRouter:Routing:FailoverHalfOpenMaxProbes"] = "2",
            ["OptiRouter:Routing:TokenEstimation"] = "Tiktoken",
            ["OptiRouter:Routing:TiktokenEncoding"] = "o200k_base",
            ["OptiRouter:Routing:EnableLatencyAware"] = "true",
            ["OptiRouter:Routing:LatencyMinSamples"] = "15",
            ["OptiRouter:Routing:LatencyStatsWindowMinutes"] = "30",
            ["OptiRouter:Routing:EnableCapabilityFilter"] = "true",
            ["OptiRouter:Routing:EnableFusionMode"] = "true",
            ["OptiRouter:Routing:FusionMaxParallel"] = "3",
            ["OptiRouter:Routing:EnableFusionRouter"] = "true",
            ["OptiRouter:Routing:FusionRouterPanelSize"] = "4",
            ["OptiRouter:Routing:FusionRouterAnalystModel"] = "analyst-model",
            ["OptiRouter:Routing:FusionRouterAnalystPrompt"] = "json only",
            ["OptiRouter:Routing:FusionRouterOuterModel"] = "outer-model",
            ["OptiRouter:Routing:FusionRouterMaxOutputTokens"] = "12000",
            ["OptiRouter:Routing:FusionRouterTemperature"] = "0.4"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        // Act
        var options = Options.Create(new RouterOptions());
        configuration.GetSection("OptiRouter").Bind(options.Value);

        // Assert
        var model = Assert.Single(options.Value.Models);
        Assert.Equal("gpt-4o", model.Name);
        Assert.Equal("https://api.openai.com/v1", model.BaseUrl);
        Assert.Equal("sk-test", model.ApiKey);
        Assert.Equal(ModelTier.Strong, model.Tier);
        Assert.Equal(128000, model.MaxContextTokens);
        Assert.Equal(2.5m, model.InputPricePerMillion);
        Assert.Equal(10.0m, model.OutputPricePerMillion);
        Assert.Equal(120, model.TimeoutSeconds);
        Assert.Equal(0, model.MaxRetries);
        Assert.True(model.Enabled);
        Assert.Equal(["vision", "tool-use"], model.Tags);

        Assert.Equal(10.0m, options.Value.Budget.DailyBudgetUsd);
        Assert.Equal(5.0m, options.Value.Budget.SessionBudgetUsd);
        Assert.Equal(BudgetExhaustionMode.Degrade, options.Value.Budget.EnforceOnExhausted);

        Assert.True(options.Value.Routing.EnableRuleClassifier);
        Assert.True(options.Value.Routing.EnableTokenEstimator);
        Assert.True(options.Value.Routing.EnableBudgetGuard);
        Assert.True(options.Value.Routing.EnableFailover);
        Assert.Equal(32000, options.Value.Routing.LongInputThresholdTokens);
        Assert.Equal(ModelTier.Medium, options.Value.Routing.DefaultTier);
        Assert.Equal(3, options.Value.Routing.FailoverFailureThreshold);
        Assert.Equal(60, options.Value.Routing.FailoverCooldownSeconds);
        Assert.Equal(2, options.Value.Routing.FailoverHalfOpenMaxProbes);
        Assert.Equal(TokenEstimationMode.Tiktoken, options.Value.Routing.TokenEstimation);
        Assert.Equal("o200k_base", options.Value.Routing.TiktokenEncoding);

        // 第一批新增配置项绑定。
        Assert.True(options.Value.Routing.EnableLatencyAware);
        Assert.Equal(15, options.Value.Routing.LatencyMinSamples);
        Assert.Equal(30, options.Value.Routing.LatencyStatsWindowMinutes);
        Assert.True(options.Value.Routing.EnableCapabilityFilter);
        Assert.True(options.Value.Routing.EnableFusionMode);
        Assert.Equal(3, options.Value.Routing.FusionMaxParallel);
        Assert.True(options.Value.Routing.EnableFusionRouter);
        Assert.Equal(4, options.Value.Routing.FusionRouterPanelSize);
        Assert.Equal("analyst-model", options.Value.Routing.FusionRouterAnalystModel);
        Assert.Equal("json only", options.Value.Routing.FusionRouterAnalystPrompt);
        Assert.Equal("outer-model", options.Value.Routing.FusionRouterOuterModel);
        Assert.Equal(12000, options.Value.Routing.FusionRouterMaxOutputTokens);
        Assert.Equal(0.4, options.Value.Routing.FusionRouterTemperature);
    }
}

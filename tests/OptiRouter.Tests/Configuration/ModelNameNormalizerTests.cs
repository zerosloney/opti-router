using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

public class ModelNameNormalizerTests
{
    [Fact]
    public void EmptyNameWithId_GeneratesProviderSlashId_FromBaseUrl()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("deepseek/deepseek-chat", models[0].Name);
    }

    [Fact]
    public void ExplicitProvider_PreferredOverBaseUrlInference()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://proxy.example.com/v1", Provider = "deepseek" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("deepseek/deepseek-chat", models[0].Name);
    }

    [Fact]
    public void SameProviderSameModelTwice_AppendsSequenceNumbers()
    {
        // 同供应商同模型多 Key：两个端点都留空 Name，生成名去重。
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1", ApiKey = "sk-1" },
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1", ApiKey = "sk-2" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("deepseek/deepseek-chat", models[0].Name);
        Assert.Equal("deepseek/deepseek-chat #2", models[1].Name);
    }

    [Fact]
    public void ExplicitNameOccupyingGeneratedBase_GeneratedGetsSuffix()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "deepseek/deepseek-chat", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1" },
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1", ApiKey = "sk-2" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("deepseek/deepseek-chat", models[0].Name);
        Assert.Equal("deepseek/deepseek-chat #2", models[1].Name);
    }

    [Fact]
    public void DifferentProviders_SameModelId_NoCollision()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://api.deepseek.com/v1" },
            new() { Name = "", Id = "deepseek-chat", BaseUrl = "https://api.siliconflow.cn/v1" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("deepseek/deepseek-chat", models[0].Name);
        Assert.Equal("siliconflow/deepseek-chat", models[1].Name);
    }

    [Fact]
    public void ExplicitNames_Untouched()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "gpt-4o", BaseUrl = "https://api.openai.com/v1" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("gpt-4o", models[0].Name);
    }

    [Fact]
    public void EmptyNameWithoutId_LeftEmpty_ForValidatorToReject()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "", BaseUrl = "https://api.deepseek.com/v1" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal(string.Empty, models[0].Name);
    }

    [Fact]
    public void UnknownBaseUrl_FallsBackToModelPrefix()
    {
        var models = new List<ModelEndpointOptions>
        {
            new() { Name = "", Id = "custom-llm", BaseUrl = "not-a-uri" }
        };

        ModelNameNormalizer.Normalize(models);

        Assert.Equal("model/custom-llm", models[0].Name);
    }

    [Fact]
    public void UpstreamModelId_FallsBackToName_WhenIdEmpty()
    {
        var model = new ModelEndpointOptions { Name = "gpt-4o" };
        Assert.Equal("gpt-4o", model.UpstreamModelId);

        model.Id = "gpt-4o-2024-08-06";
        Assert.Equal("gpt-4o-2024-08-06", model.UpstreamModelId);
    }
}

using Microsoft.Extensions.Options;
using OptiRouter.Configuration;

namespace OptiRouter.Tests;

public class RouterOptionsValidatorTests
{
    private static RouterOptionsValidator CreateValidator()
        => new RouterOptionsValidator();

    [Fact]
    public void ValidOptions_ShouldReturnSuccess()
    {
        // Arrange
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions
        {
            Name = "gpt-4o",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            Tier = ModelTier.Strong,
            MaxContextTokens = 128000,
            InputPricePerMillion = 2.5m,
            OutputPricePerMillion = 10.0m
        });

        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void EmptyModels_ShouldReturnFailure()
    {
        // Arrange
        var options = new RouterOptions();
        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("Models 不能为空", result.FailureMessage);
    }

    [Fact]
    public void DuplicateModelNames_ShouldReturnFailure()
    {
        // Arrange
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions { Name = "gpt-4o", BaseUrl = "https://api.openai.com/v1" });
        options.Models.Add(new ModelEndpointOptions { Name = "gpt-4o", BaseUrl = "https://api.openai.com/v1" });

        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("Name 必须唯一", result.FailureMessage);
    }

    [Fact]
    public void NegativePrice_ShouldReturnFailure()
    {
        // Arrange
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions
        {
            Name = "cheap",
            BaseUrl = "https://example.com/v1",
            InputPricePerMillion = -1m,
            OutputPricePerMillion = 0.5m,
            MaxContextTokens = 1000
        });

        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("InputPricePerMillion 不能为负数", result.FailureMessage);
    }

    [Fact]
    public void NonPositiveMaxContextTokens_ShouldReturnFailure()
    {
        // Arrange
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions
        {
            Name = "cheap",
            BaseUrl = "https://example.com/v1",
            MaxContextTokens = 0,
            InputPricePerMillion = 0.1m,
            OutputPricePerMillion = 0.2m
        });

        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("MaxContextTokens 必须大于 0", result.FailureMessage);
    }

    [Fact]
    public void NonPositiveHalfOpenMaxProbes_ShouldReturnFailure()
    {
        // Arrange
        var options = CreateValidOptions();
        options.Routing.FailoverHalfOpenMaxProbes = 0;
        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("FailoverHalfOpenMaxProbes 必须大于 0", result.FailureMessage);
    }

    [Fact]
    public void InvalidTiktokenEncoding_ShouldReturnFailure()
    {
        // Arrange
        var options = CreateValidOptions();
        options.Routing.TokenEstimation = TokenEstimationMode.Tiktoken;
        options.Routing.TiktokenEncoding = "not_a_real_encoding";
        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("不是可用的 tiktoken 编码", result.FailureMessage);
    }

    [Fact]
    public void ValidTiktokenEncoding_ShouldReturnSuccess()
    {
        // Arrange
        var options = CreateValidOptions();
        options.Routing.TokenEstimation = TokenEstimationMode.Tiktoken;
        options.Routing.TiktokenEncoding = "o200k_base";
        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BucketMode_IgnoresEncodingValidity_ShouldReturnSuccess()
    {
        // Arrange：Bucket 模式下编码字段不参与校验，任意值都应通过。
        var options = CreateValidOptions();
        options.Routing.TokenEstimation = TokenEstimationMode.Bucket;
        options.Routing.TiktokenEncoding = "not_a_real_encoding";
        var validator = CreateValidator();

        // Act
        var result = validator.Validate(null, options);

        // Assert
        Assert.True(result.Succeeded);
    }

    private static RouterOptions CreateValidOptions()
    {
        var options = new RouterOptions();
        options.Models.Add(new ModelEndpointOptions
        {
            Name = "gpt-4o",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            Tier = ModelTier.Strong,
            MaxContextTokens = 128000,
            InputPricePerMillion = 2.5m,
            OutputPricePerMillion = 10.0m
        });
        return options;
    }
}

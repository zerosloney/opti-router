using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

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

    [Fact]
    public void NegativeLatencyMinSamples_ShouldReturnFailure()
    {
        var options = CreateValidOptions();
        options.Routing.LatencyMinSamples = -1;
        var validator = CreateValidator();

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("LatencyMinSamples 不能为负数", result.FailureMessage);
    }

    [Fact]
    public void ZeroLatencyStatsWindowMinutes_ShouldReturnFailure()
    {
        var options = CreateValidOptions();
        options.Routing.LatencyStatsWindowMinutes = 0;
        var validator = CreateValidator();

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("LatencyStatsWindowMinutes 必须大于 0", result.FailureMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void FusionMaxParallelOutOfRange_ShouldReturnFailure(int value)
    {
        var options = CreateValidOptions();
        options.Routing.FusionMaxParallel = value;
        var validator = CreateValidator();

        var result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("FusionMaxParallel 必须在 [2, 5]", result.FailureMessage);
    }

    [Fact]
    public void DefaultNewOptions_ShouldValidateSuccessfully()
    {
        // 默认值（延迟感知/能力过滤/并行首试全关闭，参数默认值）应通过校验。
        var options = CreateValidOptions();
        var validator = CreateValidator();

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void UnknownTags_WarnsButSucceeds()
    {
        // 软校验：未识别 tag 仅 warning，不阻断启动。
        var options = CreateValidOptions();
        options.Models[0].Tags.Add("vision");   // 已知
        options.Models[0].Tags.Add("vison");    // 拼写错，未知
        options.Models[0].Tags.Add("custom");   // 自定义，未知
        var capture = new CaptureLogger();
        var validator = new RouterOptionsValidator(capture);

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded); // 软校验不失败
        Assert.Contains(capture.Logs, l =>
            l.LogLevel == LogLevel.Warning &&
            l.Message.Contains("vison") && l.Message.Contains("custom"));
    }

    [Fact]
    public void KnownTags_NoWarning()
    {
        var options = CreateValidOptions();
        options.Models[0].Tags.Add("vision");
        options.Models[0].Tags.Add("tool-use");
        options.Models[0].Tags.Add("json-mode");
        var capture = new CaptureLogger();
        var validator = new RouterOptionsValidator(capture);

        var result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(capture.Logs, l => l.LogLevel == LogLevel.Warning);
    }

    /// <summary>捕获日志条目用于断言。</summary>
    private sealed class CaptureLogger : ILogger<RouterOptionsValidator>
    {
        public List<(LogLevel LogLevel, string Message)> Logs { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Logs.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
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

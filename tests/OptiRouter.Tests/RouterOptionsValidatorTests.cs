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

    [Theory]
    [InlineData("AUTO")]
    [InlineData("SQLITE")]
    [InlineData("mArIaDb")]
    [InlineData("pOsTgReS")]
    [InlineData("rEdIs")]
    [InlineData("iNmEmOrY")]
    public void SupportedStoreProvider_ShouldReturnSuccess(string provider)
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = provider;
        options.Budget.StorePath = string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
            ? "data/test-budget.db"
            : "";
        options.Budget.MariaDbConnectionString = "Server=localhost;Database=test";
        options.Budget.PostgresConnectionString = "Host=localhost;Database=optirouter";
        options.Budget.RedisConnectionString = "localhost:6379";

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void UnsupportedStoreProvider_ShouldReturnFailure()
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = "MongoDb";

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("StoreProvider", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MariaDbWithoutConnectionString_ShouldReturnFailure(string? connectionString)
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = "MariaDb";
        options.Budget.StorePath = "";
        options.Budget.MariaDbConnectionString = connectionString;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("MariaDbConnectionString", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PostgresWithoutConnectionString_ShouldReturnFailure(string? connectionString)
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = "Postgres";
        options.Budget.StorePath = "";
        options.Budget.PostgresConnectionString = connectionString;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("PostgresConnectionString", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RedisWithoutConnectionString_ShouldReturnFailure(string? connectionString)
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = "Redis";
        options.Budget.StorePath = "";
        options.Budget.RedisConnectionString = connectionString;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("RedisConnectionString", result.FailureMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void PersistentSqliteWithoutStorePath_ShouldReturnFailure(string? storePath)
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = "Sqlite";
        options.Budget.UsePersistentStore = true;
        options.Budget.StorePath = storePath!;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("StorePath", result.FailureMessage);
    }

    [Fact]
    public void NonPersistentSqliteWithoutStorePath_ShouldReturnSuccess()
    {
        var options = CreateValidOptions();
        options.Budget.StoreProvider = "Sqlite";
        options.Budget.UsePersistentStore = false;
        options.Budget.StorePath = "";

        var result = CreateValidator().Validate(null, options);

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/v1")]
    [InlineData("api.example.com/v1")]
    [InlineData("ftp://api.example.com/v1")]
    [InlineData("file:///tmp/model")]
    public void InvalidBaseUrl_ShouldFailForStartupAndModelWrite(string baseUrl)
    {
        var options = CreateValidOptions();
        options.Models[0].BaseUrl = baseUrl;
        var model = options.Models[0];

        var startupResult = CreateValidator().Validate(null, options);
        var writeResult = RouterOptionsValidator.ValidateModel(model);

        Assert.False(startupResult.Succeeded);
        Assert.Contains("BaseUrl", startupResult.FailureMessage);
        Assert.Contains("BaseUrl", writeResult);
    }

    [Theory]
    [InlineData("http://169.254.169.254/v1")]          // AWS/GCP 云元数据（链路本地段）
    [InlineData("http://169.254.170.2/v1")]            // ECS 元数据（同段）
    [InlineData("http://metadata.google.internal/v1")] // GCP 元数据域名
    public void CloudMetadataBaseUrl_ShouldFailForStartupAndModelWrite(string baseUrl)
    {
        var options = CreateValidOptions();
        options.Models[0].BaseUrl = baseUrl;

        var startupResult = CreateValidator().Validate(null, options);
        var writeResult = RouterOptionsValidator.ValidateModel(options.Models[0]);

        Assert.False(startupResult.Succeeded);
        Assert.Contains("BaseUrl", startupResult.FailureMessage);
        Assert.Contains("BaseUrl", writeResult);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")] // 本地 Ollama
    [InlineData("http://127.0.0.1:8000/v1")]
    public void LocalLlmBaseUrl_IsAllowed(string baseUrl)
    {
        var options = CreateValidOptions();
        options.Models[0].BaseUrl = baseUrl;

        Assert.Null(RouterOptionsValidator.ValidateModel(options.Models[0]));
    }

    [Theory]
    [InlineData("http://api.example.com/v1")]
    [InlineData("https://api.example.com/v1")]
    public void ValidModelEndpointBoundaries_ShouldPassForStartupAndModelWrite(string baseUrl)
    {
        var options = CreateValidOptions();
        options.Models[0].BaseUrl = baseUrl;
        options.Models[0].TimeoutSeconds = 1;
        options.Models[0].MaxRetries = 0;

        var startupResult = CreateValidator().Validate(null, options);
        var writeResult = RouterOptionsValidator.ValidateModel(options.Models[0]);

        Assert.True(startupResult.Succeeded);
        Assert.Null(writeResult);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeout_ShouldFailForStartupAndModelWrite(int timeoutSeconds)
    {
        var options = CreateValidOptions();
        options.Models[0].TimeoutSeconds = timeoutSeconds;

        var startupResult = CreateValidator().Validate(null, options);
        var writeResult = RouterOptionsValidator.ValidateModel(options.Models[0]);

        Assert.False(startupResult.Succeeded);
        Assert.Contains("TimeoutSeconds", startupResult.FailureMessage);
        Assert.Contains("TimeoutSeconds", writeResult);
    }

    [Theory]
    [InlineData(-1)]
    public void NegativeMaxRetries_ShouldFailForStartupAndModelWrite(int maxRetries)
    {
        var options = CreateValidOptions();
        options.Models[0].MaxRetries = maxRetries;

        var startupResult = CreateValidator().Validate(null, options);
        var writeResult = RouterOptionsValidator.ValidateModel(options.Models[0]);

        Assert.False(startupResult.Succeeded);
        Assert.Contains("MaxRetries", startupResult.FailureMessage);
        Assert.Contains("MaxRetries", writeResult);
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

    [Theory]
    [InlineData(-0.01)]
    [InlineData(2.01)]
    public void FusionRouterTemperatureOutOfRange_ShouldReturnFailure(double value)
    {
        var options = CreateValidOptions();
        options.Routing.FusionRouterTemperature = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("FusionRouterTemperature 必须在 [0, 2]", result.FailureMessage);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(2.01)]
    public void FusionRouterPanelTemperatureOutOfRange_ShouldReturnFailure(double value)
    {
        var options = CreateValidOptions();
        options.Routing.FusionRouterPanelTemperature = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("FusionRouterPanelTemperature 必须在 [0, 2]", result.FailureMessage);
    }

    [Fact]
    public void FusionRouterPanelTemperatureNull_ShouldValidateSuccessfully()
    {
        // P1：null（默认）= 沿用 FusionRouterTemperature，合法。
        var options = CreateValidOptions();
        options.Routing.FusionRouterPanelTemperature = null;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FusionRouterMinComplexityInvalidEnum_ShouldReturnFailure()
    {
        // P3：非法枚举值校验失败。
        var options = CreateValidOptions();
        options.Routing.FusionRouterMinComplexity = (OptiRouter.Routing.RequestComplexity)99;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("FusionRouterMinComplexity", result.FailureMessage);
    }

    [Fact]
    public void FusionRouterMinComplexityValid_ShouldValidateSuccessfully()
    {
        // P3：合法枚举值（Unknown..Complex）通过。
        foreach (var v in new[]
        {
            OptiRouter.Routing.RequestComplexity.Unknown,
            OptiRouter.Routing.RequestComplexity.Simple,
            OptiRouter.Routing.RequestComplexity.Standard,
            OptiRouter.Routing.RequestComplexity.Complex,
        })
        {
            var options = CreateValidOptions();
            options.Routing.FusionRouterMinComplexity = v;
            var result = CreateValidator().Validate(null, options);
            Assert.True(result.Succeeded, $"MinComplexity={v} should pass");
        }
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PromptCacheAffinityTtlMustBePositive(int value)
    {
        var options = CreateValidOptions();
        options.Routing.PromptCacheAffinityTtlSeconds = value;
        var result = CreateValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("PromptCacheAffinityTtlSeconds", result.FailureMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void FusionRouterMinPanelSizeMustBeBounded(int value)
    {
        var options = CreateValidOptions();
        options.Routing.FusionRouterMinPanelSize = value;
        var result = CreateValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("FusionRouterMinPanelSize", result.FailureMessage);
    }

    [Fact]
    public void FusionRouterMinPanelSizeCannotExceedMaximum()
    {
        var options = CreateValidOptions();
        options.Routing.FusionRouterPanelSize = 2;
        options.Routing.FusionRouterMinPanelSize = 3;
        var result = CreateValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("不能大于 FusionRouterPanelSize", result.FailureMessage);
    }

    [Theory]
    [InlineData("cached")]
    [InlineData("write")]
    public void CachePricesCannotBeNegative(string field)
    {
        var options = CreateValidOptions();
        if (field == "cached") options.Models[0].CachedInputPricePerMillion = -1;
        else options.Models[0].CacheWriteInputPricePerMillion = -1;
        var result = CreateValidator().Validate(null, options);
        Assert.False(result.Succeeded);
        Assert.Contains("PricePerMillion", result.FailureMessage);
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

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void ThompsonRaceCancelledRewardOutOfRange_ShouldReturnFailure(double value)
    {
        // 竞速失败奖励越界：[0,1] 之外应被校验拦截（仅启用 Thompson Sampling 时）。
        var options = CreateValidOptions();
        options.Routing.EnableThompsonSampling = true;
        options.Routing.ThompsonRaceCancelledReward = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ThompsonRaceCancelledReward", result.FailureMessage);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void ThompsonRaceCancelledRewardInRange_ShouldSucceed(double value)
    {
        // 边界值 [0,1] 均应通过。
        var options = CreateValidOptions();
        options.Routing.EnableThompsonSampling = true;
        options.Routing.ThompsonRaceCancelledReward = value;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(-1)]
    public void ThompsonLatencyNormalizeRefTokensNegative_ShouldReturnFailure(int value)
    {
        // 延迟归一化基准不能为负数。
        var options = CreateValidOptions();
        options.Routing.ThompsonLatencyNormalizeRefTokens = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ThompsonLatencyNormalizeRefTokens", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void ThompsonLatencyNormalizeRefTokensValid_ShouldSucceed(int value)
    {
        // 边界值 [0, ∞) 均应通过。
        var options = CreateValidOptions();
        options.Routing.ThompsonLatencyNormalizeRefTokens = value;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
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

    [Theory]
    [InlineData(0)]             // 0 = 永久保留（AuditRetentionService 跳过淘汰）
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void AuditRetentionHours_NonNegative_ShouldReturnSuccess(int hours)
    {
        var options = CreateValidOptions();
        options.Routing.AuditRetentionHours = hours;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void AuditRetentionHours_Negative_ShouldReturnFailure()
    {
        var options = CreateValidOptions();
        options.Routing.AuditRetentionHours = -1;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("AuditRetentionHours", result.FailureMessage);
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

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ContextualBanditAlphaNonPositive_ShouldReturnFailure(double value)
    {
        // LinUCB 探索系数 α<=0 会关闭探索（纯利用，冷启动饿死）——应被校验拦截。
        var options = CreateValidOptions();
        options.Routing.EnableContextualBandit = true;
        options.Routing.ContextualBanditAlpha = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ContextualBanditAlpha", result.FailureMessage);
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(1.0)]
    public void ContextualBanditDiscountFactorOutOfRange_ShouldReturnFailure(double value)
    {
        // 折扣越界 [0.5, 0.99] 导致衰减失效或过激——应被校验拦截。
        var options = CreateValidOptions();
        options.Routing.EnableContextualBandit = true;
        options.Routing.ContextualBanditDiscountFactor = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ContextualBanditDiscountFactor", result.FailureMessage);
    }

    [Fact]
    public void ContextualBanditValidConfig_ShouldSucceed()
    {
        var options = CreateValidOptions();
        options.Routing.EnableContextualBandit = true;
        options.Routing.ContextualBanditAlpha = 1.0;
        options.Routing.ContextualBanditDiscountFactor = 0.95;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ContextualBanditDisabled_InvalidParams_ShouldSucceed()
    {
        // 未启用时参数不校验（向后兼容：默认关，参数可留默认）。
        var options = CreateValidOptions();
        options.Routing.EnableContextualBandit = false;
        options.Routing.ContextualBanditAlpha = 0.0;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void BanditAndThompsonBothEnabled_ShouldReturnFailure()
    {
        // H2：互斥契约在启动期拒绝。两者同时开启时启动失败，避免段内 stat 互相覆盖、计数器错位。
        var options = CreateValidOptions();
        options.Routing.EnableContextualBandit = true;
        options.Routing.ContextualBanditAlpha = 1.0;
        options.Routing.ContextualBanditDiscountFactor = 0.95;
        options.Routing.EnableThompsonSampling = true;
        options.Routing.ThompsonLatencyTargetMs = 800.0;
        options.Routing.ThompsonDiscountFactor = 0.95;
        options.Routing.ThompsonRaceCancelledReward = 0.5;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("EnableContextualBandit 与 EnableThompsonSampling 互斥", result.FailureMessage);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void BanditAndThompsonNotBothEnabled_ShouldSucceed(bool bandit, bool thompson)
    {
        // 任一关闭、两者都关，都应通过（向后兼容：默认两关）。
        var options = CreateValidOptions();
        options.Routing.EnableContextualBandit = bandit;
        if (bandit)
        {
            options.Routing.ContextualBanditAlpha = 1.0;
            options.Routing.ContextualBanditDiscountFactor = 0.95;
        }
        options.Routing.EnableThompsonSampling = thompson;
        if (thompson)
        {
            options.Routing.ThompsonLatencyTargetMs = 800.0;
            options.Routing.ThompsonDiscountFactor = 0.95;
            options.Routing.ThompsonRaceCancelledReward = 0.5;
        }

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("bandit")]
    [InlineData("thompson")]
    [InlineData("latency")]
    public void LoadBalance_WithAnotherOrderOwner_ShouldFail(string owner)
    {
        var options = CreateValidOptions();
        options.Routing.EnableLoadBalance = true;
        options.Routing.EnableContextualBandit = owner == "bandit";
        options.Routing.EnableThompsonSampling = owner == "thompson";
        options.Routing.EnableLatencyAware = owner == "latency";

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("EnableLoadBalance", result.FailureMessage);
        Assert.Contains("互斥", result.FailureMessage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ExplorationStarvedNNegative_ShouldReturnFailure(long value)
    {
        // 探索饥饿阈值不能为负数。
        var options = CreateValidOptions();
        options.Routing.ExplorationStarvedN = value;

        var result = CreateValidator().Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains("ExplorationStarvedN", result.FailureMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void ExplorationStarvedNValid_ShouldSucceed(long value)
    {
        // 边界值 [0, ∞) 均应通过。
        var options = CreateValidOptions();
        options.Routing.ExplorationStarvedN = value;

        var result = CreateValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptiRouter.Clients;
using OptiRouter.Components.Services;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// RequestContent 审计存储开关测试。
/// </summary>
public sealed class RequestContentAuditTests
{
    [Fact]
    public void AuditStoreRequestContent_DefaultsFalseAcrossRuntimeDtoUiAndReadme()
    {
        Assert.False(new RoutingOptions().AuditStoreRequestContent);
        Assert.Equal(0, new RoutingOptions().AuditRetentionHours);
        Assert.False(new ApiService.RoutingConfigDto().AuditStoreRequestContent);

        string routerStudio = ReadRepositoryFile("RouterStudio.razor");
        Assert.Contains("public bool AuditStoreRequestContent { get; set; } = false;", routerStudio, StringComparison.Ordinal);

        string readme = ReadRepositoryFile("README.md");
        string readmeDefault = readme
            .Split('\n')
            .Single(line => line.Contains("`AuditStoreRequestContent`", StringComparison.Ordinal));
        Assert.EndsWith("| `false` |", readmeDefault.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public void AuditStoreRequestContent_ExplicitDatabaseTrueStillBindsAsOptIn()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-audit-default-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new AppConfigDbStore(dbPath))
            {
                store.SaveDocument(AppConfigDbStore.RoutingScope, "{\"auditStoreRequestContent\":true}");
            }

            var configuration = new ConfigurationBuilder()
                .Add(new DbAppConfigSource { DbPath = dbPath })
                .Build();
            var options = new RouterOptions();
            configuration.GetSection("OptiRouter").Bind(options);

            Assert.True(options.Routing.AuditStoreRequestContent);
            Assert.Equal(0, options.Routing.AuditRetentionHours);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                string path = dbPath + suffix;
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SemanticCacheHitLog_ContainsSimilarityButNeverPromptContent()
    {
        const string prompt = "privacy regression prompt 5f96b0d2 must never reach the log";
        var endpoint = new ModelEndpointOptions
        {
            Name = "model-a",
            BaseUrl = "https://api.example.com",
            ApiKey = "sk-test",
            Tier = ModelTier.Medium,
            MaxContextTokens = 8192,
            InputPricePerMillion = 1m,
            OutputPricePerMillion = 2m,
            Enabled = true
        };
        var loggerProvider = new CapturingLoggerProvider();
        int upstreamCalls = 0;
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.AddSingleton<ILoggerProvider>(loggerProvider);
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(endpoint);
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
                opt.Routing.EnableResponseCache = false;
                opt.Routing.EnableSemanticCache = true;
                opt.Routing.EnablePromptCompression = false;
                opt.Routing.AuditStoreRequestContent = false;
            });
        };
        factory.MockClients[endpoint.Name] = new MockModelClient(endpoint, (request, ct) =>
        {
            Interlocked.Increment(ref upstreamCalls);
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-semantic-log\",\"model\":\"model-a\",\"choices\":[{\"message\":{\"content\":\"cached\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}",
                new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 }));
        });

        using var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", prompt) }
        });

        using var first = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));
        using var second = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, upstreamCalls);

        string hitLog = Assert.Single(
            loggerProvider.Messages,
            message => message.Contains("Semantic Response Cache HIT", StringComparison.Ordinal));
        Assert.Contains("similarity=", hitLog, StringComparison.Ordinal);
        Assert.DoesNotContain(prompt, hitLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Prompt", hitLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithUserMessage_ReturnsContent()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("system", "You are a helpful assistant."),
                ChatMessage.FromText("user", "What is the capital of France?")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Equal("What is the capital of France?", summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithLongUserMessage_TruncatesTo500Chars()
    {
        // Arrange
        var longText = new string('A', 600);
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("user", longText)
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.NotNull(summary);
        Assert.True(summary!.Length <= 503); // 500 chars + "..."
        Assert.EndsWith("...", summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithMultipleUserMessages_ReturnsLastUserMessage()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("user", "First message"),
                ChatMessage.FromText("assistant", "Response to first"),
                ChatMessage.FromText("user", "Second message (should be returned)")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Equal("Second message (should be returned)", summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithOnlyAssistantMessages_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("assistant", "Hello!")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithEmptyMessages_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = Array.Empty<ChatMessage>()
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithNullMessages_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = null!
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    [Fact]
    public void ExtractRequestContentSummary_WithEmptyUserMessage_ReturnsNull()
    {
        // Arrange
        var request = new ChatRequest
        {
            Messages = new[]
            {
                ChatMessage.FromText("user", ""),
                ChatMessage.FromText("user", "   ")
            }
        };

        // Act
        var summary = OptiRouter.Endpoints.ProxyOrchestrator.ExtractRequestContentSummary(request);

        // Assert
        Assert.Null(summary);
    }

    // 仓库源文件由测试项目复制到输出目录（见 OptiRouter.Tests.csproj），不依赖仓库目录布局。
    private static string ReadRepositoryFile(string fileName)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RepositoryFiles", fileName));

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                    messages.Add(formatter(state, exception));
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public static NoopScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 测试用 IModelClient 实现，可根据配置返回成功响应、抛异常或产出流式块。
/// </summary>
internal sealed class MockModelClient : IModelClient
{
    private readonly Func<ChatRequest, CancellationToken, Task<ChatResponse>>? _completeFunc;
    private readonly Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatStreamChunk>>? _streamFunc;

    /// <inheritdoc />
    public ModelEndpointOptions Endpoint { get; }

    /// <summary>
    /// 初始化 mock 客户端。
    /// </summary>
    /// <param name="endpoint">关联的端点配置。</param>
    /// <param name="completeFunc">非流式回调，为 null 时调用会抛出 NotImplementedException。</param>
    /// <param name="streamFunc">流式回调，为 null 时调用会抛出 NotImplementedException。</param>
    public MockModelClient(
        ModelEndpointOptions endpoint,
        Func<ChatRequest, CancellationToken, Task<ChatResponse>>? completeFunc = null,
        Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatStreamChunk>>? streamFunc = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        _completeFunc = completeFunc;
        _streamFunc = streamFunc;
    }

    /// <inheritdoc />
    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_completeFunc == null)
            throw new NotImplementedException($"CompleteAsync is not set up for model '{Endpoint.Name}'.");
        return _completeFunc(request, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_streamFunc == null)
            throw new NotImplementedException($"StreamAsync is not set up for model '{Endpoint.Name}'.");
        return _streamFunc(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelHealthResult(true, 0));
}

/// <summary>
/// 测试用 IModelClientProvider，从字典按模型名返回预设的 mock 客户端。
/// </summary>
internal sealed class TestModelClientProvider : IModelClientProvider
{
    private readonly Dictionary<string, IModelClient> _clients;

    /// <summary>
    /// 初始化测试客户端提供者。
    /// </summary>
    /// <param name="clients">模型名到 mock 客户端的映射。</param>
    public TestModelClientProvider(Dictionary<string, IModelClient> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        _clients = clients;
    }

    /// <inheritdoc />
    public IModelClient GetClient(ModelEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (_clients.TryGetValue(endpoint.Name, out var client))
            return client;
        throw new KeyNotFoundException($"No mock client registered for model '{endpoint.Name}'.");
    }
}

/// <summary>
/// 测试专用的 WebApplicationFactory，允许注入 mock IModelClient 和覆盖 RouterOptions。
/// </summary>
internal sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestProxyApiKey = "test-proxy-key";

    /// <summary>
    /// 模型名到 mock 客户端的映射。
    /// </summary>
    public Dictionary<string, IModelClient> MockClients { get; } = new();

    /// <summary>
    /// 额外的测试服务配置回调。
    /// </summary>
    public Action<IServiceCollection>? ConfigureTestServicesAction { get; set; }

    public string ProxyApiKey { get; set; } = TestProxyApiKey;

    public int RequestsPerMinute { get; set; } = 60;

    public new HttpClient CreateClient()
    {
        return CreateClient(TestProxyApiKey);
    }

    public HttpClient CreateClient(string? apiKey)
    {
        var client = base.CreateClient();
        if (apiKey is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("OptiRouter:ProxyApiKey", ProxyApiKey);
        builder.UseSetting("OptiRouter:RequestsPerMinute", RequestsPerMinute.ToString());
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IModelClientProvider>(new TestModelClientProvider(MockClients));
            ConfigureTestServicesAction?.Invoke(services);
        });
    }
}

/// <summary>
/// ChatCompletionsEndpoint 集成测试，走完整 HTTP 管道。
/// </summary>
public class ChatCompletionsEndpointTests
{
    private static ModelEndpointOptions CreateEndpoint(string name)
    {
        return new ModelEndpointOptions
        {
            Name = name,
            BaseUrl = "https://api.example.com",
            ApiKey = "sk-test",
            Tier = ModelTier.Medium,
            MaxContextTokens = 8192,
            InputPricePerMillion = 1m,
            OutputPricePerMillion = 2m,
            Enabled = true
        };
    }

    private static ChatRequest BuildRequest(string model, bool stream = false)
    {
        return new ChatRequest
        {
            Model = model,
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "Hi" } },
            Stream = stream
        };
    }

    private static async IAsyncEnumerable<ChatStreamChunk> CreateStreamChunks(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatStreamChunk
            {
                Id = "chatcmpl-1",
                DeltaContent = i == 0 ? words[i] : " " + words[i]
            };
            await Task.Yield();
        }

        yield return new ChatStreamChunk
        {
            Id = "chatcmpl-1",
            DeltaContent = null,
            FinishReason = "stop",
            Usage = new ChatUsage { PromptTokens = 5, CompletionTokens = 2, TotalTokens = 7 }
        };
    }

    private static async IAsyncEnumerable<ChatStreamChunk> CreateFailingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable,
        string responseBody = "failed before first chunk")
    {
        ct.ThrowIfCancellationRequested();
        yield return await Task.FromException<ChatStreamChunk>(
            new ModelClientException(statusCode, responseBody));
    }

    private static TestWebApplicationFactory CreateSecurityFactory(Action? onUpstream = null)
    {
        var factory = new TestWebApplicationFactory();
        var endpoint = CreateEndpoint("model-a");
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(endpoint);
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };
        factory.MockClients["model-a"] = new MockModelClient(endpoint, (req, ct) =>
        {
            onUpstream?.Invoke();
            return Task.FromResult(new ChatResponse
            {
                Id = "chatcmpl-security",
                Model = "model-a",
                Choices = new List<ChatChoice>(),
                Usage = new ChatUsage()
            });
        });
        return factory;
    }

    #region Security tests

    [Theory]
    [InlineData(TestWebApplicationFactory.TestProxyApiKey, null)]
    [InlineData(TestWebApplicationFactory.TestProxyApiKey, "wrong-key")]
    [InlineData("", TestWebApplicationFactory.TestProxyApiKey)]
    public async Task Post_UnauthorizedOrUnconfigured_Returns401BeforeCallingUpstream(
        string configuredKey,
        string? providedKey)
    {
        // Arrange
        int attempts = 0;
        using var factory = CreateSecurityFactory(() => attempts++);
        factory.ProxyApiKey = configuredKey;
        using var client = factory.CreateClient(providedKey);
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("model-a")), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Post_CorrectProxyApiKey_ReachesEndpoint()
    {
        // Arrange
        int attempts = 0;
        using var factory = CreateSecurityFactory(() => attempts++);
        using var client = factory.CreateClient(TestWebApplicationFactory.TestProxyApiKey);
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("model-a")), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Post_OverLimit_Returns429AndAuthenticationRunsFirst()
    {
        // Arrange
        int attempts = 0;
        using var factory = CreateSecurityFactory(() => attempts++);
        factory.RequestsPerMinute = 1;
        using var unauthorizedClient = factory.CreateClient("wrong-key");
        using var authorizedClient = factory.CreateClient(TestWebApplicationFactory.TestProxyApiKey);

        // Act
        using var unauthorizedContent = new StringContent(JsonSerializer.Serialize(BuildRequest("model-a")), Encoding.UTF8, "application/json");
        var unauthorized = await unauthorizedClient.PostAsync("/v1/chat/completions", unauthorizedContent);
        using var firstContent = new StringContent(JsonSerializer.Serialize(BuildRequest("model-a")), Encoding.UTF8, "application/json");
        var first = await authorizedClient.PostAsync("/v1/chat/completions", firstContent);
        using var secondContent = new StringContent(JsonSerializer.Serialize(BuildRequest("model-a")), Encoding.UTF8, "application/json");
        var second = await authorizedClient.PostAsync("/v1/chat/completions", secondContent);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Health_WithoutKey_IsPublicAndUnlimited()
    {
        // Arrange
        using var factory = CreateSecurityFactory();
        factory.RequestsPerMinute = 1;
        using var client = factory.CreateClient(apiKey: null);

        // Act
        var first = await client.GetAsync("/health");
        var second = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public void InvalidRequestsPerMinute_FailsStartup()
    {
        // Arrange
        using var factory = CreateSecurityFactory();
        factory.RequestsPerMinute = 0;

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        // Assert
        Assert.Contains("OptiRouter:RequestsPerMinute must be greater than zero.", exception.ToString());
    }

    #endregion

    #region Non-streaming tests

    [Fact]
    public async Task Post_NonStreaming_Returns200AndRecordsCost()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var endpoint = CreateEndpoint("model-a");
        factory.MockClients["model-a"] = new MockModelClient(endpoint, (req, ct) =>
        {
            var response = new ChatResponse
            {
                Id = "chatcmpl-1",
                Model = "model-a",
                Choices = new List<ChatChoice>
                {
                    new ChatChoice
                    {
                        Index = 0,
                        Message = new ChatMessage { Role = "assistant", Content = "Hello!" },
                        FinishReason = "stop"
                    }
                },
                Usage = new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }
            };
            return Task.FromResult(response);
        });

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("model-a", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello!", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetSpend().Session > 0, "Expected cost to be recorded after successful request.");
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("role")]
    [InlineData("content-null")]
    [InlineData("temperature-low")]
    [InlineData("temperature-high")]
    [InlineData("max-tokens")]
    public async Task Post_InvalidRequest_Returns400BeforeCallingUpstream(string invalidField)
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };

        int attempts = 0;
        var endpoint = CreateEndpoint("model-a");
        factory.MockClients["model-a"] = new MockModelClient(endpoint, (req, ct) =>
        {
            attempts++;
            return Task.FromResult(new ChatResponse());
        });

        var validRequest = BuildRequest("model-a");
        var request = invalidField switch
        {
            "messages" => validRequest with { Messages = new List<ChatMessage>() },
            "role" => validRequest with { Messages = new List<ChatMessage> { new ChatMessage { Role = " " } } },
            "content-null" => validRequest with { Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = null! } } },
            "temperature-low" => validRequest with { Temperature = -0.1 },
            "temperature-high" => validRequest with { Temperature = 2.1 },
            "max-tokens" => validRequest with { MaxTokens = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField))
        };

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, attempts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Post_NonStreaming_RetryableFailure_FallsBackToNextCandidate(HttpStatusCode? statusCode)
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Models.Add(CreateEndpoint("model-b"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");

        factory.MockClients["model-a"] = new MockModelClient(endpointA, (req, ct) =>
        {
            if (statusCode is not null)
                throw new ModelClientException(statusCode.Value, "model-a failed");
            throw new HttpRequestException("model-a network failed");
        });

        factory.MockClients["model-b"] = new MockModelClient(endpointB, (req, ct) =>
        {
            return Task.FromResult(new ChatResponse
            {
                Id = "chatcmpl-b",
                Model = "model-b",
                Choices = new List<ChatChoice>
                {
                    new ChatChoice
                    {
                        Index = 0,
                        Message = new ChatMessage { Role = "assistant", Content = "From B" },
                        FinishReason = "stop"
                    }
                },
                Usage = new ChatUsage { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 }
            });
        });

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("model-b", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("From B", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Post_NonStreaming_RuleClassifierStrongFails_FallsBackToMediumTier()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        var strongEndpoint = CreateEndpoint("strong-model");
        strongEndpoint.Tier = ModelTier.Strong;
        var mediumEndpoint = CreateEndpoint("medium-model");
        int strongAttempts = 0;
        int mediumAttempts = 0;

        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(strongEndpoint);
                opt.Models.Add(mediumEndpoint);
                opt.Routing.EnableRuleClassifier = true;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = true;
            });
        };

        factory.MockClients["strong-model"] = new MockModelClient(strongEndpoint, (req, ct) =>
        {
            strongAttempts++;
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "strong failed");
        });
        factory.MockClients["medium-model"] = new MockModelClient(mediumEndpoint, (req, ct) =>
        {
            mediumAttempts++;
            return Task.FromResult(new ChatResponse
            {
                Id = "chatcmpl-medium",
                Model = "medium-model",
                Choices = new List<ChatChoice>(),
                Usage = new ChatUsage()
            });
        });

        using var client = factory.CreateClient();
        var request = new ChatRequest
        {
            Model = "strong-model",
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "public class Example {}" } }
        };
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("medium-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, strongAttempts);
        Assert.Equal(1, mediumAttempts);
    }

    [Fact]
    public async Task Post_NonStreaming_Upstream4xx_ReturnsSameStatusWithoutFailoverOrResponseBody()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Models.Add(CreateEndpoint("model-b"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = true;
                opt.Routing.FailoverFailureThreshold = 1;
            });
        };

        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        int fallbackAttempts = 0;
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (req, ct) =>
            throw new ModelClientException(HttpStatusCode.UnprocessableEntity, "sensitive upstream body"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, (req, ct) =>
        {
            fallbackAttempts++;
            return Task.FromResult(new ChatResponse());
        });

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("model-a")), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, fallbackAttempts);
        Assert.DoesNotContain("sensitive upstream body", await response.Content.ReadAsStringAsync());
        Assert.False(factory.Services.GetRequiredService<ModelHealthTracker>().IsCoolingDown("model-a"));
    }

    [Fact]
    public async Task Post_NonStreaming_AllCandidatesFail_Returns503()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Models.Add(CreateEndpoint("model-b"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");

        factory.MockClients["model-a"] = new MockModelClient(endpointA, (req, ct) =>
            throw new ModelClientException(HttpStatusCode.InternalServerError, "model-a boom"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, (req, ct) =>
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "model-b boom"));

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("All model candidates failed", body);
        Assert.Contains("model-a, model-b", body);
    }

    [Fact]
    public async Task Post_NonStreaming_BudgetExhaustedWithReject_Returns429()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("strong-model"));
                opt.Models.Add(CreateEndpoint("cheap-model"));
                opt.Budget.DailyBudgetUsd = 10m;
                opt.Budget.EnforceOnExhausted = BudgetExhaustionMode.Reject;
                opt.Routing.EnableBudgetGuard = true;
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var endpoint = CreateEndpoint("strong-model");
        factory.MockClients["strong-model"] = new MockModelClient(endpoint, (req, ct) =>
            Task.FromResult(new ChatResponse
            {
                Id = "chatcmpl-1",
                Model = "strong-model",
                Choices = new List<ChatChoice>(),
                Usage = new ChatUsage()
            }));

        // Pre-fill ledger to exceed budget.
        var ledger = factory.Services.GetRequiredService<CostLedger>();
        ledger.Record(20m);

        using var client = factory.CreateClient();
        var request = BuildRequest("strong-model", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Budget exhausted", body);
    }

    #endregion

    #region Streaming tests

    [Fact]
    public async Task Post_Streaming_ReturnsSseEventsAndDone()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var endpoint = CreateEndpoint("model-a");
        factory.MockClients["model-a"] = new MockModelClient(endpoint, streamFunc: (req, ct) => CreateStreamChunks("hello world", ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        var dataLines = new List<string>();
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var data = line.Substring("data: ".Length).Trim();
            if (data == "[DONE]")
            {
                dataLines.Add("[DONE]");
                break;
            }
            dataLines.Add(data);
        }

        Assert.NotEmpty(dataLines);
        Assert.Equal("[DONE]", dataLines[^1]);

        using var firstDoc = JsonDocument.Parse(dataLines[0]);
        Assert.Equal("hello", firstDoc.RootElement.GetProperty("delta_content").GetString());

        using var lastDoc = JsonDocument.Parse(dataLines[^2]);
        Assert.Equal("stop", lastDoc.RootElement.GetProperty("finish_reason").GetString());
        Assert.Equal(7, lastDoc.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetSpend().Session > 0, "Expected streaming cost to be recorded.");
    }

    [Fact]
    public async Task Post_Streaming_AllCandidatesFail_ReturnsErrorEventInStream()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Models.Add(CreateEndpoint("model-b"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");

        factory.MockClients["model-a"] = new MockModelClient(endpointA, streamFunc: (req, ct) =>
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "model-a failed"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, streamFunc: (req, ct) =>
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "model-b failed"));

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

        // Assert
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // stream always returns 200, error is in SSE
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        var dataLines = new List<string>();
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var data = line.Substring("data: ".Length).Trim();
            if (data == "[DONE]")
            {
                dataLines.Add("[DONE]");
                break;
            }
            dataLines.Add(data);
        }

        Assert.NotEmpty(dataLines);
        Assert.Equal("[DONE]", dataLines[^1]);
        using var errorDoc = JsonDocument.Parse(dataLines[0]);
        Assert.Equal("all model candidates failed", errorDoc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Post_Streaming_RuleClassifierStrongFailsBeforeFirstChunk_FallsBackToCheapTier()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        var strongEndpoint = CreateEndpoint("strong-model");
        strongEndpoint.Tier = ModelTier.Strong;
        var cheapEndpoint = CreateEndpoint("cheap-model");
        cheapEndpoint.Tier = ModelTier.Cheap;
        int strongAttempts = 0;
        int cheapAttempts = 0;

        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(strongEndpoint);
                opt.Models.Add(cheapEndpoint);
                opt.Routing.EnableRuleClassifier = true;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = true;
            });
        };

        factory.MockClients["strong-model"] = new MockModelClient(strongEndpoint, streamFunc: (req, ct) =>
        {
            strongAttempts++;
            return CreateFailingStream(ct);
        });
        factory.MockClients["cheap-model"] = new MockModelClient(cheapEndpoint, streamFunc: (req, ct) =>
        {
            cheapAttempts++;
            return CreateStreamChunks("cheap response", ct);
        });

        using var client = factory.CreateClient();
        var request = new ChatRequest
        {
            Model = "strong-model",
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "public class Example {}" } },
            Stream = true
        };
        using var httpContent = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"delta_content\":\"cheap\"", body);
        Assert.Equal(1, strongAttempts);
        Assert.Equal(1, cheapAttempts);
    }

    [Fact]
    public async Task Post_Streaming_Upstream4xxBeforeFirstChunk_ReturnsSameStatusWithoutFailoverOrResponseBody()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
                opt.Models.Add(CreateEndpoint("model-b"));
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableBudgetGuard = false;
                opt.Routing.EnableFailover = true;
                opt.Routing.FailoverFailureThreshold = 1;
            });
        };

        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        int fallbackAttempts = 0;
        factory.MockClients["model-a"] = new MockModelClient(endpointA, streamFunc: (req, ct) =>
            CreateFailingStream(ct, HttpStatusCode.UnprocessableEntity, "sensitive upstream body"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, streamFunc: (req, ct) =>
        {
            fallbackAttempts++;
            return CreateStreamChunks("unexpected fallback", ct);
        });

        using var client = factory.CreateClient();
        var request = BuildRequest("model-a", stream: true);
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, fallbackAttempts);
        Assert.DoesNotContain("sensitive upstream body", await response.Content.ReadAsStringAsync());
        Assert.False(factory.Services.GetRequiredService<ModelHealthTracker>().IsCoolingDown("model-a"));
    }

    #endregion

    #region Budget degrade test

    [Fact]
    public async Task Post_NonStreaming_BudgetExhaustedWithDegrade_RoutesToCheapest()
    {
        // Arrange
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "strong-model",
                    Tier = ModelTier.Strong,
                    MaxContextTokens = 8192,
                    InputPricePerMillion = 5m,
                    OutputPricePerMillion = 10m,
                    Enabled = true
                });
                opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "cheap-model",
                    Tier = ModelTier.Cheap,
                    MaxContextTokens = 8192,
                    InputPricePerMillion = 0.1m,
                    OutputPricePerMillion = 0.2m,
                    Enabled = true
                });
                opt.Budget.DailyBudgetUsd = 10m;
                opt.Budget.EnforceOnExhausted = BudgetExhaustionMode.Degrade;
                opt.Routing.EnableBudgetGuard = true;
                opt.Routing.EnableRuleClassifier = false;
                opt.Routing.EnableTokenEstimator = false;
                opt.Routing.EnableFailover = false;
            });
        };

        var strongEndpoint = CreateEndpoint("strong-model");
        var cheapEndpoint = CreateEndpoint("cheap-model");

        factory.MockClients["strong-model"] = new MockModelClient(strongEndpoint, (req, ct) =>
            Task.FromResult(new ChatResponse
            {
                Id = "chatcmpl-strong",
                Model = "strong-model",
                Choices = new List<ChatChoice>
                {
                    new ChatChoice { Index = 0, Message = new ChatMessage { Role = "assistant", Content = "Strong" }, FinishReason = "stop" }
                },
                Usage = new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }
            }));

        factory.MockClients["cheap-model"] = new MockModelClient(cheapEndpoint, (req, ct) =>
            Task.FromResult(new ChatResponse
            {
                Id = "chatcmpl-cheap",
                Model = "cheap-model",
                Choices = new List<ChatChoice>
                {
                    new ChatChoice { Index = 0, Message = new ChatMessage { Role = "assistant", Content = "Cheap" }, FinishReason = "stop" }
                },
                Usage = new ChatUsage { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 }
            }));

        // Pre-fill ledger to exceed budget.
        var ledger = factory.Services.GetRequiredService<CostLedger>();
        ledger.Record(20m);

        using var client = factory.CreateClient();
        var request = BuildRequest("strong-model", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("cheap-model", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("Cheap", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    #endregion
}

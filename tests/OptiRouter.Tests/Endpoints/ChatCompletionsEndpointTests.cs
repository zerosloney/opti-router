using System.Net;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 测试用 IModelClient 实现，可返回原始响应、抛异常或产出原始流式行。
/// </summary>
internal sealed class MockModelClient : IModelClient
{
    private readonly Func<ChatRequest, CancellationToken, Task<RawChatResponse>>? _completeRawFunc;
    private readonly Func<ChatRequest, CancellationToken, Task<Clients.ChatResponse>>? _completeFunc;
    private readonly Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>>? _streamRawFunc;

    /// <inheritdoc />
    public ModelEndpointOptions Endpoint { get; }

    /// <summary>
    /// 初始化 mock 客户端。
    /// </summary>
    /// <param name="endpoint">关联的端点配置。</param>
    /// <param name="completeRawFunc">非流式原始回调，为 null 时调用会抛出 NotImplementedException。</param>
    /// <param name="streamRawFunc">流式原始回调，为 null 时调用会抛出 NotImplementedException。</param>
    /// <param name="completeFunc">解析后的非流式回调（级联自校验用 CompleteAsync）；为 null 时抛 NotImplementedException。</param>
    public MockModelClient(
        ModelEndpointOptions endpoint,
        Func<ChatRequest, CancellationToken, Task<RawChatResponse>>? completeRawFunc = null,
        Func<ChatRequest, CancellationToken, IAsyncEnumerable<RawStreamLine>>? streamRawFunc = null,
        Func<ChatRequest, CancellationToken, Task<Clients.ChatResponse>>? completeFunc = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        Endpoint = endpoint;
        _completeRawFunc = completeRawFunc;
        _streamRawFunc = streamRawFunc;
        _completeFunc = completeFunc;
    }

    /// <inheritdoc />
    public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_completeRawFunc == null)
            throw new NotImplementedException($"CompleteRawAsync is not set up for model '{Endpoint.Name}'.");
        return _completeRawFunc(request, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_streamRawFunc == null)
            throw new NotImplementedException($"StreamRawAsync is not set up for model '{Endpoint.Name}'.");
        return _streamRawFunc(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Clients.ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        if (_completeFunc == null)
            throw new NotImplementedException($"CompleteAsync is not set up for model '{Endpoint.Name}'.");
        return _completeFunc(request, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Clients.ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Legacy StreamAsync not used; use StreamRawAsync.");

    /// <inheritdoc />
    public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
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

    /// <summary>测试用管理密钥；null = 不覆盖（沿用 appsettings/环境的值）。</summary>
    public string? AdminApiKey { get; set; } = null;

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
        if (AdminApiKey is not null)
        {
            builder.UseSetting("OptiRouter:AdminApiKey", AdminApiKey);
        }
        // 测试用临时配置库：路由/预算/模型配置隔离在各自测试实例，不污染真实 data/optirouter-config.db。
        builder.UseSetting("OptiRouter:ConfigDbPath",
            Path.Combine(Path.GetTempPath(), "optirouter-config-test-" + Guid.NewGuid().ToString("N") + ".db"));
        // 测试用内存账本，避免写真实 SQLite 文件与跨测试状态残留。
        builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
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
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "Hi") },
            Stream = stream
        };
    }

    private static async Task<HttpResponseMessage> PostChatAsync(
        HttpClient client,
        string model = "auto",
        string? sessionId = null,
        bool stream = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(BuildRequest(model, stream)),
                Encoding.UTF8,
                "application/json")
        };
        if (sessionId is not null)
            request.Headers.Add("X-Session-Id", sessionId);

        return await client.SendAsync(
            request,
            stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead);
    }

    private static TenantKeyFixture CreateTenantKeyFixture(
        decimal dailyBudgetUsd = 100m,
        int maxQps = 50,
        Action? onUpstream = null)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "optirouter-endpoint-client-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, "client-keys.json");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

        var factory = CreateSecurityFactory(onUpstream);
        var existingConfiguration = factory.ConfigureTestServicesAction;
        factory.ConfigureTestServicesAction = services =>
        {
            existingConfiguration?.Invoke(services);
            services.AddSingleton<ClientKeyService>(sp => new ClientKeyService(
                filePath,
                sp.GetRequiredService<ILogger<ClientKeyService>>(),
                clock));
        };

        var service = factory.Services.GetRequiredService<ClientKeyService>();
        var created = service.CreateKey("tenant-test", dailyBudgetUsd, maxQps);
        return new TenantKeyFixture(factory, service, created, directory);
    }

    private sealed class TenantKeyFixture : IDisposable
    {
        private readonly string _directory;

        public TenantKeyFixture(
            TestWebApplicationFactory factory,
            ClientKeyService service,
            (string PlaintextKey, ClientKeyInfo Info) created,
            string directory)
        {
            Factory = factory;
            Service = service;
            PlaintextKey = created.PlaintextKey;
            Info = created.Info;
            _directory = directory;
        }

        public TestWebApplicationFactory Factory { get; }
        public ClientKeyService Service { get; }
        public string PlaintextKey { get; }
        public ClientKeyInfo Info { get; }

        public void Dispose()
        {
            Factory.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async IAsyncEnumerable<RawStreamLine> CreateStreamChunks(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var delta = i == 0 ? words[i] : " " + words[i];
            yield return new RawStreamLine(
                $"{{\"id\":\"chatcmpl-1\",\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{delta}\"}}}}]}}",
                null);
            await Task.Yield();
        }

        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}",
            new ChatUsage { PromptTokens = 5, CompletionTokens = 2, TotalTokens = 7 });
        yield return new RawStreamLine("[DONE]", null);
    }

    private static async IAsyncEnumerable<RawStreamLine> CreateStreamChunksWithoutUsage(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-no-usage\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"}}]}",
            null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-no-usage\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}",
            null);
        yield return new RawStreamLine("[DONE]", null);
    }

    private static async IAsyncEnumerable<RawStreamLine> CreateFailingStream(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        HttpStatusCode statusCode = HttpStatusCode.ServiceUnavailable,
        string responseBody = "failed before first chunk",
        UpstreamResponseMetadata? metadata = null)
    {
        ct.ThrowIfCancellationRequested();
        yield return await Task.FromException<RawStreamLine>(
            new ModelClientException(statusCode, responseBody, metadata: metadata));
    }

    /// <summary>
    /// 模拟中途失败流：先 yield 一个正常首 chunk（让代理 flush 200 + 透传），
    /// 随后抛指定异常模拟不同失败类型（上游断连/超时/size limit）。
    /// 用于验证 endpoint 的中途错误注入 + code 分类路径。
    /// </summary>
    private static async IAsyncEnumerable<RawStreamLine> CreateMidStreamFailingStream(
        Exception midStreamException,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        yield return new RawStreamLine(
            "{\"id\":\"chatcmpl-mid\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}",
            null);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        throw midStreamException;
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
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-security\",\"model\":\"model-a\",\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}",
                new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 }));
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
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            var error = document.RootElement.GetProperty("error");
            Assert.Equal("authentication_error", error.GetProperty("type").GetString());
            Assert.Equal("INVALID_API_KEY", error.GetProperty("code").GetString());
            Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
        }
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Post_AssistantNullContentWithToolCalls_IsAccepted_ButToolNullContentIsRejected()
    {
        int attempts = 0;
        using var factory = CreateSecurityFactory(() => attempts++);
        using var client = factory.CreateClient();

        using var assistantContent = new StringContent(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"weather\",\"arguments\":\"{}\"}}]}]}",
            Encoding.UTF8,
            "application/json");
        using var assistantResponse = await client.PostAsync("/v1/chat/completions", assistantContent);

        Assert.Equal(HttpStatusCode.OK, assistantResponse.StatusCode);
        Assert.Equal(1, attempts);

        using var toolContent = new StringContent(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"tool\",\"content\":null,\"tool_call_id\":\"call_1\"}]}",
            Encoding.UTF8,
            "application/json");
        using var toolResponse = await client.PostAsync("/v1/chat/completions", toolContent);

        Assert.Equal(HttpStatusCode.BadRequest, toolResponse.StatusCode);
        using var toolDocument = JsonDocument.Parse(await toolResponse.Content.ReadAsStringAsync());
        var toolError = toolDocument.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", toolError.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", toolError.GetProperty("code").GetString());
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Post_UnknownModel_ReturnsOpenAiErrorEnvelope()
    {
        using var factory = CreateSecurityFactory();
        using var client = factory.CreateClient();

        using var response = await PostChatAsync(client, model: "no-such-model");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_CorrectProxyApiKey_ReachesEndpoint()
    {
        // Arrange
        int attempts = 0;
        using var factory = CreateSecurityFactory(() => attempts++);
        using var client = factory.CreateClient(TestWebApplicationFactory.TestProxyApiKey);
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Post_TenantKey_ReachesV1Endpoint()
    {
        using var tenant = CreateTenantKeyFixture();
        using var client = tenant.Factory.CreateClient(tenant.PlaintextKey);

        using var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Assert.Single(tenant.Service.GetAllKeys()).DailySpendUsd > 0m);
    }

    [Fact]
    public async Task Post_TenantSessionId_IsScopedPerClientKey_AndStableForSameTenant()
    {
        using var tenant = CreateTenantKeyFixture();
        var secondTenant = tenant.Service.CreateKey("tenant-two");
        using var firstClient = tenant.Factory.CreateClient(tenant.PlaintextKey);
        using var secondClient = tenant.Factory.CreateClient(secondTenant.PlaintextKey);

        using var first = await PostChatAsync(firstClient, sessionId: "shared-session");
        using var sameTenant = await PostChatAsync(firstClient, sessionId: "shared-session");
        using var otherTenant = await PostChatAsync(secondClient, sessionId: "shared-session");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        _ = await first.Content.ReadAsStringAsync();
        _ = await sameTenant.Content.ReadAsStringAsync();
        _ = await otherTenant.Content.ReadAsStringAsync();

        var audits = GetTenantSessionAudits(tenant.Factory, expectedCount: 3);
        var bySession = audits.GroupBy(record => record.SessionId).ToList();
        Assert.Equal(2, bySession.Count);
        Assert.Contains(bySession, group => group.Count() == 2);
        Assert.Contains(bySession, group => group.Count() == 1);

        var sameTenantSession = bySession.Single(group => group.Count() == 2).Key!;
        var otherTenantSession = bySession.Single(group => group.Count() == 1).Key!;
        Assert.Contains(tenant.Info.KeyId, sameTenantSession, StringComparison.Ordinal);
        Assert.Contains(secondTenant.Info.KeyId, otherTenantSession, StringComparison.Ordinal);
        Assert.NotEqual(sameTenantSession, otherTenantSession);

        var ledger = tenant.Factory.Services.GetRequiredService<CostLedger>();
        Assert.Equal(
            bySession.Single(group => group.Key == sameTenantSession).Sum(record => record.Cost),
            ledger.GetSessionSpend(sameTenantSession));
        Assert.Equal(
            bySession.Single(group => group.Key == otherTenantSession).Sum(record => record.Cost),
            ledger.GetSessionSpend(otherTenantSession));
    }

    [Fact]
    public async Task Post_StreamingTenantSessionId_IsScopedPerClientKey_AndStableForSameTenant()
    {
        using var tenant = CreateTenantKeyFixture();
        var secondTenant = tenant.Service.CreateKey("tenant-two");
        var endpoint = CreateEndpoint("model-a");
        tenant.Factory.MockClients["model-a"] = new MockModelClient(
            endpoint,
            streamRawFunc: (_, ct) => CreateStreamChunks("tenant stream", ct));
        using var firstClient = tenant.Factory.CreateClient(tenant.PlaintextKey);
        using var secondClient = tenant.Factory.CreateClient(secondTenant.PlaintextKey);

        using var first = await PostChatAsync(firstClient, sessionId: "shared-session", stream: true);
        using var sameTenant = await PostChatAsync(firstClient, sessionId: "shared-session", stream: true);
        using var otherTenant = await PostChatAsync(secondClient, sessionId: "shared-session", stream: true);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        _ = await first.Content.ReadAsStringAsync();
        _ = await sameTenant.Content.ReadAsStringAsync();
        _ = await otherTenant.Content.ReadAsStringAsync();

        var audits = GetTenantSessionAudits(tenant.Factory, expectedCount: 3);
        var bySession = audits.GroupBy(record => record.SessionId).ToList();
        Assert.Equal(2, bySession.Count);
        Assert.Equal(2, bySession.Single(group => group.Count() == 2).Count());
        Assert.Single(bySession.Single(group => group.Count() == 1));
        Assert.NotEqual(
            bySession.Single(group => group.Count() == 2).Key,
            bySession.Single(group => group.Count() == 1).Key);
    }

    private static List<RequestAuditRecord> GetTenantSessionAudits(
        TestWebApplicationFactory factory,
        int expectedCount)
    {
        var records = factory.Services.GetRequiredService<IRequestAuditStore>()
            .GetRecent(10)
            .Where(record => record.Success && record.SessionId is not null)
            .ToList();
        Assert.Equal(expectedCount, records.Count);
        return records;
    }

    [Fact]
    public async Task Post_ResponseCache_IsPartitionedByTenantKey()
    {
        int attempts = 0;
        using var tenant = CreateTenantKeyFixture(onUpstream: () => attempts++);
        var secondTenant = tenant.Service.CreateKey("tenant-two");
        var routing = tenant.Factory.Services.GetRequiredService<IOptionsMonitor<RouterOptions>>().CurrentValue.Routing;
        routing.EnableResponseCache = true;
        routing.ResponseCacheTtlSeconds = 60;
        routing.EnableSemanticCache = false;

        using var firstClient = tenant.Factory.CreateClient(tenant.PlaintextKey);
        using var secondClient = tenant.Factory.CreateClient(secondTenant.PlaintextKey);

        using var first = await PostChatAsync(firstClient);
        using var sameTenant = await PostChatAsync(firstClient);
        using var otherTenant = await PostChatAsync(secondClient);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sameTenant.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherTenant.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Post_InvalidOrDisabledTenantKey_Returns401WithoutTenantDetails()
    {
        using var tenant = CreateTenantKeyFixture();

        using (var invalidClient = tenant.Factory.CreateClient("wrong-tenant-key"))
        using (var invalidResponse = await PostChatAsync(invalidClient))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
            var body = await invalidResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("tenant-test", body, StringComparison.Ordinal);
            Assert.DoesNotContain(tenant.PlaintextKey, body, StringComparison.Ordinal);
        }

        Assert.True(tenant.Service.UpdateKey(tenant.Info.KeyId, enabled: false, dailyBudgetUsd: null, maxQps: null));
        using var disabledClient = tenant.Factory.CreateClient(tenant.PlaintextKey);
        using var disabledResponse = await PostChatAsync(disabledClient);

        Assert.Equal(HttpStatusCode.Unauthorized, disabledResponse.StatusCode);
    }

    [Fact]
    public async Task Post_TenantKeyQpsExceeded_Returns429WithRetryAfter()
    {
        using var tenant = CreateTenantKeyFixture(dailyBudgetUsd: 0m, maxQps: 1);
        using var client = tenant.Factory.CreateClient(tenant.PlaintextKey);

        using var first = await PostChatAsync(client);
        using var second = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter.Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        var body = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("tenant-test", body, StringComparison.Ordinal);
        Assert.DoesNotContain(tenant.PlaintextKey, body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("application/json", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal("rate_limit_error", error.GetProperty("type").GetString());
        Assert.Equal("RATE_LIMIT_EXCEEDED", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Post_TenantKeyBudgetExceeded_Returns429WithRetryAfter()
    {
        using var tenant = CreateTenantKeyFixture(dailyBudgetUsd: 1m, maxQps: 20);
        tenant.Service.RecordSpend(tenant.Info.KeyId, 1m);
        using var client = tenant.Factory.CreateClient(tenant.PlaintextKey);

        using var response = await PostChatAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.True(int.Parse(retryAfter.Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("tenant-test", body, StringComparison.Ordinal);
        Assert.DoesNotContain(tenant.PlaintextKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAdminEndpoint_TenantKeyIsRejected()
    {
        using var tenant = CreateTenantKeyFixture();
        using var client = tenant.Factory.CreateClient(tenant.PlaintextKey);

        using var response = await client.GetAsync("/api/dashboard/metrics");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_GlobalProxyKeyRemainsCompatible_AndDoesNotConsumeTenantQps()
    {
        using var tenant = CreateTenantKeyFixture(dailyBudgetUsd: 0m, maxQps: 1);
        using var globalClient = tenant.Factory.CreateClient(TestWebApplicationFactory.TestProxyApiKey);
        using var tenantClient = tenant.Factory.CreateClient(tenant.PlaintextKey);

        using var globalFirst = await PostChatAsync(globalClient);
        using var globalSecond = await PostChatAsync(globalClient);
        Assert.Equal(0m, Assert.Single(tenant.Service.GetAllKeys()).DailySpendUsd);

        using var tenantResponse = await PostChatAsync(tenantClient);

        Assert.Equal(HttpStatusCode.OK, globalFirst.StatusCode);
        Assert.Equal(HttpStatusCode.OK, globalSecond.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tenantResponse.StatusCode);
        Assert.True(Assert.Single(tenant.Service.GetAllKeys()).DailySpendUsd > 0m);
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
        using var unauthorizedContent = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var unauthorized = await unauthorizedClient.PostAsync("/v1/chat/completions", unauthorizedContent);
        using var firstContent = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var first = await authorizedClient.PostAsync("/v1/chat/completions", firstContent);
        using var secondContent = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var second = await authorizedClient.PostAsync("/v1/chat/completions", secondContent);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(1, attempts);
        using var document = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("application/json", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal("rate_limit_error", error.GetProperty("type").GetString());
        Assert.Equal("RATE_LIMIT_EXCEEDED", error.GetProperty("code").GetString());
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
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-1\",\"model\":\"model-a\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hello!\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}",
                new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 }));
        });

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: false);
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
        Assert.True(ledger.GetSpend().Total > 0, "Expected cost to be recorded after successful request.");
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
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"\",\"model\":\"\",\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}",
                new ChatUsage()));
        });

        var validRequest = BuildRequest("auto");
        var request = invalidField switch
        {
            "messages" => validRequest with { Messages = new List<ChatMessage>() },
            "role" => validRequest with { Messages = new List<ChatMessage> { new ChatMessage { Role = " " } } },
            "content-null" => validRequest with { Messages = new List<ChatMessage> { new ChatMessage { Role = "user" } } },
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
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-b\",\"model\":\"model-b\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"From B\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}",
                new ChatUsage { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 }));
        });

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: false);
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
    public async Task Post_NonStreaming_Upstream429UpdatesQuotaWithoutHealthOrThompsonFailure()
    {
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
            opt.Routing.FailoverFailureThreshold = 1;
        });
        var reset = DateTimeOffset.UtcNow.AddMinutes(1);
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (_, _) =>
            throw new ModelClientException(HttpStatusCode.TooManyRequests, "sensitive-body", metadata:
                new UpstreamResponseMetadata
                {
                    RequestsRemaining = 0,
                    RequestsResetAt = reset,
                    RetryAfterAt = reset
                }));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, (_, _) =>
            Task.FromResult(new RawChatResponse("{\"model\":\"model-b\",\"choices\":[]}", new ChatUsage())));

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        Assert.Equal(CircuitState.Closed, health.GetState("model-a"));
        Assert.False(health.GetCircuitsSnapshot().TryGetValue("model-a", out var circuit) && circuit.FailureCount > 0);
        var thompson = factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-a");
        Assert.Equal(1.0, thompson.Beta);
        var quota = factory.Services.GetRequiredService<UpstreamQuotaStateStore>().GetSnapshot("model-a");
        Assert.NotNull(quota);
        Assert.True(quota!.IsExhausted(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Post_NonStreaming_Upstream5xxStillIncrementsHealthAndThompsonFailure()
    {
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
            opt.Routing.FailoverFailureThreshold = 1;
        });
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (_, _) =>
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "down"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, (_, _) =>
            Task.FromResult(new RawChatResponse("{\"model\":\"model-b\",\"choices\":[]}", new ChatUsage())));

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CircuitState.Open, factory.Services.GetRequiredService<ModelHealthTracker>().GetState("model-a"));
        Assert.True(factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-a").Beta > 1.0);
    }

    [Fact]
    public async Task Post_Streaming_Upstream429FailsOverWithoutHealthOrThompsonFailure()
    {
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
            opt.Routing.FailoverFailureThreshold = 1;
        });
        var reset = DateTimeOffset.UtcNow.AddMinutes(1);
        factory.MockClients["model-a"] = new MockModelClient(endpointA, streamRawFunc: (_, ct) =>
            CreateFailingStream(ct, HttpStatusCode.TooManyRequests, "secret", new UpstreamResponseMetadata
            {
                RequestsRemaining = 0,
                RequestsResetAt = reset,
                RetryAfterAt = reset
            }));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, streamRawFunc: (_, ct) =>
            CreateStreamChunks("fallback", ct));

        using var client = factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(BuildRequest("auto", stream: true)), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CircuitState.Closed,
            factory.Services.GetRequiredService<ModelHealthTracker>().GetState("model-a"));
        Assert.Equal(1.0,
            factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-a").Beta);
        Assert.True(factory.Services.GetRequiredService<UpstreamQuotaStateStore>()
            .GetSnapshot("model-a")!.IsExhausted(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Post_NonStreaming_RequestRejection400_FallsBack_RecordsAuditAndBanditPenalty_WithoutCircuit()
    {
        // P0-3 回归：请求语义类 4xx（400）此前既不审计也不进 bandit，直接穿透 400，
        // 路由器对同类失败反复踩坑。现在：审计留痕 + bandit 惩罚 + 不熔断 + 降级下一候选。
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
            opt.Routing.FailoverFailureThreshold = 1;
        });
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (_, _) =>
            throw new ModelClientException(HttpStatusCode.BadRequest, "invalid tool message"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, (_, _) =>
            Task.FromResult(new RawChatResponse(
                "{\"model\":\"model-b\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"From B\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}",
                new ChatUsage { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 })));

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        // 降级成功：客户端拿到 200 + model-b 的回答
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("model-b", doc.RootElement.GetProperty("model").GetString());

        // 不熔断：400 是请求语义问题，模型对其他请求仍可用
        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        Assert.Equal(CircuitState.Closed, health.GetState("model-a"));
        Assert.False(health.GetCircuitsSnapshot().TryGetValue("model-a", out var circuit) && circuit.FailureCount > 0);

        // bandit 惩罚生效
        Assert.True(factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-a").Beta > 1.0);

        // 审计留痕：model-a 失败行（upstream-status-400）+ model-b 成功行
        var audits = factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(10);
        Assert.Contains(audits, r => r.Model == "model-a" && !r.Success && r.ErrorMessage == "upstream-status-400");
        Assert.Contains(audits, r => r.Model == "model-b" && r.Success);
    }

    [Fact]
    public async Task Post_NonStreaming_RequestRejection400_NoOtherCandidate_Propagates400_AndRecordsAudit()
    {
        // 单候选 400：保持透传语义（客户端收到原始状态码），但审计必须留痕。
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
        });
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (_, _) =>
            throw new ModelClientException(HttpStatusCode.BadRequest, "invalid tool message"));

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("UPSTREAM_REJECTION", doc.RootElement.GetProperty("error").GetProperty("code").GetString());

        var audits = factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(10);
        Assert.Contains(audits, r => r.Model == "model-a" && !r.Success && r.ErrorMessage == "upstream-status-400");
    }

    [Fact]
    public async Task Post_NoSessionHeader_DerivesStableConversationSession()
    {
        // P2-7 回归：agent 客户端（如 omp）不发 X-Session-Id，会话维度（亲和/预算/审计）全空转。
        // 现从首条 user 消息派生稳定指纹：同会话不同轮次 → 同 session；全局 key 不做租户包装。
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
        });
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (_, _) =>
            Task.FromResult(new RawChatResponse(
                "{\"model\":\"model-a\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1,\"total_tokens\":6}}",
                new ChatUsage { PromptTokens = 5, CompletionTokens = 1, TotalTokens = 6 })));

        using var client = factory.CreateClient();
        async Task SendAsync(IEnumerable<ChatMessage> messages)
        {
            var request = new ChatRequest { Model = "auto", Messages = messages.ToList() };
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/v1/chat/completions", content);
            await response.Content.ReadAsStringAsync();
        }

        // 同一会话：首条 user 相同，第二轮追加新消息
        await SendAsync([ChatMessage.FromText("user", "first task begins"), ChatMessage.FromText("assistant", "ack")]);
        await SendAsync([ChatMessage.FromText("user", "first task begins"), ChatMessage.FromText("assistant", "ack"), ChatMessage.FromText("user", "next turn")]);
        // 不同会话：首条 user 不同
        await SendAsync([ChatMessage.FromText("user", "second task starts here")]);

        var audits = factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(10);
        var sessions = audits.Where(r => r.Model == "model-a").Select(r => r.SessionId).ToList();
        Assert.Equal(3, sessions.Count);
        Assert.All(sessions, sid => Assert.StartsWith("conv-", sid));
        // 三个请求只产生两个会话指纹：first-task 两轮相同，second-task 独立
        Assert.Equal(2, sessions.Distinct().Count());
        var firstTaskSession = sessions.GroupBy(sid => sid).Single(g => g.Count() == 2).Key;
    }

    [Fact]
    public async Task Post_Streaming_RequestRejection400_FallsBackToNextCandidate()
    {
        // 流式路径的 P0-3 对称实现：首 chunk 前 400 → 降级下一候选，不熔断，bandit 惩罚。
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
            opt.Routing.FailoverFailureThreshold = 1;
        });
        factory.MockClients["model-a"] = new MockModelClient(endpointA, streamRawFunc: (_, ct) =>
            CreateFailingStream(ct, HttpStatusCode.BadRequest, "invalid tool message"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, streamRawFunc: (_, ct) =>
            CreateStreamChunks("fallback", ct));

        using var client = factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(BuildRequest("auto", stream: true)), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = factory.Services.GetRequiredService<ModelHealthTracker>();
        Assert.Equal(CircuitState.Closed, health.GetState("model-a"));
        Assert.True(factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-a").Beta > 1.0);
        var audits = factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(10);
        Assert.Contains(audits, r => r.Model == "model-a" && !r.Success && r.ErrorMessage == "upstream-status-400");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task Post_RaceFailure_Only5xxUpdatesHealthAndThompson(
        HttpStatusCode statusCode,
        bool expectHealthFailure)
    {
        using var factory = new TestWebApplicationFactory();
        var endpointA = CreateEndpoint("model-a");
        var endpointB = CreateEndpoint("model-b");
        factory.ConfigureTestServicesAction = services => services.Configure<RouterOptions>(opt =>
        {
            opt.Models.Clear();
            opt.Models.Add(endpointA);
            opt.Models.Add(endpointB);
            opt.Routing.EnableRuleClassifier = false;
            opt.Routing.EnableTokenEstimator = false;
            opt.Routing.EnableBudgetGuard = false;
            opt.Routing.EnableFailover = true;
            opt.Routing.EnableFusionMode = true;
            opt.Routing.FusionMaxParallel = 2;
            opt.Routing.FailoverFailureThreshold = 1;
        });
        var reset = DateTimeOffset.UtcNow.AddMinutes(1);
        factory.MockClients["model-a"] = new MockModelClient(endpointA, (_, _) =>
            throw new ModelClientException(statusCode, "secret", metadata: statusCode == HttpStatusCode.TooManyRequests
                ? new UpstreamResponseMetadata { RequestsRemaining = 0, RequestsResetAt = reset }
                : null));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, async (_, ct) =>
        {
            await Task.Delay(50, ct);
            return new RawChatResponse("{\"model\":\"model-b\",\"choices\":[]}", new ChatUsage());
        });

        using var client = factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectHealthFailure ? CircuitState.Open : CircuitState.Closed,
            factory.Services.GetRequiredService<ModelHealthTracker>().GetState("model-a"));
        Assert.Equal(expectHealthFailure,
            factory.Services.GetRequiredService<ThompsonStateStore>().GetOrAdd("model-a").Beta > 1.0);
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
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-medium\",\"model\":\"medium-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}",
                new ChatUsage()));
        });

        using var client = factory.CreateClient();
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "public class Example {}") }
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
    public async Task Post_NonStreaming_Upstream4xx_FallsBackToNextCandidateWithoutCircuitOrBodyLeak()
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
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"\",\"model\":\"\",\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}",
                new ChatUsage()));
        });

        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(BuildRequest("auto")), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert：P0-3 新契约——请求语义类 4xx 有其他候选时降级（上游校验阶段拒绝、无生成费用），
        // 客户端拿到 200；上游敏感错误体不泄漏；模型不熔断（对其他请求仍可用）。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fallbackAttempts);
        Assert.False(factory.Services.GetRequiredService<ModelHealthTracker>().IsCoolingDown("model-a"));
        var audits = factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(10);
        Assert.Contains(audits, r => r.Model == "model-a" && !r.Success && r.ErrorMessage == "upstream-status-422");
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
        var request = BuildRequest("auto", stream: false);
        var json = JsonSerializer.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("All model candidates failed", body);
        Assert.Contains("model-a, model-b", body);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("all_candidates_failed", error.GetProperty("type").GetString());
        Assert.Equal("ALL_CANDIDATES_FAILED", error.GetProperty("code").GetString());
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
            Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-1\",\"model\":\"strong-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0,\"total_tokens\":0}}",
                new ChatUsage())));

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
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("budget_exceeded", error.GetProperty("type").GetString());
        Assert.Equal("BUDGET_EXHAUSTED", error.GetProperty("code").GetString());
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
        factory.MockClients["model-a"] = new MockModelClient(endpoint, streamRawFunc: (req, ct) => CreateStreamChunks("hello world", ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
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
        Assert.Equal("hello", firstDoc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        using var lastDoc = JsonDocument.Parse(dataLines[^2]);
        Assert.Equal("stop", lastDoc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(7, lastDoc.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetSpend().Total > 0, "Expected streaming cost to be recorded.");

        var audit = Assert.Single(factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(1));
        Assert.False(audit.IsEstimated);
    }

    [Fact]
    public async Task Post_Streaming_WithoutUsage_RecordsEstimatedCostAndAudit()
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
        factory.MockClients["model-a"] = new MockModelClient(endpoint,
            streamRawFunc: (req, ct) => CreateStreamChunksWithoutUsage(ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("[DONE]", body);

        var ledger = factory.Services.GetRequiredService<CostLedger>();
        Assert.True(ledger.GetSpend().Total > 0, "Expected estimated streaming cost to be recorded.");

        var audit = Assert.Single(factory.Services.GetRequiredService<IRequestAuditStore>().GetRecent(1));
        Assert.True(audit.Success);
        Assert.True(audit.IsStreaming);
        Assert.True(audit.IsEstimated);
        Assert.True(audit.Cost > 0m);
        Assert.Equal(OutcomeRecorder.EstimateInputCost(endpoint, audit.EstimatedInputTokens), audit.Cost);
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

        factory.MockClients["model-a"] = new MockModelClient(endpointA, streamRawFunc: (req, ct) =>
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "model-a failed"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, streamRawFunc: (req, ct) =>
            throw new ModelClientException(HttpStatusCode.ServiceUnavailable, "model-b failed"));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
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
        // OpenAI 兼容嵌套结构：{"error":{"message":...,"type":...,"code":...}}
        var errorObj = errorDoc.RootElement.GetProperty("error");
        Assert.Equal("all model candidates failed", errorObj.GetProperty("message").GetString());
        Assert.Equal("all_candidates_failed", errorObj.GetProperty("type").GetString());
        Assert.Equal("ALL_CANDIDATES_FAILED", errorObj.GetProperty("code").GetString());
    }

    /// <summary>
    /// 流式首 chunk 已透传后，上游中途断连：代理不硬断 500，
    /// 而是注入 OpenAI 兼容 error event（UPSTREAM_ERROR）+ [DONE] 干净终止，
    /// 客户端可机读 error.code/type 判定是否重试。
    /// </summary>
    [Fact]
    public async Task Post_Streaming_MidStreamFailure_InjectsErrorEventAndDone()
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
        // mock 先 yield 首 chunk，随后抛 IOException 模拟上游中途断连。
        factory.MockClients["model-a"] = new MockModelClient(endpoint,
            streamRawFunc: (req, ct) => CreateMidStreamFailingStream(
                new IOException("upstream connection reset mid-stream"), ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

        // Assert：HTTP 仍 200（首 chunk 前已 flush header），error 在 SSE 内。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? line;
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

        // dataLines = [首chunk, errorEvent, [DONE]] —— 证明部分内容已透传，非全程失败。
        Assert.True(dataLines.Count >= 3, "Expected first chunk + error event + [DONE]");
        Assert.Equal("[DONE]", dataLines[^1]);

        // 首 chunk 是真实透传内容。
        using var firstDoc = JsonDocument.Parse(dataLines[0]);
        Assert.Equal("partial", firstDoc.RootElement.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString());

        // error event：上游断连 → UPSTREAM_ERROR（可重试类别），OpenAI 嵌套结构。
        using var errorDoc = JsonDocument.Parse(dataLines[^2]);
        var errorObj = errorDoc.RootElement.GetProperty("error");
        Assert.Contains("upstream connection reset", errorObj.GetProperty("message").GetString());
        Assert.Equal("upstream_error", errorObj.GetProperty("type").GetString());
        Assert.Equal("UPSTREAM_ERROR", errorObj.GetProperty("code").GetString());
    }

    /// <summary>
    /// 流式中途超时（HttpClient 内部 timeout，非外部 ct 取消）→ TIMEOUT（可重试类别）。
    /// </summary>
    [Fact]
    public async Task Post_Streaming_MidStreamTimeout_InjectsTimeoutCode()
    {
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
        factory.MockClients["model-a"] = new MockModelClient(endpoint,
            streamRawFunc: (req, ct) => CreateMidStreamFailingStream(
                new OperationCanceledException("upstream read timed out"), ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line.Substring("data: ".Length).Trim();
            if (data == "[DONE]") { dataLines.Add("[DONE]"); break; }
            dataLines.Add(data);
        }

        Assert.True(dataLines.Count >= 2);
        Assert.Equal("[DONE]", dataLines[^1]);
        using var errorDoc = JsonDocument.Parse(dataLines[^2]);
        var errorObj = errorDoc.RootElement.GetProperty("error");
        Assert.Contains("timed out", errorObj.GetProperty("message").GetString());
        Assert.Equal("timeout", errorObj.GetProperty("type").GetString());
        Assert.Equal("TIMEOUT", errorObj.GetProperty("code").GetString());
    }

    /// <summary>
    /// 流式超出 MaxResponseStreamBytes → RESPONSE_TOO_LARGE（不可重试类别）。
    /// </summary>
    [Fact]
    public async Task Post_Streaming_MidStreamSizeLimit_InjectsResponseTooLargeCode()
    {
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
        factory.MockClients["model-a"] = new MockModelClient(endpoint,
            streamRawFunc: (req, ct) => CreateMidStreamFailingStream(
                new ResponseSizeLimitExceededException(110, "Response size limit exceeded (110 bytes)."), ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line.Substring("data: ".Length).Trim();
            if (data == "[DONE]") { dataLines.Add("[DONE]"); break; }
            dataLines.Add(data);
        }

        Assert.True(dataLines.Count >= 2);
        Assert.Equal("[DONE]", dataLines[^1]);
        using var errorDoc = JsonDocument.Parse(dataLines[^2]);
        var errorObj = errorDoc.RootElement.GetProperty("error");
        Assert.Contains("Response size limit exceeded", errorObj.GetProperty("message").GetString());
        Assert.Equal("response_too_large", errorObj.GetProperty("type").GetString());
        Assert.Equal("RESPONSE_TOO_LARGE", errorObj.GetProperty("code").GetString());
    }

    /// <summary>
    /// 流式中途抛通用 InvalidOperationException（代理真内部 bug，非 size limit）→ INTERNAL_ERROR，
    /// 不再误标 RESPONSE_TOO_LARGE（专用 ResponseSizeLimitExceededException 才归类为 size limit）。
    /// </summary>
    [Fact]
    public async Task Post_Streaming_MidStreamGenericInvalidOperation_InjectsInternalErrorCode()
    {
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
        factory.MockClients["model-a"] = new MockModelClient(endpoint,
            streamRawFunc: (req, ct) => CreateMidStreamFailingStream(
                new InvalidOperationException("unexpected internal state"), ct));

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
        var json = JsonSerializer.Serialize(request);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line.Substring("data: ".Length).Trim();
            if (data == "[DONE]") { dataLines.Add("[DONE]"); break; }
            dataLines.Add(data);
        }

        Assert.True(dataLines.Count >= 2);
        Assert.Equal("[DONE]", dataLines[^1]);
        using var errorDoc = JsonDocument.Parse(dataLines[^2]);
        var errorObj = errorDoc.RootElement.GetProperty("error");
        Assert.Equal("server_error", errorObj.GetProperty("type").GetString());
        Assert.Equal("INTERNAL_ERROR", errorObj.GetProperty("code").GetString());
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

        factory.MockClients["strong-model"] = new MockModelClient(strongEndpoint, streamRawFunc: (req, ct) =>
        {
            strongAttempts++;
            return CreateFailingStream(ct);
        });
        factory.MockClients["cheap-model"] = new MockModelClient(cheapEndpoint, streamRawFunc: (req, ct) =>
        {
            cheapAttempts++;
            return CreateStreamChunks("cheap response", ct);
        });

        using var client = factory.CreateClient();
        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "public class Example {}") },
            Stream = true
        };
        using var httpContent = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = httpContent };

        // Act
        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"content\":\"cheap\"", body);
        Assert.Equal(1, strongAttempts);
        Assert.Equal(1, cheapAttempts);
    }

    [Fact]
    public async Task Post_Streaming_Upstream4xxBeforeFirstChunk_FallsBackToNextCandidateWithoutCircuitOrBodyLeak()
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
        factory.MockClients["model-a"] = new MockModelClient(endpointA, streamRawFunc: (req, ct) =>
            CreateFailingStream(ct, HttpStatusCode.UnprocessableEntity, "sensitive upstream body"));
        factory.MockClients["model-b"] = new MockModelClient(endpointB, streamRawFunc: (req, ct) =>
        {
            fallbackAttempts++;
            return CreateStreamChunks("unexpected fallback", ct);
        });

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: true);
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PostAsync("/v1/chat/completions", content);

        // Assert：P0-3 新契约——流式首 chunk 前 4xx 同样降级下一候选，敏感体不泄漏，不熔断。
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, fallbackAttempts);
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
                    BaseUrl = "https://api.example.com",
                    Tier = ModelTier.Strong,
                    MaxContextTokens = 8192,
                    InputPricePerMillion = 5m,
                    OutputPricePerMillion = 10m,
                    Enabled = true
                });
                opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "cheap-model",
                    BaseUrl = "https://api.example.com",
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
            Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-strong\",\"model\":\"strong-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Strong\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}",
                new ChatUsage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 })));

        factory.MockClients["cheap-model"] = new MockModelClient(cheapEndpoint, (req, ct) =>
            Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-cheap\",\"model\":\"cheap-model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Cheap\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}",
                new ChatUsage { PromptTokens = 5, CompletionTokens = 3, TotalTokens = 8 })));

        // Pre-fill ledger to exceed budget.
        var ledger = factory.Services.GetRequiredService<CostLedger>();
        ledger.Record(20m);

        using var client = factory.CreateClient();
        var request = BuildRequest("auto", stream: false);
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

    #region GET /v1/models

    [Fact]
    public async Task GetModels_RequiresApiKey_UnauthorizedWithoutIt()
    {
        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("m1"));
            });
        };

        using var client = factory.CreateClient(apiKey: null);
        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetModels_ReturnsOnlyEnabledModels_Sanitized()
    {
        var enabled = CreateEndpoint("gpt-4o");
        enabled.Tier = ModelTier.Strong;
        var disabled = CreateEndpoint("legacy");
        disabled.Enabled = false;

        using var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(enabled);
                opt.Models.Add(disabled);
            });
        };

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        var data = doc.RootElement.GetProperty("data");
        // 首位是虚拟 auto 模型，其后是启用的真实模型（禁用的 legacy 不出现）。
        Assert.Equal(2, data.GetArrayLength());

        var autoEntry = data[0];
        Assert.Equal("auto", autoEntry.GetProperty("id").GetString());
        Assert.Equal("auto", autoEntry.GetProperty("routing").GetString());

        var entry = data[1];
        Assert.Equal("example.com/gpt-4o", entry.GetProperty("id").GetString());
        Assert.Equal("model", entry.GetProperty("object").GetString());
        Assert.Equal("opti-router", entry.GetProperty("owned_by").GetString());
        Assert.Equal("strong", entry.GetProperty("tier").GetString());
        Assert.Equal("direct", entry.GetProperty("routing").GetString());
        // 显示 id 为「{供应商}/{真实模型 Id}」；upstream_id 即发往上游的 model 值。
        Assert.Equal("gpt-4o", entry.GetProperty("upstream_id").GetString());
        Assert.Equal("gpt-4o", entry.GetProperty("name").GetString());

        // ApiKey 不应出现在响应中（脱敏）
        var raw = body;
        Assert.DoesNotContain("sk-test", raw);
        Assert.DoesNotContain("api_key", raw, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Semantic cache + PII anonymization combination

    /// <summary>
    /// 敏感 PII 请求不得进入语义缓存：即使两次请求文本相同，也必须分别经过上游，
    /// 避免缓存响应绕过当前 PII 安全边界。每次上游响应仍须按当前请求还原 PII 占位符。
    /// </summary>
    [Fact]
    public async Task Post_SemanticCacheWithSensitivePii_DoesNotReuseCachedResponse()
    {
        // Arrange
        int attempts = 0;
        using var factory = CreateSemanticCachePiiFactory(() => attempts++);
        using var client = factory.CreateClient();

        var request = new ChatRequest
        {
            Model = "auto",
            Messages = new List<ChatMessage>
            {
                ChatMessage.FromText("user", "What is the balance of account 13800138000?")
            }
        };
        using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");

        // Act: 同一请求发送两次
        var first = await client.PostAsync("/v1/chat/completions", content);
        var second = await client.PostAsync("/v1/chat/completions", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // 敏感 PII 请求完全禁用语义缓存 → 两次请求均调用上游。
        Assert.Equal(2, attempts);

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        // 首次请求：上游占位符响应被还原为真实 PII
        Assert.Contains("13800138000", firstBody);
        // 第二次请求同样还原为当前请求的 PII，而非泄漏占位符
        Assert.Contains("13800138000", secondBody);
        Assert.DoesNotContain("PII_PHONE", secondBody);
    }

    private static TestWebApplicationFactory CreateSemanticCachePiiFactory(Action? onUpstream = null)
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
                // 关闭精确响应缓存，隔离出语义缓存路径（否则精确缓存先命中，测试失去区分度）
                opt.Routing.EnableResponseCache = false;
                opt.Routing.EnableSemanticCache = true;
                opt.Routing.EnablePiiAnonymization = true;
            });
        };
        factory.MockClients["model-a"] = new MockModelClient(endpoint, (req, ct) =>
        {
            onUpstream?.Invoke();
            // 上游响应引用脱敏占位符（模拟模型回显占位符的场景）
            return Task.FromResult(new RawChatResponse(
                "{\"id\":\"chatcmpl-sem\",\"model\":\"model-a\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Balance for [PII_PHONE_1] is $42.00\"}}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}",
                new ChatUsage { PromptTokens = 3, CompletionTokens = 2, TotalTokens = 5 }));
        });
        return factory;
    }

    #endregion
}

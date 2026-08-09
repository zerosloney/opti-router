using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

internal sealed class M2MockModelClient : IModelClient
{
    private readonly Func<ChatRequest, CancellationToken, Task<RawChatResponse>> _completeRawFunc;

    public ModelEndpointOptions Endpoint { get; }

    public M2MockModelClient(ModelEndpointOptions endpoint, Func<ChatRequest, CancellationToken, Task<RawChatResponse>> completeRawFunc)
    {
        Endpoint = endpoint;
        _completeRawFunc = completeRawFunc;
    }

    public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        return _completeRawFunc(request, cancellationToken);
    }

    public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Clients.ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<Clients.ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelHealthResult(true, 0));
}

internal sealed class M2ModelClientProvider : IModelClientProvider
{
    private readonly IModelClient _client;

    public M2ModelClientProvider(IModelClient client)
    {
        _client = client;
    }

    public IModelClient GetClient(ModelEndpointOptions endpoint) => _client;
}

internal sealed class M2WebApplicationFactory : WebApplicationFactory<Program>
{
    public int RequestsPerMinute { get; set; } = 60;
    public int MaxConcurrentRequestsPerPartition { get; set; } = 100;
    public bool TrustProxyHeaders { get; set; } = false;
    public Func<ChatRequest, CancellationToken, Task<RawChatResponse>>? OnCompleteRaw { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((context, config) =>
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                ["OptiRouter:ProxyApiKey"] = "m2-test-key",
                ["OptiRouter:RequestsPerMinute"] = RequestsPerMinute.ToString(),
                ["OptiRouter:MaxConcurrentRequestsPerPartition"] = MaxConcurrentRequestsPerPartition.ToString(),
                ["OptiRouter:TrustProxyHeaders"] = TrustProxyHeaders.ToString(),
                ["OptiRouter:Models:0:Name"] = "gpt-4o",
                ["OptiRouter:Models:0:BaseUrl"] = "http://localhost/v1",
                ["OptiRouter:Models:0:ApiKey"] = "sk-xxx",
                ["OptiRouter:Models:0:Tier"] = "Strong",
                ["OptiRouter:Models:0:PricePerMillionInputTokens"] = "10.0",
                ["OptiRouter:Models:0:PricePerMillionOutputTokens"] = "30.0",
                ["OptiRouter:Models:0:MaxContextTokens"] = "8192",
                ["OptiRouter:Models:0:Enabled"] = "true"
            };
            config.AddInMemoryCollection(inMemoryConfig);
        });

        builder.ConfigureServices(services =>
        {
            // Remove existing ModelClientProvider and register M2ModelClientProvider with mock
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IModelClientProvider));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IModelClientProvider>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
                var endpoint = options.Models[0];
                var mockClient = new M2MockModelClient(endpoint, OnCompleteRaw ?? ((_, _) =>
                    Task.FromResult(new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello\"}}]}", null))));
                return new M2ModelClientProvider(mockClient);
            });
        });
    }
}

public class M2EndpointTests
{
    [Fact]
    public async Task Post_WithClientIP_IsolatesRateLimitPartitions()
    {
        // Arrange
        using var factory = new M2WebApplicationFactory { RequestsPerMinute = 1, TrustProxyHeaders = true };
        using var client = factory.CreateClient();

        var requestContent = new StringContent(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}",
            Encoding.UTF8,
            "application/json");

        // 1. Send first request with Client IP A (Succeeds)
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = requestContent };
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req1.Headers.Add("X-Forwarded-For", "1.1.1.1");
        var resp1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // 2. Send request with Client IP B (Succeeds because partition is isolated)
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}", Encoding.UTF8, "application/json")
        };
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req2.Headers.Add("X-Forwarded-For", "2.2.2.2");
        var resp2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        // 3. Send second request with Client IP A (Fails with 429 because its partition is exhausted)
        var req3 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}", Encoding.UTF8, "application/json")
        };
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req3.Headers.Add("X-Forwarded-For", "1.1.1.1");
        var resp3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp3.StatusCode);
    }

    [Fact]
    public async Task Post_DefaultOffXForwardedFor_IsIgnored_SoPartitionsNotSpoofed()
    {
        // Arrange: TrustProxyHeaders 默认 false——伪造 X-Forwarded-For 不得隔离限流分区。
        // 否则攻击者每请求改一次头即可绕过限流（安全回归）。
        using var factory = new M2WebApplicationFactory { RequestsPerMinute = 1 };
        using var client = factory.CreateClient();

        var content = () => new StringContent(
            "{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}",
            Encoding.UTF8,
            "application/json");

        // 1. 首请求携带伪造 XFF，成功（占满单分区配额）。
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content() };
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req1.Headers.Add("X-Forwarded-For", "9.9.9.9");
        var resp1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // 2. 不同伪造 XFF 也必须落在同一分区 → 429（证明头被忽略）。
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content() };
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req2.Headers.Add("X-Forwarded-For", "8.8.8.8");
        var resp2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp2.StatusCode);
    }

    [Fact]
    public async Task Post_WithSessionId_IsolatesRateLimitPartitions()
    {
        // Arrange
        using var factory = new M2WebApplicationFactory { RequestsPerMinute = 1 };
        using var client = factory.CreateClient();

        // 1. Send request with Session A (Succeeds)
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}", Encoding.UTF8, "application/json")
        };
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req1.Headers.Add("X-Session-Id", "session-a");
        var resp1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // 2. Send request with Session B (Succeeds)
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}", Encoding.UTF8, "application/json")
        };
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req2.Headers.Add("X-Session-Id", "session-b");
        var resp2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        // 3. Send second request with Session A (Fails with 429)
        var req3 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}", Encoding.UTF8, "application/json")
        };
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req3.Headers.Add("X-Session-Id", "session-a");
        var resp3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp3.StatusCode);
    }

    [Fact]
    public async Task Post_ExceedingConcurrencyLimit_ReturnsTooManyRequests()
    {
        // Arrange
        var tcs = new TaskCompletionSource<RawChatResponse>();
        using var factory = new M2WebApplicationFactory
        {
            MaxConcurrentRequestsPerPartition = 1,
            OnCompleteRaw = async (_, ct) => await tcs.Task
        };
        using var client = factory.CreateClient();

        // 用唯一 session key 隔离静态 ConcurrencyRegistry 跨测试复用：
        // Post_WithSessionId 也用 session-a，其 factory 默认 max=100，若复用其信号量则本测试 max=1 配置被忽略。
        var sessionKey = "session-concurrent-" + Guid.NewGuid().ToString("N");

        // Request content
        var payload = "{\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";

        // 1. Start first request (blocks on tcs.Task)
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req1.Headers.Add("X-Session-Id", sessionKey);
        var task1 = client.SendAsync(req1);

        // Give it a brief moment to enter the middleware and wait
        await Task.Delay(100);

        // 2. Send second request under same partition. Should fail immediately with 429 (Concurrency Exceeded).
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "m2-test-key");
        req2.Headers.Add("X-Session-Id", sessionKey);
        var resp2 = await client.SendAsync(req2);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp2.StatusCode);
        var body2 = await resp2.Content.ReadAsStringAsync();
        Assert.Contains("Too many concurrent requests", body2);

        // 3. Clean up: complete the blocked first request so it can exit safely
        tcs.SetResult(new RawChatResponse("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hi\"}}]}", null));
        var resp1 = await task1;
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
    }
}

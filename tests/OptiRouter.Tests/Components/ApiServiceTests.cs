using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using OptiRouter.Components.Services;

namespace OptiRouter.Tests.Components;

public class ApiServiceTests
{
    [Fact]
    public async Task GetMetricsAsync_MapsRoutingPolicyAndNumericModelTier()
    {
        const string json = """
            {
              "system": {
                "time": "2026-08-09T00:00:00Z",
                "routingPolicy": { "enableFailover": true },
                "budget": { "dailyBudgetUsd": 10, "usePersistentStore": false, "dailySpend": 1, "totalSpend": 2 },
                "qps": 0, "totalRequests": 0, "totalTokens": 0, "avgLatencyMs": 0, "alerts": []
              },
              "models": [{
                "name": "strong", "baseUrl": "https://example.com", "tier": 0,
                "inputPricePerMillion": 1, "outputPricePerMillion": 2,
                "maxContextTokens": 1000, "enabled": true, "tags": [],
                "circuitState": "Closed", "failureCount": 0, "activeProbes": 0,
                "avgLatencyMs": null, "latencySamples": 0
              }]
            }
            """;
        using var http = new HttpClient(new StaticJsonHandler(json));
        var service = new ApiService(http, new TestNavigationManager("http://localhost/dashboard"));

        var metrics = await service.GetMetricsAsync();

        Assert.NotNull(metrics);
        Assert.True(metrics.System.Routing.EnableFailover);
        Assert.Equal(OptiRouter.Configuration.ModelTier.Strong, Assert.Single(metrics.Models).Tier);
    }

    [Fact]
    public async Task DashboardRequests_NoKeyAppended()
    {
        // 管理端鉴权改走登录会话 Cookie：请求 URL 不再附加 ?key=，保持路径原样。
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var navigation = new TestNavigationManager("http://localhost/dashboard?key=admin%20key");
        var service = new ApiService(http, navigation);

        await service.GetTrendsAsync(30);
        await service.GetAuditLogAsync(limit: 25, offset: 50, model: "model/a");

        Assert.Collection(handler.RequestUris,
            uri => Assert.Equal("/api/dashboard/trends?days=30", uri.PathAndQuery),
            uri => Assert.Equal("/api/dashboard/requests?limit=25&offset=50&model=model%2Fa", uri.PathAndQuery));
    }

    [Fact]
    public async Task ModelCrud_UsesQueryRoutesAndEscapesName()
    {
        var handler = new ModelCrudRecordingHandler();
        using var http = new HttpClient(handler);
        var service = new ApiService(http, new TestNavigationManager("http://localhost/models"));
        const string name = "provider/model name?variant=1&region=cn";

        var update = await service.UpdateModelAsync(name,
            new ApiService.UpdateModelRequest(null, null, null, null, null, null, null, null, null));
        var delete = await service.DeleteModelAsync(name);
        var test = await service.TestModelConnectionAsync(name);

        Assert.True(update.Ok);
        Assert.True(delete.Ok);
        Assert.NotNull(test);
        Assert.True(test.Success);
        Assert.Collection(handler.Requests,
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/models", request.Uri.AbsolutePath);
                Assert.Equal("?name=" + Uri.EscapeDataString(name), request.Uri.Query);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Delete, request.Method);
                Assert.Equal("/api/models", request.Uri.AbsolutePath);
                Assert.Equal("?name=" + Uri.EscapeDataString(name), request.Uri.Query);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/models/test", request.Uri.AbsolutePath);
                Assert.Equal("?name=" + Uri.EscapeDataString(name), request.Uri.Query);
            });
    }

    [Fact]
    public async Task UpdateSystemConfig_SendsExpectedVersionAndReturnsNewVersion()
    {
        var handler = new ConfigUpdateHandler();
        using var http = new HttpClient(handler);
        var service = new ApiService(http, new TestNavigationManager("http://localhost/router"));

        var (ok, error, version) = await service.UpdateSystemConfigAsync(new ApiService.UpdateSystemConfigRequest
        {
            ExpectedVersion = "version-1",
            EnableFailover = false
        });

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("version-2", version);
        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("version-1", request.RootElement.GetProperty("expectedVersion").GetString());
        Assert.False(request.RootElement.GetProperty("enableFailover").GetBoolean());
    }

    [Fact]
    public async Task RunEvalBenchmark_CancellationPropagatesAndIsNotReturnedAsError()
    {
        var handler = new CancellationHandler();
        using var http = new HttpClient(handler);
        var service = new ApiService(http, new TestNavigationManager("http://localhost/router"));
        using var cts = new CancellationTokenSource();

        var run = service.RunEvalBenchmarkAsync(null, cts.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(handler.ObservedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Mutation_NonSuccess_ReturnsErrorFromBody()
    {
        // M9 回归：变更类方法的失败必须带回后端 {"error":"..."} 校验消息供 UI 展示。
        using var http = new HttpClient(new ErrorJsonHandler(
            HttpStatusCode.NotFound, "{\"error\":\"Client key 'k1' not found.\"}"));
        var service = new ApiService(http, new TestNavigationManager("http://localhost/keys"));

        var (ok, error) = await service.UpdateClientKeyAsync("k1", enabled: false, dailyBudgetUsd: 10m, maxQps: 5);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("not found", error);
    }

    [Fact]
    public async Task Mutation_NonJsonError_FallsBackToStatusCode()
    {
        // 非 JSON 错误体（如网关 502 HTML）：回退为 HTTP 状态码文本，不抛异常。
        using var http = new HttpClient(new ErrorJsonHandler(
            HttpStatusCode.BadGateway, "<html>Bad Gateway</html>"));
        var service = new ApiService(http, new TestNavigationManager("http://localhost/keys"));

        var (ok, error) = await service.DeleteClientKeyAsync("k1");

        Assert.False(ok);
        Assert.Equal("HTTP 502", error);
    }

    [Fact]
    public async Task RunSandbox_NonSuccess_ReturnsErrorFromBody()
    {
        using var http = new HttpClient(new ErrorJsonHandler(
            HttpStatusCode.BadRequest, "{\"error\":\"Prompt cannot be empty.\"}"));
        var service = new ApiService(http, new TestNavigationManager("http://localhost/router"));

        var (result, error) = await service.RunSandboxRouteAsync("");

        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("Prompt cannot be empty", error);
    }

    [Fact]
    public async Task Cookie_IsCapturedPerCircuitInstance_NotShared()
    {
        // 回归：DelegatingHandler 管道被 HttpClientFactory 跨 circuit 缓存共享，多管理员 Cookie 会串会话；
        // 现在 Cookie 捕获在 Scoped 的 ApiService 实例上，按管理员会话隔离。
        var handlerA = new RecordingCookieHandler();
        var handlerB = new RecordingCookieHandler();
        using var httpA = new HttpClient(handlerA);
        using var httpB = new HttpClient(handlerB);

        var serviceA = new ApiService(httpA, new TestNavigationManager("http://localhost/dashboard"),
            AccessorWithCookie("OptiRouter.Admin=alice"));
        var serviceB = new ApiService(httpB, new TestNavigationManager("http://localhost/dashboard"),
            AccessorWithCookie("OptiRouter.Admin=bob"));

        await serviceA.GetTrendsAsync();
        await serviceB.GetTrendsAsync();

        Assert.Equal("OptiRouter.Admin=alice", handlerA.LastCookie);
        Assert.Equal("OptiRouter.Admin=bob", handlerB.LastCookie);
    }

    [Fact]
    public async Task Cookie_FallsBackToCapturedValueWhenHttpContextUnavailable()
    {
        // circuit 交互阶段 HttpContext 为 null：回退用构造时捕获的 Cookie。
        var handler = new RecordingCookieHandler();
        using var http = new HttpClient(handler);
        var accessor = new MutableHttpContextAccessor
        {
            HttpContext = AccessorWithCookie("OptiRouter.Admin=carol").HttpContext
        };
        var service = new ApiService(http, new TestNavigationManager("http://localhost/dashboard"), accessor);

        accessor.HttpContext = null; // 模拟 circuit 交互阶段
        await service.GetTrendsAsync();

        Assert.Equal("OptiRouter.Admin=carol", handler.LastCookie);
    }

    [Fact]
    public async Task Unauthorized_RedirectsToLoginOncePerCircuit()
    {
        using var http = new HttpClient(new ErrorJsonHandler(
            HttpStatusCode.Unauthorized, "{\"error\":\"session expired\"}"));
        var navigation = new TestNavigationManager("http://localhost/dashboard");
        var service = new ApiService(http, navigation);

        await service.GetQuotaStateAsync(); // 401 → 跳转登录页（GetQuotaStateAsync 吞异常返回空列表）
        await service.GetQuotaStateAsync(); // 第二次 401 不再重复整页跳转

        Assert.Equal(1, navigation.NavigateCount);
        Assert.Equal("http://localhost/login", navigation.Uri);
    }

    private sealed class RecordingCookieHandler : HttpMessageHandler
    {
        public string? LastCookie { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastCookie = request.Headers.TryGetValues("Cookie", out var values)
                ? string.Join("; ", values)
                : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static IHttpContextAccessor AccessorWithCookie(string cookie)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Cookie"] = cookie;
        return new MutableHttpContextAccessor { HttpContext = context };
    }

    private sealed class MutableHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class ErrorJsonHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            string body = request.RequestUri!.AbsolutePath.EndsWith("/trends", StringComparison.Ordinal)
                ? "[]"
                : "{\"items\":[],\"totalCount\":0}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ModelCrudRecordingHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!));
            string body = request.RequestUri!.AbsolutePath.EndsWith("/test", StringComparison.Ordinal)
                ? "{\"success\":true,\"latencyMs\":1,\"message\":\"ok\",\"error\":null}"
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ConfigUpdateHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"version\":\"version-2\"}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            RequestStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("CancellationHandler should not return a response.");
        }
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager(string uri)
        {
            Initialize("http://localhost/", uri);
        }

        public int NavigateCount { get; private set; }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            NavigateCount++;
            Uri = ToAbsoluteUri(uri).ToString();
        }
    }
}

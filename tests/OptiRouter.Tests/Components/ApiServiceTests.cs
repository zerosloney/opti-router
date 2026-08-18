using System.Net;
using Microsoft.AspNetCore.Components;
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
        var service = new ApiService(http, new TestNavigationManager("http://localhost/dashboard"), httpContextAccessor: null);

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
        var service = new ApiService(http, navigation, httpContextAccessor: null);

        await service.GetTrendsAsync(30);
        await service.GetAuditLogAsync(limit: 25, offset: 50, model: "model/a");

        Assert.Collection(handler.RequestUris,
            uri => Assert.Equal("/api/dashboard/trends?days=30", uri.PathAndQuery),
            uri => Assert.Equal("/api/dashboard/requests?limit=25&offset=50&model=model%2Fa", uri.PathAndQuery));
    }

    [Fact]
    public async Task Mutation_NonSuccess_ReturnsErrorFromBody()
    {
        // M9 回归：变更类方法的失败必须带回后端 {"error":"..."} 校验消息供 UI 展示。
        using var http = new HttpClient(new ErrorJsonHandler(
            HttpStatusCode.NotFound, "{\"error\":\"Client key 'k1' not found.\"}"));
        var service = new ApiService(http, new TestNavigationManager("http://localhost/keys"), httpContextAccessor: null);

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
        var service = new ApiService(http, new TestNavigationManager("http://localhost/keys"), httpContextAccessor: null);

        var (ok, error) = await service.DeleteClientKeyAsync("k1");

        Assert.False(ok);
        Assert.Equal("HTTP 502", error);
    }

    [Fact]
    public async Task RunSandbox_NonSuccess_ReturnsErrorFromBody()
    {
        using var http = new HttpClient(new ErrorJsonHandler(
            HttpStatusCode.BadRequest, "{\"error\":\"Prompt cannot be empty.\"}"));
        var service = new ApiService(http, new TestNavigationManager("http://localhost/router"), httpContextAccessor: null);

        var (result, error) = await service.RunSandboxRouteAsync("");

        Assert.Null(result);
        Assert.NotNull(error);
        Assert.Contains("Prompt cannot be empty", error);
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

        protected override void NavigateToCore(string uri, bool forceLoad)
            => Uri = ToAbsoluteUri(uri).ToString();
    }
}

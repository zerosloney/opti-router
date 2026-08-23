using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// Metrics 端点鉴权测试。
/// </summary>
public sealed class MetricsEndpointAuthTests
{
    private sealed class MetricsWebApplicationFactory : WebApplicationFactory<Program>
    {
        public string? MetricsApiKey { get; set; } = null;
        public bool EnableMetrics { get; set; } = true;
        public string MetricsEndpointPath { get; set; } = "/metrics";
        public string? AdminApiKey { get; set; }

        // 封闭测试：配置库指向临时文件（空库），模型经 Configure<RouterOptions> 注入——
        // 否则 models 权威来自配置库，会依赖运行环境遗留 DB 状态（src/data 或 bin/data）。
        private readonly string _tempDbPath = Path.Combine(
            Path.GetTempPath(), $"optirouter-metrics-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ConfigDbPath", _tempDbPath);
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");

            builder.ConfigureAppConfiguration((context, config) =>
            {
                var inMemoryConfig = new Dictionary<string, string?>
                {
                    ["OptiRouter:ProxyApiKey"] = "test-proxy-key",
                    ["OptiRouter:Routing:EnableMetrics"] = EnableMetrics.ToString(),
                    ["OptiRouter:Routing:MetricsEndpointPath"] = MetricsEndpointPath
                };

                if (MetricsApiKey is not null)
                {
                    inMemoryConfig["OptiRouter:Routing:MetricsApiKey"] = MetricsApiKey;
                }
                if (AdminApiKey is not null)
                {
                    inMemoryConfig["OptiRouter:AdminApiKey"] = AdminApiKey;
                }

                config.AddInMemoryCollection(inMemoryConfig);
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "gpt-4o",
                        BaseUrl = "https://api.openai.com/v1",
                        ApiKey = "sk-test",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    });
                    opt.Routing.EnableHealthProbe = false;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try { File.Delete(_tempDbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task AdminApi_BearerBruteForce_LocksIp_EvenValidKeyRejected()
    {
        // 管理 API 直连 Bearer 的失败尝试与 /login 共享 IP 锁定窗口（此前完全无阻）。
        using var factory = new MetricsWebApplicationFactory
        {
            MetricsApiKey = null,
            EnableMetrics = true,
            AdminApiKey = "admin-key-123"
        };
        using var client = factory.CreateClient();

        // 5 次无效 Bearer（默认阈值）→ IP 进入锁定窗口
        for (int i = 0; i < 5; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/config");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"wrong-key-{i}");
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 锁定期间正确 Key 也被拒（与 /login 同语义：IsLocked 先于校验）
        using var validReq = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/config");
        validReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "admin-key-123");
        using var validResp = await client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.Unauthorized, validResp.StatusCode);

        // 匿名页面请求（无 Bearer）不计数、不受锁定影响：仍正常 302 到登录页
        using var pageClient = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var pageResp = await pageClient.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, pageResp.StatusCode);
    }

    [Fact]
    public async Task UnmappedProxyPath_Unauthenticated_Returns401NotLoginRedirect()
    {
        // 未映射的 /v1/* 落到兜底页（带 [Authorize]）；UseAuthorization 必须晚于自定义代理鉴权中间件，
        // 否则 API 客户端拿到 302 登录页重定向而非 401 协议错误。
        using var factory = new MetricsWebApplicationFactory { EnableMetrics = false, AdminApiKey = "admin-key-123" };
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/v1/not-a-real-endpoint");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithoutApiKey_Returns200()
    {
        // Arrange
        using var factory = new MetricsWebApplicationFactory
        {
            MetricsApiKey = null,
            EnableMetrics = true
        };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metrics");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithApiKey_NoBearer_Returns401()
    {
        // Arrange
        using var factory = new MetricsWebApplicationFactory
        {
            MetricsApiKey = "secret-metrics-key",
            EnableMetrics = true
        };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metrics");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithApiKey_WrongBearer_Returns401()
    {
        // Arrange
        using var factory = new MetricsWebApplicationFactory
        {
            MetricsApiKey = "secret-metrics-key",
            EnableMetrics = true
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");

        // Act
        var response = await client.GetAsync("/metrics");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_WithApiKey_CorrectBearer_Returns200()
    {
        // Arrange
        using var factory = new MetricsWebApplicationFactory
        {
            MetricsApiKey = "secret-metrics-key",
            EnableMetrics = true
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-metrics-key");

        // Act
        var response = await client.GetAsync("/metrics");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_Disabled_DoesNotServeMetricsContent()
    {
        // Arrange：EnableMetrics=false 时 /metrics 不映射；未知路径落入 dashboard fallback（200 HTML），
        // 因此断言"非指标内容"而非 404。
        using var factory = new MetricsWebApplicationFactory
        {
            MetricsApiKey = null,
            EnableMetrics = false
        };
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metrics");
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        Assert.False(body.Contains("# HELP") && body.Contains("optirouter_"),
            "禁用指标后 /metrics 不应返回 Prometheus 指标内容");
        Assert.DoesNotContain("text/plain", contentType);
    }
}

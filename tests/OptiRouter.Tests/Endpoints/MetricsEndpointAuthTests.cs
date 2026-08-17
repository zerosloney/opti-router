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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                var inMemoryConfig = new Dictionary<string, string?>
                {
                    ["OptiRouter:ProxyApiKey"] = "test-proxy-key",
                    ["OptiRouter:Models:0:Name"] = "gpt-4o",
                    ["OptiRouter:Models:0:BaseUrl"] = "https://api.openai.com/v1",
                    ["OptiRouter:Models:0:ApiKey"] = "sk-test",
                    ["OptiRouter:Models:0:Enabled"] = "true",
                    ["OptiRouter:Routing:EnableMetrics"] = EnableMetrics.ToString(),
                    ["OptiRouter:Routing:MetricsEndpointPath"] = MetricsEndpointPath
                };

                if (MetricsApiKey is not null)
                {
                    inMemoryConfig["OptiRouter:Routing:MetricsApiKey"] = MetricsApiKey;
                }

                config.AddInMemoryCollection(inMemoryConfig);
            });
        }
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

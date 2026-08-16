using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// OpenAPI 契约文档（Swagger）端到端测试：openapi.json 与 UI 页面在管理鉴权保护下可访问。
/// </summary>
public sealed class SwaggerApiTests
{
    private const string AdminKey = "swagger-test-admin-key";

    private sealed class SwaggerFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", AdminKey);
            builder.UseSetting("OptiRouter:AdminApiKey", AdminKey);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "swagger-model",
                        BaseUrl = "https://api.example.com",
                        ApiKey = "sk-test",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    });
                    opt.Routing.EnableHealthProbe = false;
                    opt.Routing.EnableLatencyAware = false;
                });
            });
        }
    }

    [Fact]
    public async Task OpenApiJson_ExposesProxyAndAdminEndpoints()
    {
        using var factory = new SwaggerFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        var resp = await client.GetAsync("/dashboard/api-docs/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");

        // 代理端点
        Assert.True(paths.TryGetProperty("/v1/chat/completions", out _));
        Assert.True(paths.TryGetProperty("/v1/models", out _));

        // 管理端点
        Assert.True(paths.TryGetProperty("/api/dashboard/keys/usage", out _));
        Assert.True(paths.TryGetProperty("/api/dashboard/keys/usage/export", out _));

        // 文档元信息
        Assert.Equal("OptiRouter API", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal("3.0.1", doc.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task SwaggerUi_IsServedUnderDashboard()
    {
        using var factory = new SwaggerFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);

        var resp = await client.GetAsync("/dashboard/swagger/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/html", resp.Content.Headers.ContentType?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task OpenApiJson_RequiresAdminAuth()
    {
        using var factory = new SwaggerFactory();
        using var anonymous = factory.CreateClient();

        var resp = await anonymous.GetAsync("/dashboard/api-docs/v1/openapi.json");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// MCP Server 端点鉴权回归：/mcp 暴露模型清单/预算/健康等内部状态，
/// 必须纳入管理端鉴权（Bearer AdminApiKey 或登录 Cookie），
/// 未认证请求按 API 语义返回 401（不 302 登录页——MCP 客户端不认重定向）。
/// </summary>
public class McpEndpointAuthTests
{
    private static ModelEndpointOptions CreateEndpoint(string name) => new()
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

    private static TestWebApplicationFactory CreateFactory()
    {
        var factory = new TestWebApplicationFactory();
        factory.ConfigureTestServicesAction = services =>
        {
            services.Configure<RouterOptions>(opt =>
            {
                opt.Models.Clear();
                opt.Models.Add(CreateEndpoint("model-a"));
            });
        };
        return factory;
    }

    [Fact]
    public async Task Mcp_PostWithoutAuth_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(apiKey: null);

        using var content = new StringContent(
            """{"jsonrpc":"2.0","method":"tools/list","params":{},"id":1}""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/mcp", content);

        // 修复前：/mcp 不在 AdminPathPrefixes，鉴权中间件直接放行，未认证即可枚举工具。
        // 修复后：按 API 语义 401（/mcp 排除在 isPageRequest 之外，不产生 302 /login）。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_ToolsGetWithoutAuth_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(apiKey: null);

        using var response = await client.GetAsync("/mcp/tools");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_PostWithAdminBearer_ServesJsonRpc()
    {
        using var factory = CreateFactory();
        factory.AdminApiKey = "test-admin-key";
        using var client = factory.CreateClient("test-admin-key");

        using var content = new StringContent(
            """{"jsonrpc":"2.0","method":"tools/list","params":{},"id":1}""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/mcp", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("router_status", body);
    }

    [Fact]
    public async Task Mcp_PostWithWrongBearer_Returns401()
    {
        using var factory = CreateFactory();
        factory.AdminApiKey = "test-admin-key";
        using var client = factory.CreateClient("wrong-key");

        using var content = new StringContent(
            """{"jsonrpc":"2.0","method":"tools/list","params":{},"id":1}""",
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync("/mcp", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

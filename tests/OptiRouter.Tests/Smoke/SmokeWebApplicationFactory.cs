using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Tests.Smoke;

/// <summary>
/// 端到端冒烟测试专用 WebApplicationFactory，使用真实的 <see cref="ModelClientProvider"/>
/// 发出 HTTP 请求到 WireMock，不注入 mock IModelClient。
/// </summary>
internal sealed class SmokeWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestProxyApiKey = "test-proxy-key";

    /// <summary>
    /// 额外的测试服务配置回调。
    /// </summary>
    public Action<IServiceCollection>? ConfigureTestServicesAction { get; set; }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestProxyApiKey);
        return client;
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("OptiRouter:RequestsPerMinute", "60");
        // 测试用临时配置库，避免读/写真实 data/optirouter-config.db。
        builder.UseSetting("OptiRouter:ConfigDbPath",
            Path.Combine(Path.GetTempPath(), "optirouter-config-test-" + Guid.NewGuid().ToString("N") + ".db"));
        // 测试用内存账本，避免写真实 SQLite 文件。
        builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
        // 集成测试宿主监听随机端口，与常驻服务无冲突；关闭单实例守卫避免被跨进程锁误杀。
        builder.UseSetting("OptiRouter:EnableSingleInstanceGuard", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveBackgroundServices();
            services.UseFixedTenantKey("test-proxy-key");
            // 不覆盖 IModelClientProvider，让 Program.cs 中注册的真实 ModelClientProvider 生效，
            // 从而让 OpenAICompatibleModelClient 真正发出 HTTP 到 WireMock。
            ConfigureTestServicesAction?.Invoke(services);
        });
    }
}

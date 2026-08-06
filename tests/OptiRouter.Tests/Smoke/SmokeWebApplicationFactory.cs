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
    /// <summary>
    /// 额外的测试服务配置回调。
    /// </summary>
    public Action<IServiceCollection>? ConfigureTestServicesAction { get; set; }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // 不覆盖 IModelClientProvider，让 Program.cs 中注册的真实 ModelClientProvider 生效，
            // 从而让 OpenAICompatibleModelClient 真正发出 HTTP 到 WireMock。
            ConfigureTestServicesAction?.Invoke(services);
        });
    }
}

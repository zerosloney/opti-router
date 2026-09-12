using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OptiRouter.Components.Services;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using System.Threading.RateLimiting;
namespace OptiRouter.Composition;

/// <summary>
/// 管理查询/UI 的 DI 注册（#4 模块化：组合根拆分）——Swagger 契约、
/// Razor Pages、Blazor Server、SignalR 选项、Dashboard ApiService、Toast。
/// </summary>
internal static class ManagementUiServiceExtensions
{
    public static IServiceCollection AddManagementUiServices(this IServiceCollection services)
    {
// OpenAPI 契约文档（Swagger）：暴露于 /dashboard/swagger（管理鉴权保护），
// openapi.json 位于 /dashboard/api-docs/v1/openapi.json。
services.AddEndpointsApiExplorer();
services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "OptiRouter API",
        Version = "v1",
        Description = "多模型智能路由代理：OpenAI 兼容 Chat Completions、模型发现、管理与租户用量 API。" +
                      "代理端点使用 Bearer 鉴权（ProxyApiKey 或租户密钥）；管理端点见 /dashboard。"
    });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Bearer <proxy-api-key> 或租户客户端密钥"
    });
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });

    string xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    string xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

// Blazor Server：组件化 Dashboard + 模型配置 UI。
// _Host.cshtml 是 Razor Page（用 <component render-mode="ServerPrerendered">），
// 需 AddRazorPages 提供 PersistentComponentState 等预渲染服务，否则 AntiforgeryStateProvider 解析失败。
services.AddRazorPages();
services.AddServerSideBlazor();
// 后台标签页节流治理：Chrome/Edge 对后台 >5 分钟的标签页把 JS 定时器压到 1 次/分钟，
// Blazor 客户端 15s 心跳 ping 实际被拉长到 ~60s，超过默认 ClientTimeoutInterval(30s)
// 即被服务端判死掐断电路——切回 /requests 等管理台页面必现"正在重试连接"横幅。
// 放宽到 90s 容下一个完整节流周期；客户端侧 serverTimeout 不受影响（WS 消息事件
// 不被节流，服务端 ping 到达即处理），无需动前端。全局无其他 SignalR Hub，仅 Blazor 电路。
services.Configure<Microsoft.AspNetCore.SignalR.HubOptions>(o =>
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(90));
// ApiService 必须 Scoped（circuit 内共享）：Blazor Server 页面间 NavLink 导航后保持同一实例。
// Cookie 注入在 ApiService 内部按请求执行（构造时捕获 Cookie 存于 Scoped 实例、HttpContext 可用时刷新）。
// 不用 DelegatingHandler：HttpClientFactory 的 handler 管道跨 circuit 缓存共享，
// 其实例字段（Cookie 缓存/401 跳转标记）会造成多管理员会话间状态串扰。
// 300s：长评测（eval/run 走真实上游管线）可能超过 100s
services.AddHttpClient(nameof(ApiService), client => client.Timeout = TimeSpan.FromSeconds(300));
services.AddScoped<ApiService>(sp =>
{
    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ApiService));
    var nav = sp.GetRequiredService<NavigationManager>();
    return new ApiService(client, nav, sp.GetService<IHttpContextAccessor>(), sp.GetService<ILogger<ApiService>>());
});
services.AddScoped<ToastService>();




        return services;
    }
}

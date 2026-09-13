using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Routing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiRouter.Endpoints;

/// <summary>
/// 提供可视化配置和健康状态监控 Dashboard 页面及 API 接口。
/// </summary>
public static partial class DashboardHandler
{
    /// <summary>
    /// 注册监控 Dashboard 的 HTML 页面路由及监控 JSON 数据 API 路由。
    /// </summary>
    /// <param name="endpoints">路由构建器。</param>
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // 1. Dashboard UI is now served by Blazor Server via Pages/Dashboard/_Host.cshtml (Razor Pages routing).
        //    Old MapGet removed - was: static dashboard.html served here.

                MapLiveMetricsEndpoints(endpoints);
        MapRequestAuditEndpoints(endpoints);
        MapEvalSandboxEndpoints(endpoints);
        MapRoutingStateEndpoints(endpoints);
        MapConfigEndpoints(endpoints);
        MapTenantKeyEndpoints(endpoints);
    }
}

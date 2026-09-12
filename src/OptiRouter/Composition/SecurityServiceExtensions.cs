using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.RateLimiting;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Security;

namespace OptiRouter.Composition;

/// <summary>
/// 安全模块的 DI 注册（#4 模块化：组合根拆分）——管理端 Cookie 认证/授权、
/// 登录失败限流、/v1 全局固定窗口限流（网络边界）。
/// </summary>
internal static class SecurityServiceExtensions
{
    public static IServiceCollection AddSecurityServices(this IServiceCollection services, IConfiguration configuration)
    {
// 管理端登录会话（Cookie）：可视化界面仅管理员登录后可用。
// /v1/* 代理端点不受此影响（仍走 ProxyApiKey + 租户 ClientKeyService）。
// 默认强制 HTTPS（Cookie 仅经 HTTPS 下发，防中间人窃取）；纯内网 HTTP 部署可在 appsettings 设 OptiRouter:AdminCookieRequireHttps=false。
bool adminCookieRequireHttps = configuration.GetValue<bool?>("OptiRouter:AdminCookieRequireHttps") ?? true;
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "OptiRouter.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = adminCookieRequireHttps
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
    });
services.AddAuthorization();

// 管理端登录失败限流（单实例内存，按 IP 计数）。
services.AddSingleton<LoginRateLimiter>();

int requestsPerMinute = configuration.GetValue<int?>("OptiRouter:RequestsPerMinute") ?? 60;
if (requestsPerMinute <= 0)
    throw new InvalidOperationException("OptiRouter:RequestsPerMinute must be greater than zero.");

services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejectionContext, cancellationToken) =>
    {
        await ProtocolErrorHelper.WriteProxyErrorAsync(
            rejectionContext.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "Rate limit exceeded. Please slow down.",
            "RATE_LIMIT_EXCEEDED",
            retryAfterSeconds: 60).ConfigureAwait(false);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!RequestPathPolicy.IsProxyPath(context.Request.Path))
            return RateLimitPartition.GetNoLimiter("public");

        // 每请求从已合并的 IConfiguration 读阈值（含 WebApplicationFactory 经 ConfigureAppConfiguration 注入的值）。
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        bool trustProxy = config.GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false;
        string partitionKey = RequestIdentity.ResolvePartitionKey(context, trustProxy);

        // 注意：FixedWindowRateLimiter 的 PermitLimit 在分区首次创建时定型，运行时改配置仅对新建分区生效，
        // 既有分区沿用创建时的值——变更全局生效需重启进程。这是 ASP.NET 限流器的固有约束，非可热更。
        int limit = config.GetValue<int?>("OptiRouter:RequestsPerMinute") ?? requestsPerMinute;

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});
        return services;
    }
}

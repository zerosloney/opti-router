using System.Security.Claims;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;

namespace OptiRouter.Security;

internal sealed class RequestAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (RequestPathPolicy.IsBlazorFrameworkPath(context.Request.Path)
            || !RequestPathPolicy.IsProtectedPath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        bool isAdminPath = RequestPathPolicy.IsAdminPath(context.Request.Path);
        bool isProxyPath = RequestPathPolicy.IsProxyPath(context.Request.Path);
        var adminKeyStore = context.RequestServices.GetRequiredService<AdminKeyStore>();

        if (isProxyPath && !isAdminPath)
        {
            var authorizationResult = context.RequestServices.GetRequiredService<ClientKeyService>()
                .AuthorizeRequest(RequestIdentity.ExtractApiKey(context));
            switch (authorizationResult.Status)
            {
                case ClientKeyAuthorizationStatus.Authorized:
                    context.Items[typeof(ClientKeyAuthorizationResult)] = authorizationResult;
                    break;
                case ClientKeyAuthorizationStatus.RateLimited:
                    await ProtocolErrorHelper.WriteProxyErrorAsync(context,
                        StatusCodes.Status429TooManyRequests, "Client key rate limit exceeded",
                        "RATE_LIMIT_EXCEEDED", authorizationResult.RetryAfterSeconds).ConfigureAwait(false);
                    return;
                case ClientKeyAuthorizationStatus.BudgetExhausted:
                    await ProtocolErrorHelper.WriteProxyErrorAsync(context,
                        StatusCodes.Status429TooManyRequests, "Client key daily budget exhausted",
                        "BUDGET_EXHAUSTED", authorizationResult.RetryAfterSeconds).ConfigureAwait(false);
                    return;
                default:
                    await ProtocolErrorHelper.WriteProxyErrorAsync(context,
                        StatusCodes.Status401Unauthorized, "Unauthorized", "INVALID_API_KEY").ConfigureAwait(false);
                    return;
            }
        }
        else if (isAdminPath)
        {
            bool sessionAuthenticated = context.User.Identity?.IsAuthenticated == true;
            string? presentedToken = sessionAuthenticated ? null : RequestIdentity.ExtractApiKey(context);
            LoginRateLimiter? loginRateLimiter = null;
            string? throttleKey = null;
            if (presentedToken is not null)
            {
                loginRateLimiter = context.RequestServices.GetRequiredService<LoginRateLimiter>();
                bool trustProxy = context.RequestServices.GetRequiredService<IConfiguration>()
                    .GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false;
                throttleKey = LoginRateLimiter.ResolveClientIp(context, trustProxy);
                if (loginRateLimiter.IsLocked(throttleKey))
                {
                    await ProtocolErrorHelper.WriteProxyErrorAsync(context,
                        StatusCodes.Status401Unauthorized, "Unauthorized", "INVALID_API_KEY").ConfigureAwait(false);
                    return;
                }
            }

            AdminRole role;
            if (sessionAuthenticated)
            {
                // Cookie 分支：角色取自登录时写入的 ClaimTypes.Role（小写口径，见 AdminRoles）。
                // claim 缺失（本功能上线前的历史 Cookie）默认 Admin 向后兼容——历史会话均为主键登录。
                role = AdminRoles.TryParse(
                    context.User.FindFirstValue(ClaimTypes.Role), out var parsedRole)
                    ? parsedRole
                    : AdminRole.Admin;
            }
            else if (adminKeyStore.TryResolveRole(presentedToken, out var bearerRole))
            {
                // Bearer 分支：主键 → admin；附加身份密钥 → 其存储角色。
                role = bearerRole;
            }
            else
            {
                if (presentedToken is not null)
                    loginRateLimiter!.RecordFailure(throttleKey!);
                if (RequestPathPolicy.IsAdminPageRequest(context.Request.Path))
                {
                    context.Response.Redirect("/login");
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // 最小 RBAC 执法：仅管理 API（/api/dashboard、/api/models）做角色矩阵判断，
            // 管理页面请求与 /mcp 维持现状不执法。矩阵安全默认见 AdminOperations。
            if (context.Request.Path.StartsWithSegments("/api")
                && !AdminOperations.Can(role, context.Request.Method, context.Request.Path.Value!))
            {
                // 裸 403：与上方 401 同风格，不带 body（避免向无权方泄露矩阵细节）。
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else
        {
            await ProtocolErrorHelper.WriteProxyErrorAsync(context,
                StatusCodes.Status401Unauthorized, "Unauthorized", "INVALID_API_KEY").ConfigureAwait(false);
            return;
        }

        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next(context).ConfigureAwait(false);
    }
}

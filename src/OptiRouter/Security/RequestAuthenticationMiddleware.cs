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

            bool bearerAuthenticated = presentedToken is not null && adminKeyStore.IsValid(presentedToken);
            if (!sessionAuthenticated && !bearerAuthenticated)
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

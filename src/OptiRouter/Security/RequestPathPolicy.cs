namespace OptiRouter.Security;

internal static class RequestPathPolicy
{
    private static readonly string[] AdminPathPrefixes =
    {
        "/dashboard",
        "/overview",
        "/requests",
        "/models",
        "/router",
        "/keys",
        "/benchmarks",
        "/api/dashboard",
        "/api/models",
        "/mcp"
    };

    internal static bool IsProtectedPath(PathString path) => IsProxyPath(path) || IsAdminPath(path);

    internal static bool IsAdminPath(PathString path)
    {
        foreach (var prefix in AdminPathPrefixes)
        {
            if (path.StartsWithSegments(prefix))
                return true;
        }
        return false;
    }

    // /v1beta is not a child segment of /v1.
    internal static bool IsProxyPath(PathString path) =>
        path.StartsWithSegments("/v1") || path.StartsWithSegments("/v1beta");

    internal static bool IsBlazorFrameworkPath(PathString path) =>
        path.StartsWithSegments("/_framework")
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/_content");

    internal static bool IsAdminPageRequest(PathString path) =>
        !path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/dashboard/api-docs")
        && !path.StartsWithSegments("/mcp");
}

using OptiRouter.Security;

namespace OptiRouter.Tests.Security;

public sealed class RequestPathPolicyTests
{
    [Theory]
    [InlineData("/v1", true, false)]
    [InlineData("/V1/messages", true, false)]
    [InlineData("/v1beta/models/auto:generateContent", true, false)]
    [InlineData("/v10/models", false, false)]
    [InlineData("/v1beta-extra", false, false)]
    [InlineData("/dashboard", false, true)]
    [InlineData("/DASHBOARD/api-docs/v1/openapi.json", false, true)]
    [InlineData("/api/models/item", false, true)]
    [InlineData("/api/dashboard/session/ping", false, true)]
    [InlineData("/mcp/tools", false, true)]
    [InlineData("/mcp-extra", false, false)]
    [InlineData("/dashboard-extra", false, false)]
    [InlineData("/api/models-extra", false, false)]
    [InlineData("/login", false, false)]
    public void PathClassification_UsesCaseInsensitiveSegments(string path, bool proxy, bool admin)
    {
        Assert.Equal(proxy, RequestPathPolicy.IsProxyPath(path));
        Assert.Equal(admin, RequestPathPolicy.IsAdminPath(path));
        Assert.Equal(proxy || admin, RequestPathPolicy.IsProtectedPath(path));
    }

    [Theory]
    [InlineData("/_blazor/negotiate", true)]
    [InlineData("/_framework/blazor.server.js", true)]
    [InlineData("/_content/app.css", true)]
    [InlineData("/_blazor-extra", false)]
    public void FrameworkBypass_DoesNotMatchLookalikePaths(string path, bool framework) =>
        Assert.Equal(framework, RequestPathPolicy.IsBlazorFrameworkPath(path));
}

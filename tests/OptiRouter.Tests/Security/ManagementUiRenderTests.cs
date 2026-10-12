using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Tests.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Security;

/// <summary>
/// 管理台 Blazor 页面渲染回归测试（真实 HTTP 管道 + ServerPrerendered 预渲染）。
/// 钉住的故障：v0.3.0 引入的整页空白——MainLayout 侧栏 AuthorizeView 需要级联
/// Task&lt;AuthenticationState&gt;，App.razor 缺少 CascadingAuthenticationState 时渲染期抛
/// InvalidOperationException；ErrorBoundary 的错误页本身也套 MainLayout 二次抛异常，页面彻底空白。
/// </summary>
public sealed class ManagementUiRenderTests
{
    private const string AdminKey = "render-test-admin-key";
    private const string CookieName = "OptiRouter.Admin";

    private static TestWebApplicationFactory CreateFactory() => new()
    {
        AdminApiKey = AdminKey,
        RequestsPerMinute = 6000,
        // RouterOptions 校验要求至少一个模型端点（与 AdminRbacIntegrationTests 同口径的种子模型）。
        ConfigureTestServicesAction = services =>
            services.Configure<OptiRouter.Configuration.RouterOptions>(opt => opt.Models.Add(
                new OptiRouter.Configuration.ModelEndpointOptions
                {
                    Name = "render-seed-model",
                    BaseUrl = "http://localhost/v1",
                    ApiKey = "test-only",
                    Tier = OptiRouter.Configuration.ModelTier.Medium,
                    MaxContextTokens = 8192,
                    Enabled = true
                }))
    };

    /// <summary>登录页登录流（范例同 AdminRbacIntegrationTests）：取 antiforgery → POST /login → 会话 Cookie。</summary>
    private static async Task<string> LoginAndGetCookieAsync(HttpClient client, string adminKey)
    {
        using var loginPageResponse = await client.GetAsync("/login");
        var loginPage = await loginPageResponse.Content.ReadAsStringAsync();
        var antiforgeryCookie = loginPageResponse.Headers.TryGetValues("Set-Cookie", out var lc)
            ? lc.FirstOrDefault(v => v.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal))?.Split(';', 2)[0]
            : null;
        Assert.False(string.IsNullOrWhiteSpace(antiforgeryCookie), "login page did not issue antiforgery cookie");

        const string tokenMarker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        int tokenStart = loginPage.IndexOf(tokenMarker, StringComparison.Ordinal) + tokenMarker.Length;
        int tokenEnd = loginPage.IndexOf('"', tokenStart);
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/login");
        loginRequest.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);
        loginRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["AdminKey"] = adminKey,
            ["__RequestVerificationToken"] = loginPage[tokenStart..tokenEnd]
        });
        using var loginResponse = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        return loginResponse.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    [Fact]
    public async Task AdminLogin_ModelsPage_RendersLayoutAndContent()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var cookie = await LoginAndGetCookieAsync(client, AdminKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/models");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        // 侧栏导航经 AuthorizeView(Roles=admin,operator) 渲染出来——缺 CascadingAuthenticationState 时这里整页空白。
        Assert.Contains("nav-text", body);
        // 页面正文预渲染进 HTML（不只是空壳）。
        Assert.Contains("模型配置页", body);
        // 渲染异常会落 ErrorBoundary 的错误页（文案“页面渲染异常”）。
        Assert.DoesNotContain("页面渲染异常", body);
    }
}

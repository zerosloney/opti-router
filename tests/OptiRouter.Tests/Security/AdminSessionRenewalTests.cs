using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Clients;
using OptiRouter.Components.Services;
using OptiRouter.Tests.Endpoints;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Security;

/// <summary>
/// P2-4：管理台会话 Cookie 滑动续期与电路内凭据同步。
/// - 单元层：ApiService 捕获响应中的续期 Set-Cookie 并在后续请求回送（确定性 transport）；
/// - 集成层（可控票据时钟 = 短 ExpireTimeSpan）：旧票据到期 401、续期票据仍 200——
///   证明"浏览器已续期、电路内旧凭据却过期"的问题前提与捕获修复的一致性。
/// </summary>
public sealed class AdminSessionRenewalTests
{
    private const string AdminKey = "renewal-test-key";
    private const string CookieName = "OptiRouter.Admin";

    // ─────────────────────────── 单元层：捕获逻辑 ───────────────────────────

    /// <summary>响应携带续期 Set-Cookie 时捕获，后续请求回送新值。</summary>
    [Fact]
    public async Task SendAsync_CapturesRenewedCookie_AndSendsItSubsequently()
    {
        string? seenCookieHeader = null;
        int calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
                resp.Headers.Add("Set-Cookie", $"{CookieName}=renewed-ticket; path=/; secure; httponly");
                return resp;
            }
            seenCookieHeader = string.Join("; ", request.Headers.GetValues("Cookie"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var accessor = new StubAccessor();
        accessor.Context.Request.Headers.Cookie = $"{CookieName}=initial-ticket";
        var api = new ApiService(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new StubNavigationManager("http://localhost/"),
            accessor,
            NullLogger<ApiService>.Instance);

        var first = await api.GetMetricsAsync();
        accessor.Context = null!; // 模拟电路交互阶段：HttpContext 不可用，回退 _capturedCookie。
        var second = await api.GetMetricsAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, calls);
        Assert.Contains($"{CookieName}=renewed-ticket", seenCookieHeader);
        Assert.DoesNotContain("initial-ticket", seenCookieHeader);
    }

    /// <summary>非管理台 Cookie（无 OptiRouter.Admin 前缀）不捕获。</summary>
    [Fact]
    public async Task SendAsync_IgnoresNonAdminSetCookies()
    {
        string? seenCookieHeader = null;
        int calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
                resp.Headers.Add("Set-Cookie", "other-cookie=xyz; path=/");
                return resp;
            }
            seenCookieHeader = string.Join("; ", request.Headers.GetValues("Cookie"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
        });

        var accessor = new StubAccessor();
        accessor.Context.Request.Headers.Cookie = $"{CookieName}=initial-ticket";
        var api = new ApiService(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new StubNavigationManager("http://localhost/"),
            accessor,
            NullLogger<ApiService>.Instance);

        await api.GetMetricsAsync();
        await api.GetMetricsAsync();

        Assert.Contains("initial-ticket", seenCookieHeader);
        Assert.DoesNotContain("other-cookie", seenCookieHeader);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class StubAccessor : Microsoft.AspNetCore.Http.IHttpContextAccessor
    {
        public Microsoft.AspNetCore.Http.DefaultHttpContext Context { get; set; } = new();
        Microsoft.AspNetCore.Http.HttpContext? Microsoft.AspNetCore.Http.IHttpContextAccessor.HttpContext
        {
            get => Context;
            set => Context = (Microsoft.AspNetCore.Http.DefaultHttpContext)value!;
        }
    }

    private sealed class StubNavigationManager : Microsoft.AspNetCore.Components.NavigationManager
    {
        // Initialize 在 .NET 8 为非虚方法：经反射外无公开初始化入口时，
        // 使用 NotifyLocationChanged(IsTrackedNavigation: false) 前置 Initialize 的等效公开路径。
        public StubNavigationManager(string baseUri)
        {
            var method = typeof(Microsoft.AspNetCore.Components.NavigationManager).GetMethod(
                "Initialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            method?.Invoke(this, new object[] { baseUri, baseUri });
        }
    }

    // ──────────────── 集成层：可控票据时钟（短 ExpireTimeSpan）────────────────

    /// <summary>
    /// 真实登录流 + 短票据：票据过半后服务端调用获得续期 Cookie；旧票据到期后 401
    /// （问题前提），续期后的 Cookie 仍 200（电路内捕获即修复）。
    /// </summary>
    [Fact]
    public async Task SlidingRenewal_OldTicketExpiresWhileRenewedTicketKeepsWorking()
    {
        var factory = new TestWebApplicationFactory
        {
            AdminApiKey = AdminKey,
            RequestsPerMinute = 6000,
            // 可控票据时钟：有效期缩至 5s（滑动过半 = 2.5s 即续期、5s 即过期；时序余量 ≥0.8s）。
            ConfigureTestServicesAction = services =>
            {
                services.Configure<RouterOptions>(opt => opt.Models.Add(new ModelEndpointOptions
                {
                    Name = "renewal-model",
                    BaseUrl = "http://localhost/v1",
                    ApiKey = "test-only",
                    Tier = ModelTier.Medium,
                    MaxContextTokens = 8192,
                    Enabled = true
                }));
                services.PostConfigure<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>(
                    Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.ExpireTimeSpan = TimeSpan.FromSeconds(5);
                        options.SlidingExpiration = true;
                    });
            }
        };
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false // 禁用容器：请求 Cookie 完全由测试显式控制（v1/v2 不被自动替换）。
        });

        // 登录：取 antiforgery token + antiforgery Cookie（HandleCookies=false 需手动回传）→ POST /login → 会话 Cookie v1。
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
        loginRequest.Content = new System.Net.Http.FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["AdminKey"] = AdminKey,
                ["__RequestVerificationToken"] = loginPage[tokenStart..tokenEnd]
            });
        using var loginResponse = await client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.Redirect, loginResponse.StatusCode);
        string v1 = loginResponse.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal))
            .Split(';', 2)[0];

        // 0s：v1 有效。
        using var r0 = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/metrics");
        r0.Headers.TryAddWithoutValidation("Cookie", v1);
        using var resp0 = await client.SendAsync(r0);
        Assert.Equal(HttpStatusCode.OK, resp0.StatusCode);

        // ~3.3s（> 半个有效期 2.5s）：服务端在响应中下发续期 Cookie v2。
        await Task.Delay(3000);
        using var r1 = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/metrics");
        r1.Headers.TryAddWithoutValidation("Cookie", v1);
        using var resp1 = await client.SendAsync(r1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var renewed = resp1.Headers.TryGetValues("Set-Cookie", out var sc)
            ? sc.FirstOrDefault(v => v.StartsWith(CookieName + "=", StringComparison.Ordinal))?.Split(';', 2)[0]
            : null;
        Assert.False(string.IsNullOrWhiteSpace(renewed), "expected sliding renewal Set-Cookie past half lifetime");
        Assert.NotEqual(v1, renewed);

        // ~5.8s（> v1 的 5s 有效期）：旧票据死亡（问题前提），续期票据仍放行（捕获即修复）。
        await Task.Delay(2500);
        using var r2 = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/metrics");
        r2.Headers.TryAddWithoutValidation("Cookie", v1);
        using var resp2 = await client.SendAsync(r2);
        Assert.NotEqual(HttpStatusCode.OK, resp2.StatusCode);

        using var r3 = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/metrics");
        r3.Headers.TryAddWithoutValidation("Cookie", renewed);
        using var resp3 = await client.SendAsync(r3);
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;
using OptiRouter.Security;
using OptiRouter.Tests.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Security;

/// <summary>
/// 管理面最小 RBAC 集成测试（真实 HTTP 管道）：
/// - 向后兼容：主键 Bearer 行为不变（未配置附加身份时与现状一致）；
/// - viewer：读放行、写与明文密钥查看 403（Bearer 与登录 Cookie 两路）；
/// - operator：配置写放行、密钥/身份/模型管理 403；
/// - identities API：admin 签发→列表不含 KeyHash→删除→404。
/// </summary>
public sealed class AdminRbacIntegrationTests
{
    private const string AdminKey = "rbac-test-admin-key";
    private const string CookieName = "OptiRouter.Admin";

    private static TestWebApplicationFactory CreateFactory() => new()
    {
        AdminApiKey = AdminKey,
        RequestsPerMinute = 6000,
        // RouterOptions 校验要求至少一个模型端点（与 AdminSessionRenewalTests 同口径的种子模型）。
        ConfigureTestServicesAction = services =>
            services.Configure<OptiRouter.Configuration.RouterOptions>(opt => opt.Models.Add(
                new OptiRouter.Configuration.ModelEndpointOptions
                {
                    Name = "rbac-seed-model",
                    BaseUrl = "http://localhost/v1",
                    ApiKey = "test-only",
                    Tier = OptiRouter.Configuration.ModelTier.Medium,
                    MaxContextTokens = 8192,
                    Enabled = true
                }))
    };

    private static HttpClient CreateClient(TestWebApplicationFactory factory, string? bearerToken = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        if (bearerToken is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    private static AdminKeyStore AdminKeys(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<AdminKeyStore>();

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, object? json = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
            request.Content = JsonContent.Create(json);
        return await client.SendAsync(request);
    }

    /// <summary>登录页登录流（范例同 AdminSessionRenewalTests）：取 antiforgery → POST /login → 会话 Cookie。</summary>
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

    private static HttpRequestMessage WithCookie(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    // ──────────────── a) 向后兼容：主键 Bearer 行为不变 ────────────────

    [Fact]
    public async Task PrimaryKeyBearer_BehaviorUnchanged()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, AdminKey);

        using var get = await client.GetAsync("/api/dashboard/config");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var reveal = await client.GetAsync("/api/models/apikey?name=none");
        // admin 过角色矩阵（密钥明文查看仅 admin 放行）；测试宿主无该模型 → handler 404 而非 403。
        Assert.Equal(HttpStatusCode.NotFound, reveal.StatusCode);

        using var post = await SendAsync(client, HttpMethod.Post, "/api/dashboard/keys",
            new { tenantName = "t", dailyBudgetUsd = 1m, maxQps = 1 });
        Assert.True(post.IsSuccessStatusCode, $"POST keys failed: {(int)post.StatusCode}");

        // 未配置的出示密钥仍是 401（向后兼容，不因 RBAC 改变拒绝语义）。
        using var anon = CreateClient(factory, "totally-unknown-key");
        using var unknown = await anon.GetAsync("/api/dashboard/config");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    // ──────────────── b) viewer Bearer：读放行、写 403 ────────────────

    [Fact]
    public async Task ViewerBearer_ReadsAllowed_WritesForbidden()
    {
        using var factory = CreateFactory();
        _ = AdminKeys(factory).AddIdentity("watcher", "viewer", out string viewerKey);
        using var client = CreateClient(factory, viewerKey);

        using var get = await client.GetAsync("/api/dashboard/config");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var put = await SendAsync(client, HttpMethod.Put, "/api/dashboard/config", new { });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        using var postKeys = await SendAsync(client, HttpMethod.Post, "/api/dashboard/keys",
            new { tenantName = "t" });
        Assert.Equal(HttpStatusCode.Forbidden, postKeys.StatusCode);

        // 明文上游密钥查看仅 admin。
        using var reveal = await client.GetAsync("/api/models/apikey?name=none");
        Assert.Equal(HttpStatusCode.Forbidden, reveal.StatusCode);

        // 签发身份属默认规则 admin-only。
        using var postIdentity = await SendAsync(client, HttpMethod.Post, "/api/dashboard/identities",
            new { name = "x", role = "viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, postIdentity.StatusCode);
    }

    // ──────────────── c) viewer 登录 Cookie：角色 claim 进票据 ────────────────

    [Fact]
    public async Task ViewerLogin_CookieCarriesRole_SameForbiddenMatrix()
    {
        using var factory = CreateFactory();
        _ = AdminKeys(factory).AddIdentity("watcher", "viewer", out string viewerKey);
        using var loginClient = CreateClient(factory);
        string cookie = await LoginAndGetCookieAsync(loginClient, viewerKey);

        using (var request = WithCookie(HttpMethod.Get, "/api/dashboard/config", cookie))
        using (var get = await loginClient.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        }

        using (var request = WithCookie(HttpMethod.Put, "/api/dashboard/config", cookie))
        using (var put = await loginClient.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        }

        using (var request = WithCookie(HttpMethod.Post, "/api/dashboard/keys", cookie))
        using (var postKeys = await loginClient.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, postKeys.StatusCode);
        }
    }

    // ──────────────── d) operator Bearer：配置写放行、管理写 403 ────────────────

    [Fact]
    public async Task OperatorBearer_ConfigWriteAllowed_KeyManagementForbidden()
    {
        using var factory = CreateFactory();
        _ = AdminKeys(factory).AddIdentity("ops", "operator", out string operatorKey);
        using var client = CreateClient(factory, operatorKey);

        // 配置写走 CAS：先读当前版本，再整表保存语义路由（空表 = 清空，测试宿主内无副作用）。
        using var getResp = await client.GetAsync("/api/dashboard/semantic-routes");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var snapshot = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        string version = snapshot.GetProperty("version").GetString()!;
        using var put = await SendAsync(client, HttpMethod.Put, "/api/dashboard/semantic-routes",
            new { routes = Array.Empty<object>(), expectedVersion = version });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // 密钥/身份管理写仍是 admin-only。
        using var postKeys = await SendAsync(client, HttpMethod.Post, "/api/dashboard/keys",
            new { tenantName = "t" });
        Assert.Equal(HttpStatusCode.Forbidden, postKeys.StatusCode);

        using var deleteIdentity = await SendAsync(client, HttpMethod.Delete,
            "/api/dashboard/identities/nonexistent");
        Assert.Equal(HttpStatusCode.Forbidden, deleteIdentity.StatusCode);
    }

    // ──────────────── e) identities API 生命周期 ────────────────

    [Fact]
    public async Task IdentitiesApi_AdminLifecycle_ViewerWritesForbidden()
    {
        using var factory = CreateFactory();
        _ = AdminKeys(factory).AddIdentity("watcher", "viewer", out string viewerKey);
        using var client = CreateClient(factory, AdminKey);

        // 签发：明文仅此一次返回。
        using var create = await SendAsync(client, HttpMethod.Post, "/api/dashboard/identities",
            new { name = "ops1", role = "operator" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        string id = created.GetProperty("id").GetString()!;
        string issuedKey = created.GetProperty("key").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(issuedKey), "plaintext key must be returned exactly once");
        Assert.Equal("operator", created.GetProperty("role").GetString());

        // 签发的密钥立即可鉴权（operator 角色）。
        Assert.True(AdminKeys(factory).TryResolveRole(issuedKey, out var resolved));
        Assert.Equal(AdminRole.Operator, resolved);

        // 列表：含 keyPrefix 指纹，绝不含 KeyHash。
        using var list = await client.GetAsync("/api/dashboard/identities");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        string listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains("\"keyPrefix\"", listBody);
        Assert.DoesNotContain("keyHash", listBody);
        Assert.Contains("ops1", listBody);

        // viewer：列表读 403（矩阵读规则的 admin 例外：管理身份列表不对下位角色暴露），写 403。
        using var viewerClient = CreateClient(factory, viewerKey);
        using (var viewerList = await viewerClient.GetAsync("/api/dashboard/identities"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewerList.StatusCode);
        }
        using (var viewerCreate = await SendAsync(viewerClient, HttpMethod.Post, "/api/dashboard/identities",
            new { name = "x", role = "viewer" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, viewerCreate.StatusCode);
        }

        // 删除：成功一次，再删 404；撤销后密钥立即失效。
        using (var del = await SendAsync(client, HttpMethod.Delete, $"/api/dashboard/identities/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        }
        using (var delAgain = await SendAsync(client, HttpMethod.Delete, $"/api/dashboard/identities/{id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, delAgain.StatusCode);
        }
        Assert.False(AdminKeys(factory).TryResolveRole(issuedKey, out _));

        // 非法角色 → 400。
        using var badRole = await SendAsync(client, HttpMethod.Post, "/api/dashboard/identities",
            new { name = "x", role = "root" });
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
    }
}

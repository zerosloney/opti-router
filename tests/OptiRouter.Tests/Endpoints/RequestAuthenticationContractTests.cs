using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Endpoints;

public sealed class RequestAuthenticationContractTests
{
    private static TestWebApplicationFactory CreateFactory() => new()
    {
        AdminApiKey = "auth-contract-admin",
        ConfigureTestServicesAction = services => services.Configure<RouterOptions>(options =>
        {
            options.Models.Clear();
            options.Models.Add(new ModelEndpointOptions
            {
                Name = "contract-model",
                BaseUrl = "http://localhost/v1",
                ApiKey = "test-only",
                Tier = ModelTier.Medium,
                MaxContextTokens = 8192,
                Enabled = true
            });
        })
    };

    [Theory]
    [InlineData("/v1/messages", null, "test-proxy-key", null, 400)]
    [InlineData("/v1/messages", "Bearer test-proxy-key", "wrong", null, 400)]
    [InlineData("/v1/messages", "bEaReR test-proxy-key", null, null, 400)]
    [InlineData("/v1/messages", "Bearer wrong", "test-proxy-key", null, 401)]
    [InlineData("/v1/messages", "Bearer", "test-proxy-key", null, 401)]
    [InlineData("/v1/messages", "Basic ignored", "test-proxy-key", null, 400)]
    [InlineData("/v1/messages", null, null, "test-proxy-key", 401)]
    [InlineData("/v1/messages", null, "wrong", null, 401)]
    [InlineData("/v1beta/models/auto:generateContent", null, "test-proxy-key", null, 400)]
    [InlineData("/v1beta/models/auto:generateContent", null, null, "test-proxy-key", 400)]
    [InlineData("/v1beta/models/auto:generateContent", null, "wrong", "test-proxy-key", 401)]
    [InlineData("/v1beta/models/auto:generateContent", null, "test-proxy-key", "wrong", 400)]
    [InlineData("/v1beta/models/auto:generateContent", "Bearer wrong", "test-proxy-key", "test-proxy-key", 401)]
    [InlineData("/v1beta/models/auto:generateContent", "Bearer", "test-proxy-key", "test-proxy-key", 401)]
    [InlineData("/v1beta/models/auto:generateContent", "Bearer test-proxy-key", "wrong", "wrong", 400)]
    [InlineData("/v1beta/models/auto:generateContent", "Basic ignored", null, "test-proxy-key", 400)]
    [InlineData("/v1/chat/completions", null, "test-proxy-key", "test-proxy-key", 401)]
    [InlineData("/v1/chat/completions", "Bearer test-proxy-key", null, null, 400)]
    public async Task CredentialPrecedence_PreservesStatusAndProtocolEnvelope(
        string path, string? authorization, string? nativeKey, string? queryKey, int expectedStatus)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(apiKey: null);
        using var request = new HttpRequestMessage(HttpMethod.Post, path + (queryKey is null ? "" : "?key=" + queryKey))
        {
            // Empty input stops at validation, so no upstream is contacted after authentication.
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (nativeKey is not null)
        {
            request.Headers.Add("x-api-key", nativeKey);
            request.Headers.Add("x-goog-api-key", nativeKey);
        }
        request.Headers.Add("X-Request-Id", "auth-contract");

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("auth-contract", Assert.Single(response.Headers.GetValues("X-Request-Id")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = body.RootElement.GetProperty("error");
        if (path.StartsWith("/v1beta", StringComparison.Ordinal))
        {
            Assert.Equal(expectedStatus == 401 ? "UNAUTHENTICATED" : "INVALID_ARGUMENT", error.GetProperty("status").GetString());
        }
        else
        {
            Assert.Equal(expectedStatus == 401 ? "authentication_error" : "invalid_request_error", error.GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("/dashboard", 302)]
    [InlineData("/overview", 302)]
    [InlineData("/requests", 302)]
    [InlineData("/models", 302)]
    [InlineData("/router", 302)]
    [InlineData("/keys", 302)]
    [InlineData("/benchmarks", 302)]
    [InlineData("/api/dashboard/session/ping", 401)]
    [InlineData("/api/models", 401)]
    [InlineData("/dashboard/api-docs/v1/openapi.json", 401)]
    [InlineData("/mcp/tools", 401)]
    public async Task AnonymousAdminRequests_PreserveRedirectVersusUnauthorized(string path, int expectedStatus)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(path);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (response.StatusCode == HttpStatusCode.Redirect)
            Assert.Equal("/login", response.Headers.Location?.OriginalString);
        else
            Assert.Null(response.Headers.Location);
    }
}

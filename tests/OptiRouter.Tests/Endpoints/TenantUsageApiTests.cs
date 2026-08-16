using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Tests.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 租户用量与配额 API 端到端测试：请求计数、用量查询与 CSV 导出。
/// </summary>
public sealed class TenantUsageApiTests
{
    private const string AdminKey = "tenant-test-admin-key";

    private sealed class UsageFactory : WebApplicationFactory<Program>
    {
        public string KeysFilePath { get; } = Path.Combine(
            Path.GetTempPath(),
            "optirouter-tenant-usage-" + Guid.NewGuid().ToString("N"),
            "client-keys.json");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", AdminKey);
            builder.UseSetting("OptiRouter:AdminApiKey", AdminKey);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "6000");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    var endpoint = new ModelEndpointOptions
                    {
                        Name = "usage-model",
                        BaseUrl = "https://api.example.com",
                        ApiKey = "sk-test",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    };
                    opt.Models.Add(endpoint);
                    opt.Routing.EnableHealthProbe = false;
                    opt.Routing.EnableLatencyAware = false;
                    opt.Routing.EnableRuleClassifier = false;
                    opt.Routing.EnableTokenEstimator = false;
                    opt.Routing.EnableBudgetGuard = false;
                    opt.Routing.EnableFailover = false;
                });
                // 独立临时文件，避免污染真实 data/client-keys.json。
                services.AddSingleton<ClientKeyService>(sp => new ClientKeyService(
                    KeysFilePath,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ClientKeyService>>()));
                services.AddSingleton<IModelClientProvider>(new TestModelClientProvider(new Dictionary<string, IModelClient>
                {
                    ["usage-model"] = new MockModelClient(new ModelEndpointOptions
                    {
                        Name = "usage-model",
                        BaseUrl = "https://api.example.com",
                        ApiKey = "sk-test",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        InputPricePerMillion = 1m,
                        OutputPricePerMillion = 2m,
                        Enabled = true
                    }, (req, ct) => Task.FromResult(new RawChatResponse(
                        "{\"id\":\"1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}",
                        new ChatUsage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 })))
                }));
            });
        }
    }

    private static HttpClient CreateAdminClient(UsageFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        return client;
    }

    [Fact]
    public async Task UsageEndpoints_ListAndExport_IncludeCreatedTenant()
    {
        using var factory = new UsageFactory();
        using var admin = CreateAdminClient(factory);

        // 创建租户
        var create = await admin.PostAsync("/api/dashboard/keys", new StringContent(
            JsonSerializer.Serialize(new { tenantName = "acme", dailyBudgetUsd = 50.0m, maxQps = 20 }),
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        string keyId = createDoc.RootElement.GetProperty("keyId").GetString()!;
        string plaintext = createDoc.RootElement.GetProperty("plaintextKey").GetString()!;

        // 列表包含新租户且初始用量为 0
        var usageResp = await admin.GetAsync("/api/dashboard/keys/usage");
        Assert.Equal(HttpStatusCode.OK, usageResp.StatusCode);
        using var usageDoc = JsonDocument.Parse(await usageResp.Content.ReadAsStringAsync());
        var entry = usageDoc.RootElement.EnumerateArray().Single(e => e.GetProperty("keyId").GetString() == keyId);
        Assert.Equal("acme", entry.GetProperty("tenantName").GetString());
        Assert.Equal(0, entry.GetProperty("dailyRequestCount").GetInt32());
        Assert.Equal(50.0m, entry.GetProperty("dailyBudgetUsd").GetDecimal());
        Assert.Equal(50.0m, entry.GetProperty("remainingBudgetUsd").GetDecimal());

        // 单 key 用量
        var singleResp = await admin.GetAsync($"/api/dashboard/keys/{keyId}/usage");
        Assert.Equal(HttpStatusCode.OK, singleResp.StatusCode);
        using var singleDoc = JsonDocument.Parse(await singleResp.Content.ReadAsStringAsync());
        Assert.Equal(keyId, singleDoc.RootElement.GetProperty("keyId").GetString());

        // 租户 key 真实调用一次 /v1/chat/completions → 请求计数 +1、花费入账
        using var tenantClient = factory.CreateClient();
        tenantClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plaintext);
        var chatResp = await tenantClient.PostAsync("/v1/chat/completions", new StringContent(
            "{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, chatResp.StatusCode);

        var afterResp = await admin.GetAsync($"/api/dashboard/keys/{keyId}/usage");
        using var afterDoc = JsonDocument.Parse(await afterResp.Content.ReadAsStringAsync());
        Assert.Equal(1, afterDoc.RootElement.GetProperty("dailyRequestCount").GetInt32());
        Assert.True(afterDoc.RootElement.GetProperty("dailySpendUsd").GetDecimal() > 0m);

        // CSV 导出包含表头与租户行
        var csvResp = await admin.GetAsync("/api/dashboard/keys/usage/export");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        Assert.Equal("text/csv", csvResp.Content.Headers.ContentType?.MediaType);
        string csv = await csvResp.Content.ReadAsStringAsync();
        Assert.Contains("key_id,key_prefix,tenant_name,daily_budget_usd", csv);
        Assert.Contains("acme", csv);
        Assert.Contains(",1,20,", csv); // daily_request_count=1, max_qps=20

        // 不存在的 key → 404
        var notFound = await admin.GetAsync("/api/dashboard/keys/nope/usage");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public async Task UsageEndpoints_RequireAdminAuth()
    {
        using var factory = new UsageFactory();
        using var anonymous = factory.CreateClient();
        var resp = await anonymous.GetAsync("/api/dashboard/keys/usage");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

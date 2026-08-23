using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Endpoints;

public class ModelsConfigHandlerTests
{
    private sealed class ModelsFactory : WebApplicationFactory<Program>
    {
        public const string Key = "models-test-key";

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "optirouter-models-test-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempRoot);
            File.Copy(FindSourceAppsettings(), Path.Combine(_tempRoot, "appsettings.json"));
            builder.UseSetting(WebHostDefaults.ContentRootKey, _tempRoot);
            builder.UseSetting("OptiRouter:ProxyApiKey", Key);
            builder.UseSetting("OptiRouter:AdminApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:ConfigDbPath", Path.Combine(_tempRoot, "optirouter-config.db"));
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.Configure<RouterOptions>(options =>
                {
                    options.Models.Clear();
                    options.Models.Add(new ModelEndpointOptions
                    {
                        Name = "test-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    });
                    options.Routing.EnableHealthProbe = false;
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_tempRoot))
            {
                try { Directory.Delete(_tempRoot, recursive: true); } catch { }
            }
        }

        // 源 appsettings.json 由测试项目复制到输出目录（见 OptiRouter.Tests.csproj），不向上遍历目录树。
        private static string FindSourceAppsettings()
            => Path.Combine(AppContext.BaseDirectory, "RepositoryFiles", "appsettings.json");
    }

    [Fact]
    public async Task Models_Get_ReturnsMaskedApiKeyHint_WithWhitespaceWarning()
    {
        // ApiKeyHint：前 3 + 后 4 遮蔽预览；首尾空白附加警示（粘贴误差是上游 401 常见根因）；完整密钥不回传。
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        string okName = "hint-ok-" + Guid.NewGuid().ToString("N");
        string padName = "hint-pad-" + Guid.NewGuid().ToString("N");
        await CreateModelAsync(client, okName, "sk-test-12345678-wxyz");
        await CreateModelAsync(client, padName, " sk-bad-key \n");

        using var list = await client.GetAsync("/api/models");
        list.EnsureSuccessStatusCode();
        string json = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("12345678", json); // 完整密钥绝不回传

        using var doc = JsonDocument.Parse(json);
        var ok = doc.RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == okName);
        Assert.True(ok.GetProperty("hasApiKey").GetBoolean());
        Assert.Equal("sk-••••wxyz", ok.GetProperty("apiKeyHint").GetString());
        var padded = doc.RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == padName);
        Assert.EndsWith("⚠含首尾空白", padded.GetProperty("apiKeyHint").GetString());
    }

    [Fact]
    public async Task Models_RevealApiKey_ReturnsFullKeyPerRequest_OnlyForAdmin()
    {
        // reveal 端点：管理员按需取完整密钥；未鉴权 401；未知模型 404；列表接口仍不含完整密钥。
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        string name = "reveal-" + Guid.NewGuid().ToString("N");
        await CreateModelAsync(client, name, "sk-reveal-secret-value-42");

        // 未鉴权被拒
        using var anon = factory.CreateClient();
        using var anonResp = await anon.GetAsync($"/api/models/apikey?name={name}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 按需返回完整密钥（query 与 path 两种形态）
        using var byQuery = await client.GetAsync($"/api/models/apikey?name={name}");
        byQuery.EnsureSuccessStatusCode();
        Assert.Contains("sk-reveal-secret-value-42", await byQuery.Content.ReadAsStringAsync());
        using var byPath = await client.GetAsync($"/api/models/{name}/apikey");
        byPath.EnsureSuccessStatusCode();

        // 未知模型 404
        using var missing = await client.GetAsync("/api/models/apikey?name=no-such-model");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task UpdateModel_TagsAreReplacedTrimmedAndDeduplicated()
    {
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        string name = "tags-" + Guid.NewGuid().ToString("N");
        await CreateModelAsync(client, name, "sk-tags-test-value-123");

        // 更新 Tags：含空白项与重复项，应 trim + 去空 + 去重
        string body = JsonSerializer.Serialize(new { tags = new[] { " vision ", "", "tool-use", "VISION" } });
        using var update = await client.PutAsync($"/api/models?name={name}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

        using var list = await client.GetAsync("/api/models");
        list.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var model = doc.RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
        var tags = model.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Equal(new[] { "vision", "tool-use" }, tags);

        // 空数组 = 清空
        string clear = JsonSerializer.Serialize(new { tags = Array.Empty<string>() });
        using var clearResp = await client.PutAsync($"/api/models?name={name}",
            new StringContent(clear, Encoding.UTF8, "application/json"));
        Assert.True(clearResp.IsSuccessStatusCode, await clearResp.Content.ReadAsStringAsync());

        using var list2 = await client.GetAsync("/api/models");
        list2.EnsureSuccessStatusCode();
        using var doc2 = JsonDocument.Parse(await list2.Content.ReadAsStringAsync());
        var model2 = doc2.RootElement.EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
        Assert.Equal(0, model2.GetProperty("tags").GetArrayLength());
    }

    private static async Task CreateModelAsync(HttpClient client, string name, string apiKey, string? id = null)
    {
        string body = JsonSerializer.Serialize(new
        {
            name,
            id,
            baseUrl = "https://example.com",
            apiKey,
            tier = "Medium",
            inputPricePerMillion = 0,
            outputPricePerMillion = 0
        });
        using var resp = await client.PostAsync("/api/models",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.True(resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateModel_SameUpstreamIdDifferentRoutingNames_SupportsMultiKeyAccounts()
    {
        // 同一供应商多账号场景：路由名必须唯一，但上游模型 id 可相同——两个账号各自独立熔断/预算。
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        const string upstreamId = "deepseek-v4-flash";
        string acc1 = "multi-key-a-" + Guid.NewGuid().ToString("N")[..8];
        string acc2 = "multi-key-b-" + Guid.NewGuid().ToString("N")[..8];
        await CreateModelAsync(client, acc1, "sk-account-1", id: upstreamId);
        await CreateModelAsync(client, acc2, "sk-account-2", id: upstreamId);

        // 路由名重复仍被拒绝（配置 id 不豁免唯一性），错误信息引导多账号用法。
        string dup = JsonSerializer.Serialize(new { name = acc1, id = upstreamId, baseUrl = "https://example.com", apiKey = "k" });
        using var dupResp = await client.PostAsync("/api/models", new StringContent(dup, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, dupResp.StatusCode);
        Assert.Contains("upstream model id", await dupResp.Content.ReadAsStringAsync());

        // 列表回读：两个账号条目均存在，上游 id 一致。
        using var listResp = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        using var document = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var names = document.RootElement.EnumerateArray()
            .Where(m => m.GetProperty("id").GetString() == upstreamId)
            .Select(m => m.GetProperty("name").GetString())
            .ToHashSet();
        Assert.Contains(acc1, names);
        Assert.Contains(acc2, names);
    }

    [Fact]
    public async Task CreateModel_MissingOrNegativePrices_AreZero()
    {
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        string nullInputName = "null-input-" + Guid.NewGuid().ToString("N");
        string nullOutputName = "null-output-" + Guid.NewGuid().ToString("N");
        var payloads = new Dictionary<string, string>
        {
            [nullInputName] = "{\"name\":\"" + nullInputName + "\",\"baseUrl\":\"https://example.com\",\"inputPricePerMillion\":null,\"outputPricePerMillion\":-2}",
            [nullOutputName] = "{\"name\":\"" + nullOutputName + "\",\"baseUrl\":\"https://example.com\",\"inputPricePerMillion\":-1,\"outputPricePerMillion\":null}"
        };

        foreach (var payload in payloads.Values)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/api/models", content);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var listResponse = await client.GetAsync("/api/models");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var document = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        foreach (string name in payloads.Keys)
        {
            var model = document.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name);
            Assert.Equal(0, model.GetProperty("inputPricePerMillion").GetDecimal());
            Assert.Equal(0, model.GetProperty("outputPricePerMillion").GetDecimal());
        }
    }
}

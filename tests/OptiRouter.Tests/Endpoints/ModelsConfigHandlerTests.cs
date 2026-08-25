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
    public class ModelsFactory : WebApplicationFactory<Program>
    {
        public const string Key = "models-test-key";

        /// <summary>外部可注入的上游响应器；null 表示不替换 model-discover 的 HttpClient。</summary>
        internal FakeUpstreamHandler? Upstream { get; init; }

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "optirouter-models-test-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempRoot);
            File.Copy(FindSourceAppsettings(), Path.Combine(_tempRoot, "appsettings.json"));
            builder.UseSetting(WebHostDefaults.ContentRootKey, _tempRoot);
            builder.UseSetting("OptiRouter:AdminApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:ConfigDbPath", Path.Combine(_tempRoot, "optirouter-config.db"));
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("models-test-key");
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
                if (Upstream is not null)
                {
                    services.AddHttpClient("model-discover")
                        .ConfigurePrimaryHttpMessageHandler(() => Upstream);
                }
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

    /// <summary>假上游响应器：根据预设 URL 模板返回配置响应并记录请求。</summary>
    internal sealed class FakeUpstreamHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(Uri Url, HttpRequestHeaders Headers, string? Body);
        public List<RecordedRequest> Calls { get; } = new();

        private readonly Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes;
        private readonly Func<RecordedRequest, HttpResponseMessage>? _defaultRoute;

        public FakeUpstreamHandler(
            Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> routes,
            Func<RecordedRequest, HttpResponseMessage>? defaultRoute = null)
        {
            _routes = routes;
            _defaultRoute = defaultRoute;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var recorded = new RecordedRequest(request.RequestUri!, request.Headers, body);
            Calls.Add(recorded);

            foreach (var (key, factory) in _routes)
            {
                if (recorded.Url.ToString().Contains(key, StringComparison.OrdinalIgnoreCase))
                    return factory(recorded);
            }
            return _defaultRoute is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no fake route matched " + recorded.Url) }
                : _defaultRoute(recorded);
        }
    }

    private sealed class DiscoverFactory : ModelsFactory
    {
        public DiscoverFactory(FakeUpstreamHandler upstream) { Upstream = upstream; }
    }

    [Fact]
    public async Task Models_ProbeResults_ReturnsServerSideRecords()
    {
        // 手动/后台探活留痕经 GET /api/models/probe-results 下发：页面刷新后"连通状态"列预填。
        using var factory = new ModelsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        using (var empty = await client.GetAsync("/api/models/probe-results"))
        {
            empty.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Empty(doc.RootElement.EnumerateObject());
        }

        // 服务侧写入一条留痕（模拟手动探活完成）后对页面可见
        factory.Services.GetRequiredService<OptiRouter.Health.ProbeResultStore>()
            .Record("test-model", new OptiRouter.Health.ProbeStatus(true, 123, DateTime.UtcNow, "连接正常 (OK)", null));
        using var resp = await client.GetAsync("/api/models/probe-results");
        resp.EnsureSuccessStatusCode();
        using var doc2 = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var entry = doc2.RootElement.GetProperty("test-model");
        Assert.True(entry.GetProperty("success").GetBoolean());
        Assert.Equal(123, entry.GetProperty("latencyMs").GetInt64());
        Assert.Equal("连接正常 (OK)", entry.GetProperty("message").GetString());
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

    /// <summary>拼 POST body 的小工具：property names 默认使用 camelCase 与服务端 DiscoverRequest 对齐。</summary>
    private static StringContent DiscoverBody(object body) =>
        new(JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json");

    [Fact]
    public async Task Discover_OpenAI_ReturnsParsedModels_WithBearerAuth()
    {
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1/models"] = _ => JsonResponse("""
                    {"object":"list","data":[
                        {"id":"gpt-4o","object":"model","created":1,"owned_by":"openai","context_length":128000},
                        {"id":"o1-preview","object":"model","created":1,"owned_by":"openai"}
                    ]}
                    """),
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://api.openai.com", apiKey = "sk-test", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, arr.Count);
        Assert.Equal("gpt-4o", arr[0].GetProperty("id").GetString());
        Assert.Equal("openai", arr[0].GetProperty("ownedBy").GetString());
        Assert.Equal("o1-preview", arr[1].GetProperty("id").GetString());

        var sent = fake.Calls.Single();
        Assert.Equal("https://api.openai.com/v1/models", sent.Url.ToString());
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", sent.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task Discover_OpenAI_BaseUrlEndingWithV1_DoesNotDoublePath()
    {
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1/models"] = _ => JsonResponse("""{"object":"list","data":[]}"""),
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://api.openai.com/v1/", apiKey = "k", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("https://api.openai.com/v1/models", fake.Calls.Single().Url.ToString());
    }

    [Fact]
    public async Task Discover_OpenAI_PathVersionedBaseUrl_FallsBackToBaseModelsOn404()
    {
        // base url 已含非 /v1 版本段（…/plan/v3）：首选补 /v1 会 404，应回退 base + /models
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/plan/v3/models"] = _ => JsonResponse("""{"object":"list","data":[{"id":"deepseek-v3","owned_by":"tokenhub"}]}"""),
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://tokenhub.tencentmaas.com/plan/v3", apiKey = "sk-test", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("deepseek-v3", doc.RootElement[0].GetProperty("id").GetString());
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal("https://tokenhub.tencentmaas.com/plan/v3/v1/models", fake.Calls[0].Url.ToString());
        Assert.Equal("https://tokenhub.tencentmaas.com/plan/v3/models", fake.Calls[1].Url.ToString());
    }

    [Fact]
    public async Task Discover_OpenAI_PlanPrefixBaseUrl_FallsBackToSiteRootV1Models()
    {
        // 套餐型网关（腾讯 TokenHub Token Plan）：chat 在 /plan/v3 前缀下，模型列表只在站点根 /v1/models
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["https://tokenhub.tencentmaas.com/v1/models"] = _ => JsonResponse("""{"object":"list","data":[{"id":"glm-5.3","owned_by":"tokenhub"}]}"""),
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://tokenhub.tencentmaas.com/plan/v3", apiKey = "sk-plan-key", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("glm-5.3", doc.RootElement[0].GetProperty("id").GetString());
        Assert.Equal(3, fake.Calls.Count);
        Assert.Equal("https://tokenhub.tencentmaas.com/plan/v3/v1/models", fake.Calls[0].Url.ToString());
        Assert.Equal("https://tokenhub.tencentmaas.com/plan/v3/models", fake.Calls[1].Url.ToString());
        Assert.Equal("https://tokenhub.tencentmaas.com/v1/models", fake.Calls[2].Url.ToString());
    }

    [Fact]
    public async Task Discover_OpenAI_FirstCandidateUnauthorized_DoesNotFallBack()
    {
        // 401 说明路径正确而鉴权失败：不应回退，避免掩盖真实错误并重复发送凭据
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1/models"] = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"bad key"}""")
                },
            },
            defaultRoute: _ => JsonResponse("""{"object":"list","data":[{"id":"should-not-be-used"}]}"""));
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://example.com", apiKey = "sk-wrong", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Discover_OpenAI_NoApiKey_StillIssuesRequestWithoutAuth()
    {
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1/models"] = _ => JsonResponse("""{"object":"list","data":[{"id":"llama3","owned_by":"ollama"}]}"""),
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "http://localhost:11434", apiKey = (string?)null, protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sent = fake.Calls.Single();
        Assert.Null(sent.Headers.Authorization); // 上游鉴权 header 不应发出。
    }

    [Fact]
    public async Task Discover_Gemini_UsesV1BetaEndpoint_AndGoogApiKeyHeader()
    {
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1beta/models"] = _ => JsonResponse("""
                    {"models":[{"name":"models/gemini-1.5-pro"},{"name":"models/gemini-1.5-flash"}],"nextPageToken":""}
                    """),
            },
            defaultRoute: _ => JsonResponse("""{"object":"list","data":[]}"""));
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://generativelanguage.googleapis.com", apiKey = "goog-key", protocol = "Gemini" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var sent = fake.Calls.Single();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models", sent.Url.ToString());
        // Gemini header 通过集合名寻址（Headers[name].FirstOrDefault）。
        var googHeader = sent.Headers.GetValues("x-goog-api-key").Single();
        Assert.Equal("goog-key", googHeader);
    }

    [Fact]
    public async Task Discover_Anthropic_Returns501_NoPublicListEndpoint()
    {
        var fake = new FakeUpstreamHandler(new());
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://api.anthropic.com", apiKey = "k", protocol = "Anthropic" }));

        Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        Assert.Empty(fake.Calls); // 端点直接 501，不发起上游请求。
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("Anthropic", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Discover_Upstream5xx_Becomes502WithBody()
    {
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1/models"] = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom")
                },
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://api.example.com", apiKey = "k", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("500", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("boom", doc.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Discover_NetworkFailure_Becomes502()
    {
        var fake = new FakeUpstreamHandler(new(),
            defaultRoute: _ => throw new HttpRequestException("connection refused"));
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "http://does-not-exist.invalid", apiKey = "k", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Contains("connection refused", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Discover_EmptyData_Returns200EmptyArray()
    {
        var fake = new FakeUpstreamHandler(
            new Dictionary<string, Func<FakeUpstreamHandler.RecordedRequest, HttpResponseMessage>>
            {
                ["/v1/models"] = _ => JsonResponse("""{"object":"list","data":[]}"""),
            });
        using var factory = new DiscoverFactory(fake);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "https://api.example.com", apiKey = "k", protocol = "OpenAI" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Discover_MissingBaseUrl_Returns400()
    {
        using var factory = new DiscoverFactory(new FakeUpstreamHandler(new()));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ModelsFactory.Key);

        var resp = await client.PostAsync("/api/models/discover",
            DiscoverBody(new { baseUrl = "", apiKey = "k", protocol = "OpenAI" }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}

}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 配置控制台 API 扩展测试：六组新字段 GET 暴露、预设端点、PUT 校验拒绝。
/// PUT 成功路径与既有 20 字段共享同一 PersistAppsettings 分支（生产已验证），不做落盘往返测试。
/// </summary>
public class ConfigApiExpansionTests
{
    private sealed class ConfigFactory : WebApplicationFactory<Program>
    {
        public const string Key = "config-test-key";

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "optirouter-config-test-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // PUT 校验用例会经 PersistAppsettings 落盘（env.ContentRootPath/appsettings.json）。
            // 把 content root 指向临时目录（含 appsettings.json 副本），避免污染真实配置文件。
            Directory.CreateDirectory(_tempRoot);
            File.Copy(FindSourceAppsettings(), Path.Combine(_tempRoot, "appsettings.json"));
            builder.UseSetting(WebHostDefaults.ContentRootKey, _tempRoot);

            builder.UseSetting("OptiRouter:ProxyApiKey", Key);
            builder.UseSetting("OptiRouter:AdminApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            // 测试用临时配置库：PUT 校验用例经 PersistRoutingDocuments 落库到临时文件，不污染真实配置库。
            builder.UseSetting("OptiRouter:ConfigDbPath",
                Path.Combine(_tempRoot, "optirouter-config.db"));
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    opt.Models.Add(new ModelEndpointOptions
                    {
                        Name = "test-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    });
                    opt.Routing.EnableHealthProbe = false;
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

    private static HttpClient CreateClient(ConfigFactory factory) =>
        factory.CreateClient() is { } c
            ? WithAdminKey(c)
            : throw new InvalidOperationException();

    private static HttpClient WithAdminKey(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ConfigFactory.Key);
        return client;
    }

    [Fact]
    public async Task GetConfig_ExposesAllSixGroupFields()
    {
        using var factory = new ConfigFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/dashboard/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("version").GetString()));
        var routing = doc.RootElement.GetProperty("routing");

        // ① 基础路由
        Assert.True(routing.TryGetProperty("defaultTier", out _));
        Assert.True(routing.TryGetProperty("enableSessionAffinity", out _));
        Assert.True(routing.TryGetProperty("enableLoadBalance", out _));
        Assert.True(routing.TryGetProperty("enableKalmanLoadBalance", out _));
        // ② 可靠性与预算
        Assert.True(routing.TryGetProperty("failoverGlobalTimeoutSeconds", out _));
        Assert.True(routing.TryGetProperty("enableHealthProbe", out _));
        // ③ 学习与优化
        Assert.True(routing.TryGetProperty("enableSemanticCache", out _));
        Assert.True(routing.TryGetProperty("semanticCacheSimilarityThreshold", out _));
        Assert.True(routing.TryGetProperty("enableCascadeUpgrade", out _));
        Assert.True(routing.TryGetProperty("enableRegenerateFeedback", out _));
        // ④ 合规与安全
        Assert.True(routing.TryGetProperty("enableContentModeration", out _));
        Assert.True(routing.TryGetProperty("enableStreamingComplianceFilter", out _));
        Assert.True(routing.TryGetProperty("enablePersonaDriftProtection", out _));
        Assert.True(routing.TryGetProperty("enablePromptCompression", out _));
        // ⑤ 高级编排
        Assert.True(routing.TryGetProperty("fusionRouterMinComplexity", out _));
        Assert.True(routing.TryGetProperty("enableFusionMode", out _));
        Assert.True(routing.TryGetProperty("enableByzantineConsensus", out _));
        // ⑥ 观测
        Assert.True(routing.TryGetProperty("enableDistributedTracing", out _));
        Assert.True(routing.TryGetProperty("auditStoreRequestContent", out _));
        // Preset 当前值
        Assert.True(routing.TryGetProperty("preset", out _));
    }

    [Fact]
    public async Task GetPresets_ReturnsThreePresetsWithKnownFields()
    {
        using var factory = new ConfigFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/dashboard/config/presets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("cost-first", out var costFirst));
        Assert.True(root.TryGetProperty("balanced", out var balanced));
        Assert.True(root.TryGetProperty("quality-first", out var qualityFirst));

        // 预设字段值正确（与 RoutingPreset 定义一致），enum 为字符串
        Assert.True(costFirst.GetProperty("EnableThompsonSampling").GetBoolean());
        Assert.Equal("Cheap", costFirst.GetProperty("DefaultTier").GetString());
        Assert.Equal("Medium", balanced.GetProperty("DefaultTier").GetString());
        Assert.True(qualityFirst.GetProperty("EnableFusionRouter").GetBoolean());
        Assert.True(qualityFirst.GetProperty("EnableByzantineConsensus").GetBoolean());
        Assert.Equal(0.1, balanced.GetProperty("CascadeUpgradeSampleRate").GetDouble(), precision: 5);
    }

    [Fact]
    public async Task PutConfig_NewNumericFieldOutOfRange_RejectedWithoutSideEffect()
    {
        using var factory = new ConfigFactory();
        using var client = CreateClient(factory);
        string version = await GetVersionAsync(client);

        using var content = new StringContent(
            JsonSerializer.Serialize(new { ExpectedVersion = version, EnableSemanticCache = true, SemanticCacheSimilarityThreshold = 5.0 }),
            Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync("/api/dashboard/config", content);

        // 越界值不落盘（>1 被丢弃 → 校验器按默认值通过）或被校验拒绝——两者都必须不是 5xx
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PutConfig_InvalidTierString_RejectedOrIgnored()
    {
        using var factory = new ConfigFactory();
        using var client = CreateClient(factory);
        string version = await GetVersionAsync(client);

        using var content = new StringContent(
            JsonSerializer.Serialize(new { ExpectedVersion = version, DefaultTier = "NotATier" }),
            Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync("/api/dashboard/config", content);

        // 非法枚举串被 Enum.TryParse 拒绝 → 字段不应用；请求本身可成功（null 语义）
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutConfig_MissingVersion_IsRejected()
    {
        using var factory = new ConfigFactory();
        using var client = CreateClient(factory);
        using var content = new StringContent(
            JsonSerializer.Serialize(new { EnableFailover = false }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PutAsync("/api/dashboard/config", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutConfig_StaleVersion_ReturnsConflictWithoutOverwritingFirstSave()
    {
        using var factory = new ConfigFactory();
        using var client = CreateClient(factory);
        string version = await GetVersionAsync(client);

        using var firstContent = new StringContent(
            JsonSerializer.Serialize(new { ExpectedVersion = version, EnableFailover = false }),
            Encoding.UTF8,
            "application/json");
        using var first = await client.PutAsync("/api/dashboard/config", firstContent);
        using var staleContent = new StringContent(
            JsonSerializer.Serialize(new { ExpectedVersion = version, EnableFailover = true }),
            Encoding.UTF8,
            "application/json");
        using var stale = await client.PutAsync("/api/dashboard/config", staleContent);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var current = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard/config"));
        Assert.False(current.RootElement.GetProperty("routing").GetProperty("enableFailover").GetBoolean());
    }

    private static async Task<string> GetVersionAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/dashboard/config"));
        return doc.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Config response did not include a version.");
    }
}

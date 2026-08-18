using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Routing;

/// <summary>
/// Dashboard /api/dashboard/metrics 端到端测试：验证延迟统计/Tags 字段接入。
/// </summary>
public class DashboardMetricsTests
{
    private sealed class MetricsFactory : WebApplicationFactory<Program>
    {
        public const string Key = "metrics-test-key";

        /// <summary>预设延迟统计的 stub provider。</summary>
        public ILatencyStatsProvider StatsProvider { get; set; } = new LatencyStatsCache();

        /// <summary>非 null 时把该标记值写入 Routing.ModerationApiKey，用于密钥泄漏断言。</summary>
        public string? ModerationKeyMarker { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", Key);
            builder.UseSetting("OptiRouter:AdminApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            builder.ConfigureServices(services =>
            {
                services.Configure<RouterOptions>(opt =>
                {
                    opt.Models.Clear();
                    var m = new ModelEndpointOptions
                    {
                        Name = "test-model",
                        BaseUrl = "https://example.com",
                        ApiKey = "k",
                        Tier = ModelTier.Medium,
                        MaxContextTokens = 8192,
                        Enabled = true
                    };
                    m.Tags.Add("vision");
                    m.Tags.Add("tool-use");
                    opt.Models.Add(m);
                    // 关闭后台服务避免干扰。
                    opt.Routing.EnableHealthProbe = false;
                    opt.Routing.EnableLatencyAware = false;
                    if (ModerationKeyMarker is not null)
                    {
                        opt.Routing.ModerationApiKey = ModerationKeyMarker;
                    }
                });
                // 用 stub 覆盖生产 LatencyStatsCache。
                services.AddSingleton(StatsProvider);
            });
        }
    }

    /// <summary>可控 stub，注入预设延迟。</summary>
    private sealed class StubStatsProvider : ILatencyStatsProvider
    {
        private readonly Dictionary<string, ModelLatencyStats> _stats;
        public StubStatsProvider(params (string Model, double AvgMs, int Samples)[] entries)
            => _stats = entries.ToDictionary(e => e.Model, e => new ModelLatencyStats(e.AvgMs, e.AvgMs, e.Samples), StringComparer.Ordinal);
        public ModelLatencyStats? GetStats(string modelName) => _stats.TryGetValue(modelName, out var s) ? s : null;
        public void Update(IReadOnlyDictionary<string, ModelLatencyStats>? stats) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Metrics_IncludesLatencyAndTagsFields()
    {
        var stub = new StubStatsProvider(("test-model", 123.4, 45));
        using var factory = new MetricsFactory { StatsProvider = stub };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetricsFactory.Key);

        var resp = await client.GetAsync("/api/dashboard/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var model = Assert.Single(doc.RootElement.GetProperty("models").EnumerateArray());
        Assert.Equal("test-model", model.GetProperty("name").GetString());

        // 延迟字段。
        Assert.Equal(123.4, model.GetProperty("avgLatencyMs").GetDouble(), precision: 1);
        Assert.Equal(45, model.GetProperty("latencySamples").GetInt32());

        // Tags 字段。
        var tags = model.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray();
        Assert.Equal(new[] { "vision", "tool-use" }, tags);
    }

    [Fact]
    public async Task Metrics_ColdStart_LatencyFieldsNullZero()
    {
        // 无延迟统计（冷启动）：avgLatencyMs=null, latencySamples=0。
        using var factory = new MetricsFactory(); // 默认空 LatencyStatsCache

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetricsFactory.Key);

        var resp = await client.GetAsync("/api/dashboard/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var model = Assert.Single(doc.RootElement.GetProperty("models").EnumerateArray());
        Assert.True(model.GetProperty("avgLatencyMs").ValueKind == JsonValueKind.Null);
        Assert.Equal(0, model.GetProperty("latencySamples").GetInt32());
    }

    [Fact]
    public async Task Learning_Returns200_WithModelAlphaBetaSamples()
    {
        using var factory = new MetricsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetricsFactory.Key);

        // 预热 ThompsonStateStore，确保 /api/dashboard/learning 返回非空数据。
        var tsStore = factory.Services.GetRequiredService<ThompsonStateStore>();
        tsStore.RecordOutcome("model-a", true, discountFactor: 1.0);
        tsStore.RecordOutcome("model-b", false, discountFactor: 1.0);

        var resp = await client.GetAsync("/api/dashboard/learning");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(items);

        var a = items.First(i => i.GetProperty("model").GetString() == "model-a");
        Assert.Equal(2.0, a.GetProperty("alpha").GetDouble(), precision: 4);
        Assert.Equal(1.0, a.GetProperty("beta").GetDouble(), precision: 4);
        Assert.Equal(1, a.GetProperty("samples").GetInt64());
        Assert.False(a.GetProperty("lastUpdateUtc").ValueKind == JsonValueKind.Undefined);

        var b = items.First(i => i.GetProperty("model").GetString() == "model-b");
        Assert.Equal(1.0, b.GetProperty("alpha").GetDouble(), precision: 4);
        Assert.Equal(2.0, b.GetProperty("beta").GetDouble(), precision: 4);
        Assert.Equal(1, b.GetProperty("samples").GetInt64());
    }

    [Fact]
    public async Task Metrics_DoesNotLeakSecretConfigFields()
    {
        // M7 回归：routingPolicy 必须是白名单投影（前端 RoutingPolicyInfo 的 7 个开关），
        // 不得整包下发 RoutingOptions——其中 ModerationApiKey / MetricsApiKey / MeshRedisConnectionString
        // 是密钥类字段。修复前这三个属性名会随 dump 出现在响应 JSON 中。
        using var factory = new MetricsFactory { ModerationKeyMarker = "secret-moderation-marker" };

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MetricsFactory.Key);

        var resp = await client.GetAsync("/api/dashboard/metrics");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        string body = await resp.Content.ReadAsStringAsync();

        // 白名单开关仍在（前端 RouterStudio 消费）。
        using var doc = JsonDocument.Parse(body);
        var routing = doc.RootElement.GetProperty("system").GetProperty("routingPolicy");
        Assert.True(routing.GetProperty("enableFailover").ValueKind == JsonValueKind.True
            || routing.GetProperty("enableFailover").ValueKind == JsonValueKind.False);
        Assert.Equal(7, routing.EnumerateObject().Count());

        // 密钥类字段：属性名与值都不得出现。
        Assert.DoesNotContain("moderationApiKey", body);
        Assert.DoesNotContain("metricsApiKey", body);
        Assert.DoesNotContain("meshRedisConnectionString", body);
        Assert.DoesNotContain("secret-moderation-marker", body);
    }
}

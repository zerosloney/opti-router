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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("OptiRouter:ProxyApiKey", Key);
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
}

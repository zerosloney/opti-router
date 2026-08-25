using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 本轮新增 Dashboard 功能的集成测试：
/// 审计日志筛选/导出、配置变更审计、告警历史、学习状态重置、Webhook 配置 PUT 往返、租户用量端点。
/// </summary>
public class DashboardFeatureTests
{
    private sealed class FeatureFactory(bool enableResponseCache = false) : WebApplicationFactory<Program>
    {
        public const string Key = "feature-test-key";

        private readonly string _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "optirouter-feature-test-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempRoot);
            File.Copy(FindSourceAppsettings(), Path.Combine(_tempRoot, "appsettings.json"));
            builder.UseSetting(WebHostDefaults.ContentRootKey, _tempRoot);
            builder.UseSetting("OptiRouter:AdminApiKey", Key);
            builder.UseSetting("OptiRouter:RequestsPerMinute", "600");
            builder.UseSetting("OptiRouter:ConfigDbPath", Path.Combine(_tempRoot, "optirouter-config.db"));
            builder.UseSetting("OptiRouter:Budget:UsePersistentStore", "false");
            if (enableResponseCache)
                builder.UseSetting("OptiRouter:Routing:EnableResponseCache", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveBackgroundServices();
                services.UseFixedTenantKey("feature-test-key");
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

        private static string FindSourceAppsettings()
            => Path.Combine(AppContext.BaseDirectory, "RepositoryFiles", "appsettings.json");
    }

    private static HttpClient CreateClient(FeatureFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FeatureFactory.Key);
        return client;
    }

    private static RequestAuditRecord Record(
        string requestId, string model = "test-model", bool success = true, DateTime? timestamp = null)
        => new(
            Timestamp: timestamp ?? DateTime.UtcNow,
            RequestId: requestId,
            Model: model,
            EstimatedInputTokens: 10,
            PromptTokens: 10,
            CompletionTokens: 5,
            Cost: 0.001m,
            LatencyMs: 100,
            SessionId: null,
            RoutingReason: "test",
            Success: success,
            ErrorMessage: null,
            IsStreaming: false);

    private static async Task<string> GetVersionAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/dashboard/config");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("version").GetString()!;
    }

    // ── 审计日志筛选与导出 ────────────────────────────────────────

    [Fact]
    public async Task Requests_QueryFilter_MatchesRequestIdSubstring()
    {
        using var factory = new FeatureFactory();
        var audit = factory.Services.GetRequiredService<IRequestAuditStore>();
        audit.Append(Record("req-alpha-123"));
        audit.Append(Record("req-beta-456"));
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/requests?q=ALPHA");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("req-alpha-123", items[0].GetProperty("requestId").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Requests_TimeRangeFilter_ExcludesOutsideWindow()
    {
        using var factory = new FeatureFactory();
        var audit = factory.Services.GetRequiredService<IRequestAuditStore>();
        audit.Append(Record("req-old", timestamp: DateTime.UtcNow.AddHours(-3)));
        audit.Append(Record("req-new", timestamp: DateTime.UtcNow.AddMinutes(-1)));
        using var client = CreateClient(factory);

        string from = DateTime.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        using var response = await client.GetAsync($"/api/dashboard/requests?from={from}");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("req-new", items[0].GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Requests_ExportCsv_IncludesHeaderAndRows()
    {
        using var factory = new FeatureFactory();
        var audit = factory.Services.GetRequiredService<IRequestAuditStore>();
        audit.Append(Record("req-export-1"));
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/requests/export?q=export-1");
        response.EnsureSuccessStatusCode();

        string csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("timestamp_utc,request_id,trace_id,model", csv, StringComparison.Ordinal);
        Assert.Contains("req-export-1", csv, StringComparison.Ordinal);
    }

    // ── 费用趋势：归档 + 当日实时 ─────────────────────────────────

    [Fact]
    public async Task Trends_MergesArchivedDaysWithTodayLiveSpend_InDateAmountShape()
    {
        using var factory = new FeatureFactory();
        var ledger = factory.Services.GetRequiredService<ICostLedgerStore>();
        DateTime today = DateTime.UtcNow.Date;
        DateTime yesterday = today.AddDays(-1);
        ledger.AddDaily(yesterday, 2m);
        ledger.SnapshotDaily(yesterday);          // 模拟跨天归档（仅过去日期可入档）
        ledger.AddDaily(today, 1.25m);            // 当日实时累计（从未入档）
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/trends?days=7");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // 修复前两处缺陷都会让本断言失败：
        // 1) 元组直接 Results.Json 输出 [{}]（Item1/Item2 是字段，STJ 不序列化）；
        // 2) 当日实时花费永不进趋势（只读归档表）。
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());
        Assert.Equal(yesterday.ToString("yyyy-MM-dd"), root[0].GetProperty("date").GetString());
        Assert.Equal(2m, root[0].GetProperty("amount").GetDecimal());
        Assert.Equal(today.ToString("yyyy-MM-dd"), root[1].GetProperty("date").GetString());
        Assert.Equal(1.25m, root[1].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Trends_EmptyHistory_StillShowsTodayLiveSpend()
    {
        using var factory = new FeatureFactory();
        var ledger = factory.Services.GetRequiredService<ICostLedgerStore>();
        ledger.AddDaily(DateTime.UtcNow, 0.75m);  // 无任何归档、当天有消费（服务首日场景）
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/trends?days=7");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var root = doc.RootElement;
        Assert.Equal(1, root.GetArrayLength());
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), root[0].GetProperty("date").GetString());
        Assert.Equal(0.75m, root[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ResponseCacheState_EnabledFlagReflectsRoutingOptions()
    {
        // 关闭（默认）：卡片文案需区分"未开启"与"已开启但无 eligible 流量"
        using (var factory = new FeatureFactory(enableResponseCache: false))
        {
            using var client = CreateClient(factory);
            using var response = await client.GetAsync("/api/dashboard/state/response-cache");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("sets").GetInt64());
        }

        // 开启：enabled=true，无流量时计数仍全 0（网关内缓存仅非流式请求参与）
        using (var factory = new FeatureFactory(enableResponseCache: true))
        {
            using var client = CreateClient(factory);
            using var response = await client.GetAsync("/api/dashboard/state/response-cache");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("hits").GetInt64());
            Assert.Equal(0, doc.RootElement.GetProperty("misses").GetInt64());
        }
    }

    // ── 配置变更审计 + Webhook/Fusion 配置往返 ─────────────────────

    [Fact]
    public async Task ConfigPut_RecordsChangeHistory_AndExposesNewFields()
    {
        using var factory = new FeatureFactory();
        using var client = CreateClient(factory);
        string version = await GetVersionAsync(client);

        using var content = new StringContent(
            JsonSerializer.Serialize(new
            {
                ExpectedVersion = version,
                AlertWebhookUrl = "https://hooks.example.com/alert",
                AlertWebhookIntervalSeconds = 15,
                EnableCapabilityFilter = true,
                FusionRouterAnalystModel = "test-model",
                AuditRetentionHours = 72
            }),
            Encoding.UTF8,
            "application/json");
        using var putResponse = await client.PutAsync("/api/dashboard/config", content);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        // GET 暴露新字段
        using var getResponse = await client.GetAsync("/api/dashboard/config");
        getResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        var routing = doc.RootElement.GetProperty("routing");
        Assert.Equal("https://hooks.example.com/alert", routing.GetProperty("alertWebhookUrl").GetString());
        Assert.Equal(15, routing.GetProperty("alertWebhookIntervalSeconds").GetInt32());
        Assert.True(routing.GetProperty("enableCapabilityFilter").GetBoolean());
        Assert.Equal("test-model", routing.GetProperty("fusionRouterAnalystModel").GetString());
        Assert.Equal(72, routing.GetProperty("auditRetentionHours").GetInt32());

        // 变更历史记录了落库 diff
        using var historyResponse = await client.GetAsync("/api/dashboard/config/history");
        historyResponse.EnsureSuccessStatusCode();
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var first = history.RootElement[0];
        Assert.Equal("admin", first.GetProperty("actor").GetString());
        string changes = first.GetProperty("changes").GetRawText();
        Assert.Contains("Routing:AlertWebhookUrl", changes, StringComparison.Ordinal);
        Assert.Contains("Routing:EnableCapabilityFilter", changes, StringComparison.Ordinal);
        Assert.Contains("Routing:AuditRetentionHours", changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigPut_AuditRetentionHoursZero_PersistsPermanentRetention()
    {
        // 0 = 永久保留是合法值（validator 允许 >= 0），持久化与应用两侧都必须接受，
        // 防止"运行时生效、重启回跳"的半生效状态回归。
        using var factory = new FeatureFactory();
        using var client = CreateClient(factory);
        string version = await GetVersionAsync(client);

        using var content = new StringContent(
            JsonSerializer.Serialize(new { ExpectedVersion = version, AuditRetentionHours = 0 }),
            Encoding.UTF8,
            "application/json");
        using var putResponse = await client.PutAsync("/api/dashboard/config", content);
        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);

        using var getResponse = await client.GetAsync("/api/dashboard/config");
        getResponse.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("routing").GetProperty("auditRetentionHours").GetInt32());

        // 变更历史必须记录该 diff（此前持久化条件 >= 1 会静默跳过 0）。
        using var historyResponse = await client.GetAsync("/api/dashboard/config/history");
        historyResponse.EnsureSuccessStatusCode();
        using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        Assert.Contains("Routing:AuditRetentionHours", history.RootElement[0].GetProperty("changes").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigHistory_EmptyWhenNoChanges()
    {
        using var factory = new FeatureFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/config/history");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ── 告警历史 ──────────────────────────────────────────────────

    [Fact]
    public async Task AlertsHistory_ReturnsRecordedEvents()
    {
        using var factory = new FeatureFactory();
        factory.Services.GetRequiredService<AlertHistory>().Record(new AlertEvent(
            DateTimeOffset.UtcNow, "alert", "budget-warning", "warning", "budget", "near limit"));
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/alerts/history");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var evt = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal("alert", evt.GetProperty("eventType").GetString());
        Assert.Equal("budget-warning", evt.GetProperty("alertId").GetString());
    }

    // ── 学习状态重置 ──────────────────────────────────────────────

    [Fact]
    public async Task LearningReset_ClearsThompsonState()
    {
        using var factory = new FeatureFactory();
        var tsStore = factory.Services.GetRequiredService<ThompsonStateStore>();
        tsStore.RecordOutcome("test-model", isGood: true, discountFactor: 0.95);
        tsStore.RecordOutcome("test-model", isGood: true, discountFactor: 0.95);
        Assert.Single(tsStore.GetSnapshot());
        using var client = CreateClient(factory);

        using var response = await client.PostAsync("/api/dashboard/learning/reset", content: null);
        response.EnsureSuccessStatusCode();

        Assert.Empty(tsStore.GetSnapshot());
    }

    [Fact]
    public async Task LearningExport_ReturnsCsv()
    {
        using var factory = new FeatureFactory();
        var tsStore = factory.Services.GetRequiredService<ThompsonStateStore>();
        tsStore.RecordOutcome("test-model", isGood: true, discountFactor: 0.95);
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/learning/export");
        response.EnsureSuccessStatusCode();

        string csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("model,alpha,beta,mean_reward,samples,last_update_utc", csv, StringComparison.Ordinal);
        Assert.Contains("test-model", csv, StringComparison.Ordinal);
    }

    // ── 租户用量端点（本轮接入前端前的回归保护）────────────────────

    [Fact]
    public async Task KeysUsage_EndpointReturnsArray()
    {
        using var factory = new FeatureFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/keys/usage");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task KeysUsage_Export_ReturnsCsvHeader()
    {
        using var factory = new FeatureFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync("/api/dashboard/keys/usage/export");
        response.EnsureSuccessStatusCode();

        string csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("key_id,key_prefix,tenant_name,daily_budget_usd", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CircuitOverride_WithSlashInModelName_ProperlyUnescapesAndOverrides()
    {
        using var factory = new FeatureFactory();
        var tracker = factory.Services.GetRequiredService<ModelHealthTracker>();
        using var client = CreateClient(factory);

        string escapedName = Uri.EscapeDataString("stealth/ox-alpha-01");
        var content = new StringContent(
            JsonSerializer.Serialize(new { TargetState = "Open" }),
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync($"/api/dashboard/circuits/{escapedName}/override", content);
        response.EnsureSuccessStatusCode();

        Assert.Equal(CircuitState.Open, tracker.GetState("stealth/ox-alpha-01"));
    }
}

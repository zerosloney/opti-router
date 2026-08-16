using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Routing;
using Xunit;

namespace OptiRouter.Tests.Health;

public sealed class AlertWebhookNotifierTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<string> RequestBodies { get; } = new();
        public int CallCount => RequestBodies.Count;

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responses.Enqueue(responder);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty;
            RequestBodies.Add(body);
            var responder = _responses.Count > 0 ? _responses.Dequeue() : _ => new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<RouterOptions>
    {
        public RouterOptions CurrentValue { get; set; } = new();
        public RouterOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<RouterOptions, string?> listener) => null;
    }

    private static (AlertWebhookNotifier Notifier, FakeHttpMessageHandler Handler, TestOptionsMonitor Options) Create(
        List<AlertRecord> alerts)
    {
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var options = new TestOptionsMonitor();
        options.CurrentValue.Routing.AlertWebhookUrl = "https://hooks.example.com/alert";
        var notifier = new AlertWebhookNotifier(() => alerts, new HttpClient(handler), options);
        return (notifier, handler, options);
    }

    private static AlertRecord Alert(string id, string level = "warning", string message = "test alert") =>
        new(id, level, "budget", message, DateTime.UtcNow);

    [Fact]
    public async Task Tick_NewAlert_PushesAlertEvent()
    {
        var alerts = new List<AlertRecord> { Alert("budget-warning", message: "Daily budget near limit: $80.00 / $100.00") };
        var (notifier, handler, _) = Create(alerts);

        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);

        var push = Assert.Single(handler.RequestBodies);
        using var doc = JsonDocument.Parse(push);
        Assert.Equal("alert", doc.RootElement.GetProperty("eventType").GetString());
        var alert = doc.RootElement.GetProperty("alert");
        Assert.Equal("budget-warning", alert.GetProperty("id").GetString());
        Assert.Equal("warning", alert.GetProperty("level").GetString());
        Assert.Contains("budget", alert.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Tick_SameAlert_IsNotPushedTwice()
    {
        var alerts = new List<AlertRecord> { Alert("budget-warning") };
        var (notifier, handler, _) = Create(alerts);

        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);
        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);

        Assert.Single(handler.RequestBodies); // 去重：同一告警只推一次
    }

    [Fact]
    public async Task Tick_RecoveredAlert_PushesResolvedEvent()
    {
        var alerts = new List<AlertRecord> { Alert("circuit-open-gpt-4o", level: "critical") };
        var (notifier, handler, _) = Create(alerts);

        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(1, handler.CallCount);

        // 告警消失 → 下一周期推送 resolved
        alerts.Clear();
        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        using var doc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("resolved", doc.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("circuit-open-gpt-4o", doc.RootElement.GetProperty("alert").GetProperty("id").GetString());
        Assert.Contains("Recovered", doc.RootElement.GetProperty("alert").GetProperty("message").GetString());
    }

    [Fact]
    public async Task PushFailure_KeepsPendingAndRetriesNextCycle()
    {
        var alerts = new List<AlertRecord> { Alert("budget-exhausted", level: "critical") };
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)); // 第一周期 500
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK));                  // 重试成功
        var options = new TestOptionsMonitor();
        options.CurrentValue.Routing.AlertWebhookUrl = "https://hooks.example.com/alert";
        var notifier = new AlertWebhookNotifier(() => alerts, new HttpClient(handler), options);

        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(1, handler.CallCount);

        // 失败后队首保留 → 下周期重试成功
        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);
        Assert.Equal(2, handler.CallCount);

        using var doc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("alert", doc.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("budget-exhausted", doc.RootElement.GetProperty("alert").GetProperty("id").GetString());
    }

    [Fact]
    public async Task NoWebhookUrl_DoesNotPush()
    {
        var alerts = new List<AlertRecord> { Alert("budget-warning") };
        var (notifier, handler, options) = Create(alerts);
        options.CurrentValue.Routing.AlertWebhookUrl = string.Empty;

        notifier.Tick();
        await notifier.DrainPendingAsync(CancellationToken.None);

        Assert.Empty(handler.RequestBodies);
    }
}

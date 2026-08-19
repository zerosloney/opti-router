using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Endpoints;
using Xunit;

namespace OptiRouter.Tests.Endpoints;

/// <summary>
/// 测试用 IOptionsMonitor：手动触发 OnChange，模拟配置 reload。
/// </summary>
internal sealed class FakeRouterOptionsMonitor : IOptionsMonitor<RouterOptions>
{
    private readonly List<Action<RouterOptions, string?>> _listeners = new();
    private RouterOptions _current;

    public FakeRouterOptionsMonitor(RouterOptions initial)
    {
        _current = initial;
    }

    public RouterOptions CurrentValue => _current;

    public RouterOptions Get(string? name) => _current;

    public IDisposable OnChange(Action<RouterOptions, string?> listener)
    {
        lock (_listeners)
        {
            _listeners.Add(listener);
        }
        return NullDisposable.Instance;
    }

    /// <summary>
    /// 模拟配置 reload：更新 CurrentValue 并派发 OnChange 回调。
    /// </summary>
    public void Change(RouterOptions next)
    {
        Action<RouterOptions, string?>[] snapshot;
        lock (_listeners)
        {
            _current = next;
            snapshot = _listeners.ToArray();
        }
        foreach (var listener in snapshot)
        {
            listener(next, null);
        }
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// 记录自身是否被释放的 HttpClient，用于验证退役客户端的延迟释放。
/// </summary>
internal sealed class TrackingHttpClient : HttpClient
{
    public TrackingHttpClient(HttpMessageHandler handler)
        : base(handler, disposeHandler: false)
    {
    }

    public bool WasDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// ModelClientProvider 单元测试：按名缓存、OnChange 热更新（客户端构造/请求相关字段触发重建）、
/// 退役客户端宽限期释放、Dispose 语义。
/// </summary>
public sealed class ModelClientProviderTests
{
    private readonly List<TrackingHttpClient> _createdHttpClients = new();

    private ModelClientProvider CreateProvider(RouterOptions options, TimeSpan? retirementGrace = null)
        => CreateProvider(new FakeRouterOptionsMonitor(options), retirementGrace);

    private ModelClientProvider CreateProvider(FakeRouterOptionsMonitor monitor, TimeSpan? retirementGrace = null)
    {
        return new ModelClientProvider(
            new ModelClientFactory(),
            monitor,
            retirementGrace,
            handler =>
            {
                var http = new TrackingHttpClient(handler);
                lock (_createdHttpClients)
                {
                    _createdHttpClients.Add(http);
                }
                return http;
            });
    }

    private static ModelEndpointOptions Endpoint(
        string name = "gpt-4o",
        string baseUrl = "https://api.openai.com/v1",
        string apiKey = "sk-test",
        int timeoutSeconds = 120) => new()
        {
            Name = name,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            TimeoutSeconds = timeoutSeconds
        };

    private static RouterOptions OptionsWith(params ModelEndpointOptions[] models)
    {
        var options = new RouterOptions();
        foreach (var model in models)
        {
            options.Models.Add(model);
        }
        return options;
    }

    [Fact]
    public void GetClient_SameName_ReturnsCachedInstance()
    {
        using var provider = CreateProvider(OptionsWith(Endpoint()));

        var first = provider.GetClient(Endpoint());
        var second = provider.GetClient(Endpoint());

        Assert.Same(first, second);
        Assert.Single(_createdHttpClients);
    }

    [Fact]
    public void GetClient_DifferentNames_CreatesSeparateClients()
    {
        using var provider = CreateProvider(OptionsWith(Endpoint("model-a"), Endpoint("model-b")));

        var a = provider.GetClient(Endpoint("model-a"));
        var b = provider.GetClient(Endpoint("model-b"));

        Assert.NotSame(a, b);
        Assert.Equal(2, _createdHttpClients.Count);
    }

    [Fact]
    public void Change_ConnectionUnchanged_KeepsCachedClient()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint()));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(Endpoint());

        // reload 一份字段完全相同的配置（Routing 开关变化也不应触发重建）。
        var reloaded = OptionsWith(Endpoint());
        reloaded.Routing.EnableFailover = false;
        monitor.Change(reloaded);

        var after = provider.GetClient(Endpoint());
        Assert.Same(before, after);
    }

    [Fact]
    public void Change_BaseUrlChanged_RebuildsClient()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint()));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(Endpoint());

        monitor.Change(OptionsWith(Endpoint(baseUrl: "https://other.example.com/v1")));

        var after = provider.GetClient(Endpoint(baseUrl: "https://other.example.com/v1"));
        Assert.NotSame(before, after);
        Assert.Equal(2, _createdHttpClients.Count);
        Assert.Equal(new Uri("https://other.example.com/v1/"), _createdHttpClients[1].BaseAddress);
    }

    [Fact]
    public void Change_ApiKeyChanged_RebuildsClient()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint(apiKey: "sk-old")));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(Endpoint(apiKey: "sk-old"));

        monitor.Change(OptionsWith(Endpoint(apiKey: "sk-new")));

        var after = provider.GetClient(Endpoint(apiKey: "sk-new"));
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Change_TimeoutChanged_RebuildsClient()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint(timeoutSeconds: 120)));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(Endpoint(timeoutSeconds: 120));

        monitor.Change(OptionsWith(Endpoint(timeoutSeconds: 30)));

        var after = provider.GetClient(Endpoint(timeoutSeconds: 30));
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Change_UpstreamModelIdChanged_RebuildsClient()
    {
        var initial = Endpoint();
        initial.Id = "upstream-v1";
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(initial));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(initial);

        var reloaded = Endpoint();
        reloaded.Id = "upstream-v2";
        monitor.Change(OptionsWith(reloaded));

        var after = provider.GetClient(reloaded);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Change_ProtocolChanged_RebuildsClient()
    {
        var initial = Endpoint();
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(initial));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(initial);

        var reloaded = Endpoint();
        reloaded.Protocol = ProviderProtocol.Anthropic;
        monitor.Change(OptionsWith(reloaded));

        var after = provider.GetClient(reloaded);
        Assert.NotSame(before, after);
        Assert.IsType<AnthropicModelClient>(after);
    }

    [Fact]
    public void Change_MaxRetriesChanged_RebuildsClient()
    {
        var initial = Endpoint();
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(initial));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(initial);

        var reloaded = Endpoint();
        reloaded.MaxRetries = 2;
        monitor.Change(OptionsWith(reloaded));

        var after = provider.GetClient(reloaded);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Change_TierOrPriceChanged_KeepsCachedClient()
    {
        // Tier/价格/能力/上下文长度由路由引擎每请求读取 CurrentValue，不属于客户端配置，不应触发重建。
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint()));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(Endpoint());

        var reloadedModel = Endpoint();
        reloadedModel.Tier = ModelTier.Strong;
        reloadedModel.InputPricePerMillion = 99m;
        reloadedModel.MaxContextTokens = 200000;
        reloadedModel.Capabilities["coding"] = 0.99;
        monitor.Change(OptionsWith(reloadedModel));

        var after = provider.GetClient(reloadedModel);
        Assert.Same(before, after);
    }

    [Fact]
    public void Change_ModelRemoved_RetiresClient_NextGetRebuilds()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint("model-a"), Endpoint("model-b")));
        using var provider = CreateProvider(monitor);
        var before = provider.GetClient(Endpoint("model-a"));

        // reload 后 model-a 被移出配置。
        monitor.Change(OptionsWith(Endpoint("model-b")));

        // 若仍有请求传入该端点对象，则按传入配置重建（不再命中缓存）。
        var after = provider.GetClient(Endpoint("model-a"));
        Assert.NotSame(before, after);
    }

    [Fact]
    public void RetiredClient_Disposed_AfterGraceElapsed()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint()));
        using var provider = CreateProvider(monitor, retirementGrace: TimeSpan.FromMilliseconds(1));

        provider.GetClient(Endpoint());
        Assert.Single(_createdHttpClients);
        var oldHttp = _createdHttpClients[0];

        monitor.Change(OptionsWith(Endpoint(baseUrl: "https://other.example.com/v1")));
        Thread.Sleep(30); // 越过宽限期
        provider.GetClient(Endpoint(baseUrl: "https://other.example.com/v1")); // 触发惰性清理

        Assert.True(oldHttp.WasDisposed);
        Assert.Equal(2, _createdHttpClients.Count);
        Assert.False(_createdHttpClients[1].WasDisposed);
    }

    [Fact]
    public void RetiredClient_NotDisposed_WithinGracePeriod()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint()));
        using var provider = CreateProvider(monitor, retirementGrace: TimeSpan.FromHours(1));

        provider.GetClient(Endpoint());
        var oldHttp = _createdHttpClients[0];

        monitor.Change(OptionsWith(Endpoint(baseUrl: "https://other.example.com/v1")));
        provider.GetClient(Endpoint(baseUrl: "https://other.example.com/v1"));

        Assert.False(oldHttp.WasDisposed);
    }

    [Fact]
    public void Dispose_DisposesActiveAndRetiredClients()
    {
        var monitor = new FakeRouterOptionsMonitor(OptionsWith(Endpoint()));
        var provider = CreateProvider(monitor, retirementGrace: TimeSpan.FromHours(1));

        provider.GetClient(Endpoint());
        monitor.Change(OptionsWith(Endpoint(baseUrl: "https://other.example.com/v1")));
        provider.GetClient(Endpoint(baseUrl: "https://other.example.com/v1"));
        Assert.Equal(2, _createdHttpClients.Count);

        provider.Dispose();

        Assert.True(_createdHttpClients[0].WasDisposed); // 退役的
        Assert.True(_createdHttpClients[1].WasDisposed); // 活跃的

        Assert.Throws<ObjectDisposedException>(() => provider.GetClient(Endpoint()));
    }

    [Fact]
    public void GetClient_Concurrent_SharesSingleInstance()
    {
        using var provider = CreateProvider(OptionsWith(Endpoint()));

        var results = new IModelClient[32];
        Parallel.For(0, results.Length, i => results[i] = provider.GetClient(Endpoint()));

        Assert.All(results, client => Assert.Same(results[0], client));
        Assert.Single(_createdHttpClients);
    }
}

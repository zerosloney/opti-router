using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// 生产环境 <see cref="IModelClientProvider"/> 实现，按模型名缓存客户端实例，支持端点配置热更新。
/// <para>
/// 热更新：订阅 <see cref="IOptionsMonitor{TOptions}"/> 的 OnChange。配置 reload 后逐项比对 Models——
/// 客户端构造或请求相关字段（BaseUrl/ApiKey/TimeoutSeconds/UpstreamModelId/Protocol/MaxRetries）
/// 有变化、或模型被移出配置的，退役其缓存客户端并按新配置重建；Name 改名由缓存移除/新增逻辑处理；
/// 其余字段（Tier/价格/MaxContextTokens/Enabled/能力等）变化不触发重建，它们经路由引擎每请求读取 CurrentValue 直接生效。
/// </para>
/// <para>
/// 退役的客户端不立即释放（避免打断在途请求）：保留至退役宽限期（默认 2 分钟，覆盖默认单请求超时）
/// 后才释放其 HttpClient。清理在 GetClient / 配置变更 / Dispose 时惰性触发，无后台定时器。
/// </para>
/// </summary>
public sealed class ModelClientProvider : IModelClientProvider, IDisposable
{
    /// <summary>
    /// 退役客户端的默认宽限期：2 分钟（覆盖默认 TimeoutSeconds=120 的在途请求）。
    /// </summary>
    public static readonly TimeSpan DefaultRetirementGrace = TimeSpan.FromMinutes(2);

    private readonly ModelClientFactory _factory;
    private readonly Func<HttpMessageHandler, HttpClient> _httpClientFactory;
    private readonly TimeSpan _retirementGrace;
    private readonly object _gate = new();
    private readonly SocketsHttpHandler _sharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        MaxConnectionsPerServer = 500, // Supports high-concurrency throughput
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        KeepAlivePingPolicy = System.Net.Http.HttpKeepAlivePingPolicy.WithActiveRequests
    };
    private readonly Dictionary<string, CachedClient> _cache = new(StringComparer.Ordinal);
    private readonly List<RetiredGroup> _retired = new();
    private readonly IDisposable? _changeSubscription;
    private IReadOnlyList<ModelEndpointOptions> _lastModels;
    private bool _disposed;

    private sealed class CachedClient
    {
        public required IModelClient Client { get; init; }
        public required HttpClient Http { get; init; }
    }

    private sealed class RetiredGroup
    {
        public required DateTimeOffset RetiredAt { get; init; }
        public required List<CachedClient> Clients { get; init; }
    }

    /// <summary>
    /// 初始化客户端提供者并订阅配置变更。
    /// </summary>
    /// <param name="factory">模型客户端工厂。</param>
    /// <param name="optionsMonitor">配置监视器，用于订阅 OnChange 实现端点配置热更新。</param>
    /// <param name="retirementGrace">退役客户端的释放宽限期，null 用 <see cref="DefaultRetirementGrace"/>。</param>
    /// <param name="httpClientFactory">HttpClient 创建钩子（测试接缝），null 用共享 handler 创建。</param>
    public ModelClientProvider(
        ModelClientFactory factory,
        IOptionsMonitor<RouterOptions> optionsMonitor,
        TimeSpan? retirementGrace = null,
        Func<HttpMessageHandler, HttpClient>? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(optionsMonitor);

        _factory = factory;
        _retirementGrace = retirementGrace ?? DefaultRetirementGrace;
        _httpClientFactory = httpClientFactory ?? (static handler => new HttpClient(handler, disposeHandler: false));
        _lastModels = optionsMonitor.CurrentValue.Models.ToList();
        _changeSubscription = optionsMonitor.OnChange(OnOptionsChanged);
    }

    /// <inheritdoc />
    public IModelClient GetClient(ModelEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SweepRetired_NoLock();

            if (_cache.TryGetValue(endpoint.Name, out var cached))
                return cached.Client;

            // intentional-simple：共享 SocketsHttpHandler，避免 socket 耗尽；
            // disposeHandler: false，handler 生命周期由 provider 管理。
            var httpClient = _httpClientFactory(_sharedHandler);
            var client = _factory.Create(endpoint, httpClient);
            _cache[endpoint.Name] = new CachedClient { Client = client, Http = httpClient };
            return client;
        }
    }

    /// <summary>
    /// 释放配置订阅、全部客户端（含退役未过期的）与共享 handler。
    /// </summary>
    public void Dispose()
    {
        IDisposable? subscription;
        List<HttpClient> httpClients;
        SocketsHttpHandler sharedHandler;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            subscription = _changeSubscription;
            httpClients = new List<HttpClient>(_cache.Count + _retired.Sum(g => g.Clients.Count));
            foreach (var cached in _cache.Values)
                httpClients.Add(cached.Http);
            foreach (var group in _retired)
            {
                foreach (var cached in group.Clients)
                    httpClients.Add(cached.Http);
            }
            _cache.Clear();
            _retired.Clear();
            sharedHandler = _sharedHandler;
        }

        // 订阅释放放在锁外：避免与正在派发回调的 OptionsMonitor 互相持锁等待。
        subscription?.Dispose();
        foreach (var http in httpClients)
            http.Dispose();
        sharedHandler.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnOptionsChanged(RouterOptions newOptions)
    {
        ArgumentNullException.ThrowIfNull(newOptions);

        lock (_gate)
        {
            if (_disposed) return;

            var oldModels = _lastModels;
            var newModels = newOptions.Models.ToList();
            _lastModels = newModels;

            var toRetire = new List<CachedClient>();

            // 客户端构造或请求相关字段变化（或旧配置中不存在该模型）→ 退役旧客户端，
            // 下次 GetClient 按新配置重建。Name 改名由下方缓存移除/新增逻辑处理。
            foreach (var newModel in newModels)
            {
                if (!_cache.TryGetValue(newModel.Name, out var cached)) continue;

                var oldModel = oldModels.FirstOrDefault(
                    m => string.Equals(m.Name, newModel.Name, StringComparison.Ordinal));
                if (oldModel is null || ClientConfigChanged(oldModel, newModel))
                {
                    _cache.Remove(newModel.Name);
                    toRetire.Add(cached);
                }
            }

            // 被移出配置的模型 → 同样退役（先收集再删，避免枚举中修改字典）。
            var removedNames = new List<string>();
            foreach (var name in _cache.Keys)
            {
                bool stillConfigured = newModels.Any(
                    m => string.Equals(m.Name, name, StringComparison.Ordinal));
                if (!stillConfigured) removedNames.Add(name);
            }
            foreach (var name in removedNames)
            {
                toRetire.Add(_cache[name]);
                _cache.Remove(name);
            }

            if (toRetire.Count > 0)
            {
                _retired.Add(new RetiredGroup { RetiredAt = DateTimeOffset.UtcNow, Clients = toRetire });
            }

            SweepRetired_NoLock();
        }
    }

    private static bool ClientConfigChanged(ModelEndpointOptions old, ModelEndpointOptions next) =>
        !string.Equals(old.BaseUrl, next.BaseUrl, StringComparison.Ordinal)
        || !string.Equals(old.ApiKey, next.ApiKey, StringComparison.Ordinal)
        || old.TimeoutSeconds != next.TimeoutSeconds
        || !string.Equals(old.UpstreamModelId, next.UpstreamModelId, StringComparison.Ordinal)
        || old.Protocol != next.Protocol
        || old.MaxRetries != next.MaxRetries;

    private void SweepRetired_NoLock()
    {
        if (_retired.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            if (_retired[i].RetiredAt + _retirementGrace <= now)
            {
                foreach (var cached in _retired[i].Clients)
                    cached.Http.Dispose();
                _retired.RemoveAt(i);
            }
        }
    }
}

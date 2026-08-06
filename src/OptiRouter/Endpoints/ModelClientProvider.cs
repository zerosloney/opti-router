using System.Collections.Concurrent;
using System.Net;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// 生产环境 <see cref="IModelClientProvider"/> 实现，按模型名缓存客户端实例。
/// intentional-simple：每模型缓存一个 IModelClient 实例，生命周期与 provider 同。
/// 配置在启动时 bind 一次；Models 端点（BaseUrl/ApiKey/Timeout/Tier）变更需重启进程生效。
/// Routing 开关经 IOptionsMonitor（ProxyOrchestrator 注入）可在 reload 后生效，与此缓存语义解耦。
/// </summary>
public sealed class ModelClientProvider : IModelClientProvider, IDisposable
{
    private readonly ModelClientFactory _factory;
    private readonly ConcurrentDictionary<string, IModelClient> _cache = new();
    private readonly SocketsHttpHandler _sharedHandler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
    private bool _disposed;

    /// <summary>
    /// 初始化客户端提供者。
    /// </summary>
    /// <param name="factory">模型客户端工厂。</param>
    public ModelClientProvider(ModelClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc />
    public IModelClient GetClient(ModelEndpointOptions endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _cache.GetOrAdd(endpoint.Name, _ =>
        {
            // intentional-simple：共享 SocketsHttpHandler，避免 socket 耗尽；
            // disposeHandler: false，handler 生命周期由 provider 管理。
            var httpClient = new HttpClient(_sharedHandler, disposeHandler: false);
            return _factory.Create(endpoint, httpClient);
        });
    }

    /// <summary>
    /// 释放共享的 SocketsHttpHandler。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sharedHandler.Dispose();
        GC.SuppressFinalize(this);
    }
}

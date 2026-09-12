using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Endpoints;
using OptiRouter.Configuration;
using OptiRouter.Health;
using OptiRouter.Routing;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Exporter;

namespace OptiRouter.Composition;

/// <summary>
/// 上游模型客户端与状态存储的 DI 注册（#4 模块化：组合根拆分）。
/// </summary>
internal static class UpstreamServiceExtensions
{
    /// <summary>模型客户端工厂 + 按名缓存提供者（BaseUrl/ApiKey/Timeout 热更新重建客户端）。</summary>
    public static IServiceCollection AddUpstreamClientServices(this IServiceCollection services)
    {
services.AddSingleton<ModelClientFactory>(sp =>
    new ModelClientFactory(sp.GetService<ILogger<ModelClientFactory>>()));

// 注册模型客户端提供者（生产实现，按模型名缓存 IModelClient）。
// 热更新：内部订阅 IOptionsMonitor.OnChange，BaseUrl/ApiKey/TimeoutSeconds 变化时重建对应客户端，
// 旧客户端保留一段宽限期后释放，不打断在途请求。
services.AddSingleton<IModelClientProvider>(sp => new ModelClientProvider(
    sp.GetRequiredService<ModelClientFactory>(),
    sp.GetRequiredService<IOptionsMonitor<RouterOptions>>(),
    healthTracker: sp.GetRequiredService<ModelHealthTracker>()));
        return services;
    }

    /// <summary>成本账本与请求审计存储（MariaDb/Postgres/Redis/Sqlite/InMemory 按配置切换）。</summary>
    public static IServiceCollection AddStateStoreServices(this IServiceCollection services)
    {
// 成本账本存储：支持 "MariaDb" | "Postgres" | "Redis" | "Sqlite" | "InMemory"。
// 对于 K8s 多节点部署架构，配置 "MariaDb"、"Postgres" 或 "Redis" 即可实现跨节点全局成本计费与断路器共享。
services.AddSingleton<ICostLedgerStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string provider = options.Budget.StoreProvider ?? "Sqlite";

    if (string.Equals(provider, "MariaDb", StringComparison.OrdinalIgnoreCase))
    {
        return new MariaDbCostLedgerStore(options.Budget.MariaDbConnectionString,
            logger: sp.GetService<ILogger<MariaDbCostLedgerStore>>(),
            alertHistory: sp.GetService<OptiRouter.Health.AlertHistory>());
    }
    if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
    {
        return new PostgresCostLedgerStore(options.Budget.PostgresConnectionString,
            logger: sp.GetService<ILogger<PostgresCostLedgerStore>>());
    }
    if (string.Equals(provider, "Redis", StringComparison.OrdinalIgnoreCase))
    {
        return new RedisCostLedgerStore(options.Budget.RedisConnectionString, options.Budget.RedisKeyPrefix,
            logger: sp.GetService<ILogger<RedisCostLedgerStore>>(),
            alertHistory: sp.GetService<OptiRouter.Health.AlertHistory>());
    }
    if (!options.Budget.UsePersistentStore || string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
    {
        return new InMemoryCostLedgerStore();
    }

    string storePath = options.Budget.StorePath;
    string? dir = Path.GetDirectoryName(storePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    return new SqliteCostLedgerStore(storePath);
});

// 请求审计存储：支持 "MariaDb" | "Postgres" | "Sqlite" | "InMemory"。
services.AddSingleton<IRequestAuditStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<RouterOptions>>().Value;
    string provider = options.Budget.StoreProvider ?? "Sqlite";

    if (string.Equals(provider, "MariaDb", StringComparison.OrdinalIgnoreCase))
    {
        return new MariaDbRequestAuditStore(options.Budget.MariaDbConnectionString!,
            sp.GetService<ILogger<MariaDbRequestAuditStore>>());
    }
    if (string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase))
    {
        return new PostgresRequestAuditStore(options.Budget.PostgresConnectionString);
    }
    if (!options.Budget.UsePersistentStore || string.Equals(provider, "InMemory", StringComparison.OrdinalIgnoreCase))
    {
        return new InMemoryRequestAuditStore();
    }

    string storePath = options.Budget.StorePath;
    string? dir = Path.GetDirectoryName(storePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
    {
        Directory.CreateDirectory(dir);
    }
    return new SqliteRequestAuditStore(storePath, sp.GetRequiredService<ILogger<SqliteRequestAuditStore>>());
});
        return services;
    }

    /// <summary>OpenTelemetry OTLP 链路追踪（部署级设置，不随 Routing 落库）。</summary>
    public static IServiceCollection AddTracingServices(this IServiceCollection services, IConfiguration configuration)
    {
// 配置 OpenTelemetry OTLP Exporter 链路追踪导出（无缝对接 Jaeger, Tempo 或 Datadog）。
// OTLP 为部署级设置，保留在 appsettings.json（OptiRouter:Otlp* 顶层键），不随 Routing 落库。
services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        string? otlpServiceName = configuration["OptiRouter:OtlpServiceName"];
        bool enableOtlpTracing = configuration.GetValue<bool?>("OptiRouter:EnableOtlpTracing") ?? false;
        string? otlpEndpoint = configuration["OptiRouter:OtlpEndpoint"];
        string? otlpProtocol = configuration["OptiRouter:OtlpProtocol"];

        tracerProviderBuilder
            .SetResourceBuilder(OpenTelemetry.Resources.ResourceBuilder.CreateDefault().AddService(
                string.IsNullOrEmpty(otlpServiceName) ? "OptiRouter" : otlpServiceName))
            .AddSource("OptiRouter.Tracing");

        if (enableOtlpTracing && !string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracerProviderBuilder.AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Endpoint = new Uri(otlpEndpoint);
                otlpOptions.Protocol = string.Equals(otlpProtocol, "http/protobuf", StringComparison.OrdinalIgnoreCase)
                    ? OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf
                    : OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
            });
        }
    });
        return services;
    }
}

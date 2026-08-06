using Microsoft.Extensions.Diagnostics.HealthChecks;
using OptiRouter.Routing;

namespace OptiRouter.Health;

/// <summary>
/// 成本账本健康检查。验证 <see cref="ICostLedgerStore"/> 连接正常，数据库可读写。
/// 不探测上游模型 API（成本/限流风险），仅验证自身持久化依赖。
/// </summary>
public sealed class CostLedgerHealthCheck : IHealthCheck
{
    private readonly ICostLedgerStore _store;

    /// <summary>
    /// 初始化健康检查。
    /// </summary>
    /// <param name="store">成本账本存储。</param>
    public CostLedgerHealthCheck(ICostLedgerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 探测 store 可用：读一个不存在的 key，验证连接 alive。
            _store.GetSession("__health_probe__");
            return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Cost ledger store unavailable.", ex));
        }
    }
}
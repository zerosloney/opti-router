using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 降级重试编排器：按 RouterEngine 给出的候选链顺序尝试，成功即返回，
/// 全部失败则抛出 <see cref="AllCandidatesFailedException"/>。
/// 跨请求失败记忆：通过 <see cref="ModelHealthTracker"/> 上报成败，连续失败达阈值的模型被熔断冷却。
/// </summary>
public sealed class ProxyOrchestrator : IAsyncDisposable, IDisposable
{
    private readonly IModelClientProvider _clientProvider;
    private readonly RouterEngine _engine;
    private readonly IOptionsMonitor<RouterOptions> _options;
    private readonly CostLedger _ledger;
    private readonly ModelHealthTracker _healthTracker;
    private readonly ILogger<ProxyOrchestrator> _logger;
    private bool _disposed;

    /// <summary>
    /// 初始化编排器。
    /// </summary>
    /// <param name="clientProvider">模型客户端提供者。</param>
    /// <param name="engine">路由引擎。</param>
    /// <param name="options">路由配置热更新监视器。</param>
    /// <param name="ledger">成本账本。</param>
    /// <param name="healthTracker">跨请求模型健康跟踪器。</param>
    /// <param name="logger">日志记录器。</param>
    public ProxyOrchestrator(
        IModelClientProvider clientProvider,
        RouterEngine engine,
        IOptionsMonitor<RouterOptions> options,
        CostLedger ledger,
        ModelHealthTracker healthTracker,
        ILogger<ProxyOrchestrator> logger)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(healthTracker);
        ArgumentNullException.ThrowIfNull(logger);

        _clientProvider = clientProvider;
        _engine = engine;
        _options = options;
        _ledger = ledger;
        _healthTracker = healthTracker;
        _logger = logger;
    }

    /// <summary>
    /// 非流式发送请求，按候选链顺序尝试，失败则降级到下一候选。
    /// </summary>
    /// <param name="request">聊天请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完整聊天响应。</returns>
    /// <exception cref="BudgetExhaustedException">预算耗尽且模式为 Reject。</exception>
    /// <exception cref="AllCandidatesFailedException">所有候选均失败。</exception>
    public async Task<ChatResponse> SendAsync(ChatRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;
        var failedInThisRequest = new HashSet<string>(StringComparer.Ordinal);
        var attemptedModels = new List<string>();
        int threshold = options.Routing.FailoverFailureThreshold;
        int cooldown = options.Routing.FailoverCooldownSeconds;

        while (true)
        {
            var decision = _engine.Decide(request, options, failedInThisRequest);

            if (decision.Candidates.Count == 0)
            {
                if (decision.Reason.Contains("reject", StringComparison.OrdinalIgnoreCase))
                    throw new BudgetExhaustedException(decision.Reason);
                throw new AllCandidatesFailedException(attemptedModels);
            }

            bool attemptedCandidate = false;
            foreach (var candidate in decision.Candidates)
            {
                if (!failedInThisRequest.Add(candidate.Name))
                    continue;

                attemptedCandidate = true;
                attemptedModels.Add(candidate.Name);
                try
                {
                    var client = _clientProvider.GetClient(candidate);
                    var response = await client.CompleteAsync(request, ct).ConfigureAwait(false);

                    var cost = CostCalculator.Compute(response.Usage, candidate);
                    _ledger.Record(cost);
                    _healthTracker.RecordSuccess(candidate.Name);

                    return response;
                }
                catch (ModelClientException ex)
                {
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    _logger.LogWarning(ex, "Model {Name} failed (status {Status}), trying next candidate{Tripped}",
                        candidate.Name, ex.StatusCode, tripped ? " (circuit tripped)" : "");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 客户端内部超时，非外部取消，记失败继续。
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    _logger.LogWarning("Model {Name} timed out, trying next{Tripped}",
                        candidate.Name, tripped ? " (circuit tripped)" : "");
                }
            }

            if (!options.Routing.EnableFailover || !attemptedCandidate)
                throw new AllCandidatesFailedException(attemptedModels);
        }
    }

    /// <summary>
    /// 流式发送请求，按候选链顺序尝试。首个 chunk 开始 yield 后若失败，
    /// 无法再切换模型，直接向上抛出异常。
    /// </summary>
    /// <param name="request">聊天请求。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>响应块异步枚举。</returns>
    /// <exception cref="BudgetExhaustedException">预算耗尽且模式为 Reject。</exception>
    /// <exception cref="AllCandidatesFailedException">所有候选在首 chunk 前均失败。</exception>
    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var options = _options.CurrentValue;
        var failedInThisRequest = new HashSet<string>(StringComparer.Ordinal);
        var attemptedModels = new List<string>();
        int threshold = options.Routing.FailoverFailureThreshold;
        int cooldown = options.Routing.FailoverCooldownSeconds;

        while (true)
        {
            var decision = _engine.Decide(request, options, failedInThisRequest);

            if (decision.Candidates.Count == 0)
            {
                if (decision.Reason.Contains("reject", StringComparison.OrdinalIgnoreCase))
                    throw new BudgetExhaustedException(decision.Reason);
                throw new AllCandidatesFailedException(attemptedModels);
            }

            bool attemptedCandidate = false;
            foreach (var candidate in decision.Candidates)
            {
                if (!failedInThisRequest.Add(candidate.Name))
                    continue;

                attemptedCandidate = true;
                attemptedModels.Add(candidate.Name);
                var client = _clientProvider.GetClient(candidate);
                IAsyncEnumerator<ChatStreamChunk>? enumerator = null;
                ChatStreamChunk firstChunk = default!;
                ChatUsage? finalUsage = null;
                Exception? preStreamFailure = null;

                // Phase 1: 创建 enumerator 并尝试拿到第一个 chunk。
                // 此处有 catch，不能 yield；仅做"失败则继续下一候选"的判定。
                try
                {
                    enumerator = client.StreamAsync(request, ct).GetAsyncEnumerator(ct);
                    if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        firstChunk = enumerator.Current;
                        if (firstChunk.Usage is not null)
                            finalUsage = firstChunk.Usage;
                    }
                    else
                    {
                        // 空流：视为成功但无内容，继续尝试下一个候选。
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                        continue;
                    }
                }
                catch (ModelClientException ex)
                {
                    preStreamFailure = ex;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    preStreamFailure = ex;
                }
                finally
                {
                    if (preStreamFailure is not null && enumerator is not null)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (preStreamFailure is not null)
                {
                    bool tripped = _healthTracker.RecordFailure(candidate.Name, threshold, cooldown);
                    _logger.LogWarning(preStreamFailure, "Streaming model {Name} failed pre-stream, trying next{Tripped}",
                        candidate.Name, tripped ? " (circuit tripped)" : "");
                    continue;
                }

                ArgumentNullException.ThrowIfNull(enumerator);

                // Phase 2: 首个 chunk 在 try-catch 之外 yield，避免 CS1626。
                yield return firstChunk;

                // 继续 yield 剩余 chunk。此处只有 finally，无 catch，允许 yield。
                try
                {
                    while (await enumerator!.MoveNextAsync().ConfigureAwait(false))
                    {
                        var chunk = enumerator.Current;
                        if (chunk.Usage is not null)
                            finalUsage = chunk.Usage;
                        yield return chunk;
                    }
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }

                // 流正常结束，记账 + 标记健康。
                if (finalUsage is not null)
                {
                    _ledger.Record(CostCalculator.Compute(finalUsage, candidate));
                }
                _healthTracker.RecordSuccess(candidate.Name);
                yield break;
            }

            if (!options.Routing.EnableFailover || !attemptedCandidate)
                throw new AllCandidatesFailedException(attemptedModels);
        }
    }

    /// <summary>
    /// 释放共享的 SocketsHttpHandler 和客户端提供者。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_clientProvider is IDisposable d) d.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 异步释放资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_clientProvider is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        else if (_clientProvider is IDisposable d) d.Dispose();
        GC.SuppressFinalize(this);
    }
}

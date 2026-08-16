using System.Collections.Concurrent;

namespace OptiRouter.Concurrency;

/// <summary>
/// 基于 TCP Vegas / AIMD 算法的上游模型自适应并发拥塞控制器实现。
/// </summary>
public sealed class AdaptiveConcurrencyLimiter : IAdaptiveConcurrencyLimiter
{
    private readonly ConcurrentDictionary<string, ModelLimiterState> _states = new(StringComparer.Ordinal);
    private readonly int _minLimit;
    private readonly int _maxLimit;
    private readonly double _backoffFactor;

    private sealed class ModelLimiterState
    {
        private readonly object _lock = new();
        public SemaphoreSlim Semaphore { get; }
        public int CurrentLimit { get; private set; }
        public double MinRttMs { get; private set; } = double.MaxValue;

        public ModelLimiterState(int initialLimit)
        {
            CurrentLimit = initialLimit;
            Semaphore = new SemaphoreSlim(initialLimit, 2000);
        }

        public void Adjust(double rttMs, int minLimit, int maxLimit, double backoffFactor)
        {
            lock (_lock)
            {
                if (rttMs < MinRttMs) MinRttMs = rttMs;

                double gradient = MinRttMs / Math.Max(rttMs, 1.0);

                if (gradient < 0.70)
                {
                    // 上游延迟显著偏离最佳基线 (RTT > 1.43 * MinRTT) -> 乘性递减 (Multiplicative Decrease)
                    int newLimit = Math.Max(minLimit, (int)(CurrentLimit * backoffFactor));
                    CurrentLimit = newLimit;
                }
                else if (gradient >= 0.85 && CurrentLimit < maxLimit)
                {
                    // 上游响应平稳 -> 加性递增 (Additive Increase)
                    CurrentLimit = Math.Min(maxLimit, CurrentLimit + 1);
                }
            }
        }
    }

    /// <summary>
    /// 初始化自适应并发限制器。
    /// </summary>
    /// <param name="minLimit">最小允许并发数。</param>
    /// <param name="maxLimit">最大允许并发数。</param>
    /// <param name="backoffFactor">退避缩减因子（默认 0.8，即拥塞时缩小至 80%）。</param>
    public AdaptiveConcurrencyLimiter(int minLimit = 2, int maxLimit = 50, double backoffFactor = 0.8)
    {
        _minLimit = Math.Max(1, minLimit);
        _maxLimit = Math.Max(_minLimit, maxLimit);
        _backoffFactor = Math.Clamp(backoffFactor, 0.5, 0.95);
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var state = _states.GetOrAdd(modelName, name => new ModelLimiterState(_maxLimit));
        await state.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(state.Semaphore);
    }

    /// <inheritdoc />
    public void RecordRtt(string modelName, double rttMs)
    {
        if (rttMs <= 0) return;
        var state = _states.GetOrAdd(modelName, name => new ModelLimiterState(_maxLimit));
        state.Adjust(rttMs, _minLimit, _maxLimit, _backoffFactor);
    }

    /// <inheritdoc />
    public int GetCurrentLimit(string modelName)
    {
        return _states.TryGetValue(modelName, out var state) ? state.CurrentLimit : _maxLimit;
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}

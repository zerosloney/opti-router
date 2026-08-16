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

    /// <summary>
    /// 单模型限流状态：动态上限由 <see cref="_currentLimit"/> 原子计数表示，
    /// 在飞并发数由 <see cref="_inFlight"/> 原子计数表示，信号量仅作为"有槽位空出"的排队通知。
    /// 这样上限下调立即对新的获取者生效（CAS 检查），无需调整信号量容量。
    /// </summary>
    private sealed class ModelLimiterState
    {
        private readonly object _lock = new();
        private readonly SemaphoreSlim _releaseSignal = new(0, int.MaxValue);
        private int _inFlight;
        private int _currentLimit;
        private double _minRttMs = double.MaxValue;

        public ModelLimiterState(int initialLimit)
        {
            _currentLimit = initialLimit;
        }

        public int CurrentLimit => Volatile.Read(ref _currentLimit);

        /// <summary>
        /// 尝试原子占用一个并发槽位；在飞数达到动态上限时返回 false，调用方应等待槽位空出。
        /// </summary>
        public bool TryReserveSlot()
        {
            int current = Volatile.Read(ref _inFlight);
            int limit = Volatile.Read(ref _currentLimit);
            while (current < limit)
            {
                if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
                {
                    return true;
                }
                current = Volatile.Read(ref _inFlight);
                limit = Volatile.Read(ref _currentLimit);
            }
            return false;
        }

        /// <summary>
        /// 释放一个并发槽位并唤醒一个等待者。
        /// </summary>
        public void ReleaseSlot()
        {
            Interlocked.Decrement(ref _inFlight);
            _releaseSignal.Release();
        }

        public async Task WaitForReleaseAsync(CancellationToken ct)
        {
            await _releaseSignal.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 按 RTT 观测调整动态并发上限（AIMD）。上限下调只阻止新的获取者，
        /// 在飞请求自然完成后收敛；上限上调时主动唤醒等待者，避免回升后无人通知导致的等待饥饿。
        /// </summary>
        public void Adjust(double rttMs, int minLimit, int maxLimit, double backoffFactor)
        {
            lock (_lock)
            {
                if (rttMs < _minRttMs) _minRttMs = rttMs;

                double gradient = _minRttMs / Math.Max(rttMs, 1.0);
                int newLimit = _currentLimit;

                if (gradient < 0.70)
                {
                    // 上游延迟显著偏离最佳基线 (RTT > 1.43 * MinRTT) -> 乘性递减 (Multiplicative Decrease)
                    newLimit = Math.Max(minLimit, (int)(_currentLimit * backoffFactor));
                }
                else if (gradient >= 0.85 && _currentLimit < maxLimit)
                {
                    // 上游响应平稳 -> 加性递增 (Additive Increase)
                    newLimit = Math.Min(maxLimit, _currentLimit + 1);
                }

                int oldLimit = _currentLimit;
                if (newLimit != oldLimit)
                {
                    Volatile.Write(ref _currentLimit, newLimit);
                    if (newLimit > oldLimit)
                    {
                        for (int i = 0; i < newLimit - oldLimit; i++)
                        {
                            _releaseSignal.Release();
                        }
                    }
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
        while (!state.TryReserveSlot())
        {
            await state.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        return new Releaser(state);
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
        private ModelLimiterState? _state;

        public Releaser(ModelLimiterState state)
        {
            _state = state;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _state, null)?.ReleaseSlot();
        }
    }
}

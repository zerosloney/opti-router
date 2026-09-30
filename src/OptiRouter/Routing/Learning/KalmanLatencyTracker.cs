using System.Collections.Concurrent;

namespace OptiRouter.Routing;

/// <summary>
/// 卡尔曼滤波状态估算值。
/// </summary>
public sealed record KalmanEstimate(
    double EstimatedLatencyMs,
    double EstimateVariance,
    double EstimatedP99Ms,
    double PenaltyWeightFactor);

/// <summary>
/// 基于 1D 卡尔曼滤波 (1D Kalman Filter) 的 Provider 隐状态延迟与 P99 动态降权跟踪器。
/// 能够从存在剧烈随机抖动的 LLM 响应延迟数据中，以平滑且高敏捷度的方式提炼出真实系统隐藏延迟，
/// 并结合估算的 P99 尾部延迟对高抖动/高尾延 Provider 进行指数级动态降权。
/// </summary>
public sealed class KalmanLatencyTracker
{
    private sealed class ModelKalmanState
    {
        public double State;      // 估算的真实延迟 \hat{x}
        public double Variance;   // 估算方差 P
        public long SampleCount;  // 样本数

        public ModelKalmanState(double initialEstimate = 500.0, double initialVariance = 10000.0)
        {
            State = initialEstimate;
            Variance = initialVariance;
            SampleCount = 0;
        }
    }

    private readonly ConcurrentDictionary<string, ModelKalmanState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly double _processNoiseQ;       // 过程噪声方差 Q (模型真实状态漂移速度)
    private readonly double _measurementNoiseR;   // 测量噪声方差 R (单次网络/生成抖动噪声)
    private readonly double _targetLatencyMs;      // 目标 SLA 延迟基准
    private readonly double _penaltyGamma;         // 降权惩罚指数因子

    public KalmanLatencyTracker(
        double processNoiseQ = 10.0,
        double measurementNoiseR = 400.0,
        double targetLatencyMs = 1000.0,
        double penaltyGamma = 1.5)
    {
        _processNoiseQ = Math.Max(0.1, processNoiseQ);
        _measurementNoiseR = Math.Max(1.0, measurementNoiseR);
        _targetLatencyMs = Math.Max(10.0, targetLatencyMs);
        _penaltyGamma = Math.Max(0.1, penaltyGamma);
    }

    /// <summary>
    /// 录入一次真实观测延迟，并更新卡尔曼状态。
    /// </summary>
    /// <param name="modelName">模型/Provider 标识。</param>
    /// <param name="observedLatencyMs">单次测量延迟（毫秒）。</param>
    /// <returns>更新后的卡尔曼估计值。</returns>
    public KalmanEstimate RecordObservation(string modelName, double observedLatencyMs)
    {
        if (string.IsNullOrWhiteSpace(modelName) || observedLatencyMs <= 0)
        {
            return GetEstimate(modelName);
        }

        var state = _states.GetOrAdd(modelName, _ => new ModelKalmanState(observedLatencyMs));

        lock (state)
        {
            // 1. 预测步 (Predict)
            double xPred = state.State;
            double pPred = state.Variance + _processNoiseQ;

            // 2. 卡尔曼增益 (Kalman Gain)
            double K = pPred / (pPred + _measurementNoiseR);

            // 3. 更新步 (Update)
            state.State = xPred + K * (observedLatencyMs - xPred);
            state.Variance = (1.0 - K) * pPred;
            state.SampleCount++;

            return ComputeEstimateUnsafe(state);
        }
    }

    /// <summary>
    /// 获取当前卡尔曼延迟估计与 P99 降权因子。
    /// </summary>
    public KalmanEstimate GetEstimate(string modelName)
    {
        if (!_states.TryGetValue(modelName, out var state))
        {
            return new KalmanEstimate(500.0, 10000.0, 1000.0, 1.0);
        }

        lock (state)
        {
            return ComputeEstimateUnsafe(state);
        }
    }

    private KalmanEstimate ComputeEstimateUnsafe(ModelKalmanState state)
    {
        double est = Math.Max(1.0, state.State);
        double stdDev = Math.Sqrt(Math.Max(0.0, state.Variance));
        // 99% 置信区间上界 (P99 估算 = \hat{x} + 2.58 * \sigma)
        double p99Est = est + 2.58 * stdDev;

        // 计算 P99 动态降权因子: exp(-\gamma * max(0, P99 - Target) / Target)
        double weightFactor = 1.0;
        if (p99Est > _targetLatencyMs)
        {
            double excessRatio = (p99Est - _targetLatencyMs) / _targetLatencyMs;
            weightFactor = Math.Exp(-_penaltyGamma * excessRatio);
        }

        return new KalmanEstimate(est, state.Variance, p99Est, weightFactor);
    }
}

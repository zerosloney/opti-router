using OptiRouter.Clients;

namespace OptiRouter.Routing;

/// <summary>
/// 校准式 token 估算器：在内层估算（分桶粗估 / Tiktoken）之上，用上游真实
/// usage.prompt_tokens 与估算值的比值做 EMA 校正。
/// 观测由 <see cref="Endpoints.OutcomeRecorder.RecordAudit"/> 在成功请求上回填。
/// <para>
/// 背景：分桶粗估对工具调用密集的 agent 负载系统性偏低（实测平均 0.66、最差 0.17），
/// 污染 LongInputPolicy 上下文过滤、压缩触发点与预估成本。上游每次都返回精确 usage，
/// 却从未被用于校正。
/// </para>
/// </summary>
/// <remarks>
/// intentional-simple: 全局单一比率、进程内存活（重启后几十个样本重暖）。校准的是
/// "内容形态 → token"的系统性偏差，与具体模型基本无关；按模型分桶会因样本稀疏而抖动。
/// 比率夹在 [0.4, 3.0]，异常样本（比值 &lt;0.1 或 &gt;10）直接丢弃，防止上游缓存计数
/// 口径差异等噪声把估算打飞。
/// </remarks>
public sealed class CalibratingTokenEstimator : ITokenEstimator
{
    private const double MinRatio = 0.4;
    private const double MaxRatio = 3.0;
    private const int MinActualTokens = 200;      // 小请求 token 数噪声大，不采样
    private const int WarmupSamples = 10;         // 前 N 个样本用大步长快速逼近

    private readonly ITokenEstimator _inner;
    private double _ratio = 1.0;
    private int _observations;

    /// <summary>
    /// 以内层估算器构造校准器。
    /// </summary>
    /// <param name="inner">基础估算器（分桶或 Tiktoken）。</param>
    public CalibratingTokenEstimator(ITokenEstimator inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>当前校准比率（actual / estimated 的 EMA），仅供诊断展示。</summary>
    public double CurrentRatio => Volatile.Read(ref _ratio);

    /// <summary>已采样的成功请求数，仅供诊断展示。</summary>
    public int Observations => Volatile.Read(ref _observations);

    /// <inheritdoc />
    public int Estimate(ChatRequest request)
    {
        int raw = _inner.Estimate(request);
        if (raw <= 0)
            return 0;

        double ratio = Volatile.Read(ref _ratio);
        return Math.Max(1, (int)Math.Round(raw * ratio));
    }

    /// <summary>
    /// 用一次成功请求的真实输入 token 数校正比率。
    /// 入参 estimated 是<see cref="Estimate"/>已乘过当前比率的校准值，因此样本比值
    /// actual/estimated 需再乘当前比率才还原为绝对比率（否则反馈回路收敛到 √(真实偏差)，
    /// 生产实测残余低估 20%+）。非合法样本（估算/实际非正、比值超出 [0.1, 10]）静默丢弃。
    /// </summary>
    /// <param name="estimated">本请求的估算输入 token 数（校准后，即 decision.EstimatedInputTokens）。</param>
    /// <param name="actualPromptTokens">上游 usage.prompt_tokens。</param>
    public void Observe(int estimated, int actualPromptTokens)
    {
        if (estimated <= 0 || actualPromptTokens < MinActualTokens)
            return;

        double observed = actualPromptTokens / (double)estimated;
        if (observed < 0.1 || observed > 10)
            return;

        double alpha = Volatile.Read(ref _observations) < WarmupSamples ? 0.5 : 0.2;
        double current = Volatile.Read(ref _ratio);
        // 样本绝对比率 = current × observed（把"对已校准值的偏差"换算回"对内层原始估算的偏差"）。
        double next = current * (1 - alpha + alpha * observed);
        next = Math.Clamp(next, MinRatio, MaxRatio);

        Interlocked.Exchange(ref _ratio, next);
        Interlocked.Increment(ref _observations);
    }
}

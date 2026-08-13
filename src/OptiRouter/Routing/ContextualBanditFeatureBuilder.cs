using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 上下文老虎机特征构造：分类信号/tier one-hot + 输入规模、流式和多轮请求特征。
/// </summary>
public static class ContextualBanditFeatureBuilder
{
    /// <summary>分类信号白名单（规则分类 7 类 + 语义覆盖）。</summary>
    public static readonly IReadOnlyList<string> Signals = new[]
    {
        "code-detected", "code-complex", "code-simple",
        "math-detected", "translation-request", "simple-qa", "complex-instruction", "semantic-route"
    };

    /// <summary>tier 白名单（与 ModelTier 的 Strong/Medium/Cheap 一致）。</summary>
    public static readonly IReadOnlyList<ModelTier> Tiers = new[]
    {
        ModelTier.Strong, ModelTier.Medium, ModelTier.Cheap
    };

    /// <summary>特征维度 = 8 信号 + 3 tier + 3 请求特征 + bias。</summary>
    public const int Dimension = 8 + 3 + 3 + 1;

    /// <summary>
    /// 从分类信号 + 目标 tier 构造 one-hot 特征向量。
    /// 未知信号/tier 时对应位全零（仅 bias=1），不偏向任何类别。
    /// </summary>
    /// <param name="signal">分类信号（如 "code-complex"）；null/未知 → 信号位全零。</param>
    /// <param name="targetTier">目标 tier；null/未知 → tier 位全零。</param>
    /// <param name="estimatedInputTokens">估算输入 token 数。</param>
    /// <param name="isStreaming">是否为流式请求。</param>
    /// <param name="messageCount">消息数量；大于 1 视为多轮/带系统上下文。</param>
    /// <returns>长度 = <see cref="Dimension"/> 的特征向量。</returns>
    public static double[] Build(
        string? signal,
        ModelTier? targetTier,
        int estimatedInputTokens = 0,
        bool isStreaming = false,
        int messageCount = 0)
    {
        var x = new double[Dimension];
        x[Dimension - 1] = 1.0;  // bias

        if (signal is not null)
        {
            string canonicalSignal = signal.StartsWith("semantic:", StringComparison.OrdinalIgnoreCase)
                ? "semantic-route"
                : signal;
            int idx = IndexOf(Signals, canonicalSignal);
            if (idx >= 0) x[idx] = 1.0;
        }

        int requestFeatureStart = Signals.Count + Tiers.Count;
        // log2 bucket 压缩长尾 token 数并归一化到 [0,1]；无 token 估算时为 0。
        x[requestFeatureStart] = estimatedInputTokens > 0
            ? Math.Clamp(Math.Log2(estimatedInputTokens + 1) / 20.0, 0.0, 1.0)
            : 0.0;
        x[requestFeatureStart + 1] = isStreaming ? 1.0 : 0.0;
        x[requestFeatureStart + 2] = messageCount > 1 ? 1.0 : 0.0;

        if (targetTier is { } tier)
        {
            int idx = IndexOf(Tiers, tier);
            if (idx >= 0) x[Signals.Count + idx] = 1.0;
        }

        return x;
    }

    /// <summary>从完整路由决策构造与决策时一致的学习特征。</summary>
    public static double[] Build(RouterDecision decision) => Build(
        decision.ClassificationSignal,
        decision.ClassificationTargetTier,
        decision.EstimatedInputTokens,
        decision.RequestIsStreaming,
        decision.RequestMessageCount);

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, T value)
    {
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < list.Count; i++)
            if (cmp.Equals(list[i], value))
                return i;
        return -1;
    }
}

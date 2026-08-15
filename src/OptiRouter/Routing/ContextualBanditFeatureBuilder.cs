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

    /// <summary>特征维度 = 8 信号 + 3 tier + 3 请求特征 + 4 语义路由哈希 + 1 语言 + 1 输出预算 + 1 工具调用 + 2 交互 + bias。</summary>
    public const int Dimension = 8 + 3 + 3 + 4 + 1 + 1 + 1 + 2 + 1;

    /// <summary>
    /// 从分类信号 + 目标 tier 构造 one-hot 特征向量。
    /// 未知信号/tier 时对应位全零（仅 bias=1），不偏向任何类别。
    /// </summary>
    /// <param name="signal">分类信号（如 "code-complex"）；null/未知 → 信号位全零。</param>
    /// <param name="targetTier">目标 tier；null/未知 → tier 位全零。</param>
    /// <param name="estimatedInputTokens">估算输入 token 数。</param>
    /// <param name="isStreaming">是否为流式请求。</param>
    /// <param name="messageCount">消息数量；大于 1 视为多轮/带系统上下文。</param>
    /// <param name="cjkRatio">CJK 字符占比 [0,1]；默认 0。</param>
    /// <param name="maxTokens">最大生成 token 数；用于输出预算特征。</param>
    /// <param name="hasTools">请求是否携带工具调用；默认 false。</param>
    /// <returns>长度 = <see cref="Dimension"/> 的特征向量。</returns>
    public static double[] Build(
        string? signal,
        ModelTier? targetTier,
        int estimatedInputTokens = 0,
        bool isStreaming = false,
        int messageCount = 0,
        double cjkRatio = 0,
        int maxTokens = 0,
        bool hasTools = false)
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
        double inputTokenBucket = estimatedInputTokens > 0
            ? Math.Clamp(Math.Log2(estimatedInputTokens + 1) / 20.0, 0.0, 1.0)
            : 0.0;
        x[requestFeatureStart] = inputTokenBucket;
        x[requestFeatureStart + 1] = isStreaming ? 1.0 : 0.0;
        x[requestFeatureStart + 2] = messageCount > 1 ? 1.0 : 0.0;

        // 新增维度起始位置（旧 15 维之后）
        int newFeatureStart = requestFeatureStart + 3;

        // 语义路由哈希 4 维：当 signal 形如 "semantic:路由名" 时，用 FNV-1a 哈希路由名
        if (signal is not null && signal.StartsWith("semantic:", StringComparison.OrdinalIgnoreCase))
        {
            string routeName = signal.Substring("semantic:".Length);
            int hash = Fnv1aHash(routeName);
            int hashIndex = newFeatureStart + (hash % 4);
            x[hashIndex] = 1.0;
        }

        // 语言 1 维：CJK 字符占比
        x[newFeatureStart + 4] = Math.Clamp(cjkRatio, 0.0, 1.0);

        // 输出预算 1 维：MaxTokens > 0 时 Log2(MaxTokens+1)/20 归一化
        x[newFeatureStart + 5] = maxTokens > 0
            ? Math.Clamp(Math.Log2(maxTokens + 1) / 20.0, 0.0, 1.0)
            : 0.0;

        // 工具调用 1 维
        x[newFeatureStart + 6] = hasTools ? 1.0 : 0.0;

        // 交互 2 维：code 族信号 × isStreaming，code 族 × 输入 token 桶
        bool isCodeSignal = signal is not null && (
            string.Equals(signal, "code-detected", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(signal, "code-complex", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(signal, "code-simple", StringComparison.OrdinalIgnoreCase));
        x[newFeatureStart + 7] = isCodeSignal && isStreaming ? 1.0 : 0.0;
        x[newFeatureStart + 8] = isCodeSignal ? inputTokenBucket : 0.0;

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
        decision.RequestMessageCount,
        decision.CjkRatio,
        decision.MaxTokens,
        decision.HasTools);

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

    /// <summary>
    /// FNV-1a 32 位哈希算法（确定性，跨进程一致）。
    /// 禁用 string.GetHashCode 是因为它跨进程随机化，会导致持久化状态维度语义漂移。
    /// </summary>
    /// <remarks>
    /// intentional-simple: 简单哈希实现，可接受碰撞（不同路由名可能落入同一桶）。
    /// FNV-1a 算法：hash = (hash ^ byte) * 16777619（FNV prime）。
    /// </remarks>
    private static int Fnv1aHash(string input)
    {
        const uint FNV_prime = 16777619;
        uint hash = 2166136261;  // FNV offset basis

        foreach (char c in input)
        {
            // 处理 Unicode 字符：使用 UTF-16 低字节，简单哈希不要求完美分布
            hash ^= (byte)(c & 0xFF);
            hash *= FNV_prime;
            hash ^= (byte)((c >> 8) & 0xFF);
            hash *= FNV_prime;
        }

        return (int)(hash & 0x7FFFFFFF);  // 转为正整数
    }
}

using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 上下文老虎机特征构造：把分类信号 + tier 映射为固定维度 one-hot 特征向量。
/// 维度 = 7 信号 + 3 tier + 1 bias = 11（与 <see cref="ContextualBanditState"/> 默认维度一致）。
/// </summary>
public static class ContextualBanditFeatureBuilder
{
    /// <summary>分类信号白名单（与 RuleClassifierPolicy 的 7 类信号一致）。</summary>
    public static readonly IReadOnlyList<string> Signals = new[]
    {
        "code-detected", "code-complex", "code-simple",
        "math-detected", "translation-request", "simple-qa", "complex-instruction"
    };

    /// <summary>tier 白名单（与 ModelTier 的 Strong/Medium/Cheap 一致）。</summary>
    public static readonly IReadOnlyList<ModelTier> Tiers = new[]
    {
        ModelTier.Strong, ModelTier.Medium, ModelTier.Cheap
    };

    /// <summary>特征维度 = 信号数 + tier 数 + bias。</summary>
    public const int Dimension = 7 + 3 + 1;

    /// <summary>
    /// 从分类信号 + 目标 tier 构造 one-hot 特征向量。
    /// 未知信号/tier 时对应位全零（仅 bias=1），不偏向任何类别。
    /// </summary>
    /// <param name="signal">分类信号（如 "code-complex"）；null/未知 → 信号位全零。</param>
    /// <param name="targetTier">目标 tier；null/未知 → tier 位全零。</param>
    /// <returns>长度 = <see cref="Dimension"/> 的特征向量。</returns>
    public static double[] Build(string? signal, ModelTier? targetTier)
    {
        var x = new double[Dimension];
        x[Dimension - 1] = 1.0;  // bias

        if (signal is not null)
        {
            int idx = IndexOf(Signals, signal);
            if (idx >= 0) x[idx] = 1.0;
        }

        if (targetTier is { } tier)
        {
            for (int i = 0; i < Tiers.Count; i++)
            {
                if (Tiers[i] == tier)
                {
                    x[Signals.Count + i] = 1.0;
                    break;
                }
            }
        }

        return x;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}

namespace OptiRouter.Routing;

/// <summary>
/// 延迟统计数学工具：纯函数、无状态，供 InMemory / SQLite 审计存储复用，
/// 与 <c>scripts/analyze_audit.py</c> 的 percentile 语义保持一致。
/// </summary>
public static class LatencyStatsMath
{
    /// <summary>
    /// 线性插值百分位。<paramref name="sorted"/> 必须已升序排序且非空。
    /// 返回第 <paramref name="pct"/> 百分位的延迟值（毫秒）。
    /// </summary>
    /// <param name="sorted">已升序排序的非空延迟列表（毫秒）。</param>
    /// <param name="pct">百分位（0-100，如 95 表示 p95）。</param>
    public static double Percentile(List<double> sorted, double pct)
    {
        if (sorted.Count == 1) return sorted[0];
        double k = (sorted.Count - 1) * (pct / 100.0);
        int lo = (int)Math.Floor(k);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        double frac = k - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}

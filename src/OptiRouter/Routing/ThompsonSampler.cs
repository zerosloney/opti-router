using System;
using System.Threading;

namespace OptiRouter.Routing;

/// <summary>
/// 零依赖、高吞吐、Native AOT 兼容的汤姆森采样（Thompson Sampling）Beta 分布随机生成器。
/// 基于 Marsaglia and Tsang 方法生成 Gamma 随机变量并将其转换为 Beta 样本。
/// </summary>
public static class ThompsonSampler
{
    private static readonly ThreadLocal<Random> ThreadLocalRng = new(() => new Random());

    /// <summary>
    /// 从 Beta(alpha, beta) 概率分布中抽取随机数，取值范围在 (0, 1) 之间。
    /// </summary>
    /// <param name="alpha">先验 Alpha 计数（代表正向质量反馈数），必须为正数。</param>
    /// <param name="beta">先验 Beta 计数（代表负向延迟/可用性故障反馈数），必须为正数。</param>
    /// <returns>Beta 随机分布采样样本。</returns>
    public static double SampleBeta(double alpha, double beta)
    {
        var rng = ThreadLocalRng.Value ?? Random.Shared;

        // 极限极小值防护
        double a = Math.Max(alpha, 1e-5);
        double b = Math.Max(beta, 1e-5);

        double u1 = SampleGamma(a, 1.0, rng);
        double u2 = SampleGamma(b, 1.0, rng);
        double sum = u1 + u2;

        if (sum <= 0.0) return 0.5; // 极限容错兜底
        return u1 / sum;
    }

    /// <summary>
    /// 精确的 Marsaglia and Tsang (2000) Gamma 分布变量生成方法。
    /// </summary>
    private static double SampleGamma(double alpha, double beta, Random rng)
    {
        if (alpha < 1.0)
        {
            // 当 alpha < 1.0 时，利用关系：Gamma(alpha) = Gamma(alpha + 1) * U^(1/alpha)
            return SampleGamma(alpha + 1.0, beta, rng) * Math.Pow(rng.NextDouble(), 1.0 / alpha);
        }

        double d = alpha - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);

        while (true)
        {
            double z = SampleNormal(rng);
            double v = 1.0 + c * z;
            if (v <= 0.0) continue;

            v = v * v * v;
            double u = rng.NextDouble();

            // 快速舍弃（Squeeze）判定提高吞吐率
            if (u < 1.0 - 0.0331 * z * z * z * z)
            {
                return d * v / beta;
            }

            // 对数概率精细舍弃判定
            if (Math.Log(u) < 0.5 * z * z + d * (1.0 - v + Math.Log(v)))
            {
                return d * v / beta;
            }
        }
    }

    /// <summary>
    /// 通过标准极坐标 Box-Muller 变换获取标准正态分布随机数。
    /// </summary>
    private static double SampleNormal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

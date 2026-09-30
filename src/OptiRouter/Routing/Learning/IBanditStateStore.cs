namespace OptiRouter.Routing;

/// <summary>
/// 上下文老虎机（LinUCB）状态持久化抽象。每条记录对应一个模型的 arm 状态
///（协方差矩阵 A、累积向量 b、样本数 N）。
/// </summary>
public interface IBanditStateStore
{
    /// <summary>保存单模型的 Bandit arm 状态。</summary>
    /// <param name="modelName">模型名。</param>
    /// <param name="dim">特征维度。</param>
    /// <param name="a">协方差矩阵 A（dim × dim）。</param>
    /// <param name="b">累积向量 b（dim）。</param>
    /// <param name="n">样本数。</param>
    void Save(string modelName, int dim, double[,] a, double[] b, int n);

    /// <summary>加载全部已持久化的 Bandit arm 状态。</summary>
    /// <returns>模型名到 arm 状态的映射。</returns>
    Dictionary<string, (int Dim, double[,] A, double[] B, int N)> LoadAll();
}

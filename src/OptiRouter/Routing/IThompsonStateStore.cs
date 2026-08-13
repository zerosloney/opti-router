namespace OptiRouter.Routing;

/// <summary>
/// Thompson 采样状态持久化抽象。每条记录对应一个模型的 Beta(α,β) 参数。
/// </summary>
public interface IThompsonStateStore
{
    /// <summary>保存单模型的 Thompson 采样参数。</summary>
    void Save(string modelName, double alpha, double beta);

    /// <summary>加载全部已持久化的 Thompson 采样参数。</summary>
    /// <returns>模型名到 (alpha, beta) 的映射。</returns>
    Dictionary<string, (double Alpha, double Beta)> LoadAll();
}

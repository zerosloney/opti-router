using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// 按端点配置提供 <see cref="IModelClient"/> 实例的抽象。
/// 便于测试时注入 mock，也便于生产端统一管理 HttpClient 生命周期。
/// </summary>
public interface IModelClientProvider
{
    /// <summary>
    /// 获取或创建对应端点的模型客户端。
    /// </summary>
    /// <param name="endpoint">端点配置。</param>
    /// <returns>模型客户端。</returns>
    IModelClient GetClient(ModelEndpointOptions endpoint);
}

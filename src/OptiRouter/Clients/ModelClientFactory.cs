using System.Net;
using System.Net.Http.Headers;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// 按端点配置创建 <see cref="IModelClient"/> 实例的工厂。
/// </summary>
public sealed class ModelClientFactory
{
    /// <summary>
    /// 按 endpoint 创建一个 IModelClient 实例。
    /// 注意：每个 endpoint 用独立 HttpClient 实例（不同 BaseAddress/ApiKey/Timeout）。
    /// </summary>
    /// <param name="endpoint">端点配置。</param>
    /// <param name="httpClient">外部传入的 HttpClient。</param>
    /// <returns>模型客户端。</returns>
    public IModelClient Create(ModelEndpointOptions endpoint, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);

        // 配置 BaseAddress。
        if (!endpoint.BaseUrl.EndsWith("/", StringComparison.Ordinal))
            httpClient.BaseAddress = new Uri(endpoint.BaseUrl + "/");
        else
            httpClient.BaseAddress = new Uri(endpoint.BaseUrl);

        // 配置超时。
        httpClient.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds > 0 ? endpoint.TimeoutSeconds : 120);

        // 配置 Authorization。
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }
        else
        {
            httpClient.DefaultRequestHeaders.Authorization = null;
        }

        return new OpenAICompatibleModelClient(endpoint, httpClient);
    }

    /// <summary>
    /// 便捷方法：为测试创建客户端，使用自定义 <see cref="HttpMessageHandler"/>。
    /// </summary>
    /// <param name="endpoint">端点配置。</param>
    /// <param name="handler">自定义消息处理器。</param>
    /// <returns>模型客户端。</returns>
    public static IModelClient CreateForEndpoint(ModelEndpointOptions endpoint, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(handler);

        var httpClient = new HttpClient(handler, disposeHandler: false);
        return new ModelClientFactory().Create(endpoint, httpClient);
    }
}

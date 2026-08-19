using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// 按端点配置创建 <see cref="IModelClient"/> 实例的工厂。
/// </summary>
public sealed class ModelClientFactory
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly ILogger? _logger;

    /// <summary>
    /// 初始化工厂。
    /// </summary>
    /// <param name="logger">可选日志，透传给客户端用于流式解析降级的诊断记录。</param>
    public ModelClientFactory(ILogger? logger = null)
    {
        _logger = logger;
    }

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

        ConfigureAuthentication(endpoint, httpClient.DefaultRequestHeaders);

        // 按端点协议选择客户端：原生协议（Anthropic/Gemini）在客户端内部完成双向翻译，
        // 下游始终收到 OpenAI 契约响应。
        return endpoint.Protocol switch
        {
            ProviderProtocol.Anthropic => new AnthropicModelClient(endpoint, httpClient, _logger),
            ProviderProtocol.Gemini => new GeminiModelClient(endpoint, httpClient, _logger),
            _ => new OpenAICompatibleModelClient(endpoint, httpClient, _logger)
        };
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

    internal static void ConfigureAuthentication(ModelEndpointOptions endpoint, HttpRequestHeaders headers)
    {
        headers.Authorization = null;
        headers.Remove("x-api-key");
        headers.Remove("x-goog-api-key");
        headers.Remove("anthropic-version");

        if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
            return;

        switch (endpoint.Protocol)
        {
            case ProviderProtocol.Anthropic:
                headers.TryAddWithoutValidation("x-api-key", endpoint.ApiKey);
                headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
                break;
            case ProviderProtocol.Gemini:
                headers.TryAddWithoutValidation("x-goog-api-key", endpoint.ApiKey);
                break;
            default:
                headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                break;
        }
    }
}

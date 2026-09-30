using System.Diagnostics;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// 云知声 SystemOne 决策协议（u2-decision）的模型客户端。
/// <para>
/// SystemOne 不是 chat 协议（请求为 state + questions，响应为概率分布 answers），
/// 不接入 chat 翻译管线：chat 形态的调用一律抛 <see cref="NotSupportedException"/>——
/// chat 路由候选池已由 <see cref="Routing.RouterEngine"/> 排除该协议，此异常仅作为
/// FallbackChain 误配等漏网路径的最后防线（候选失败 → 正常 failover 到其他模型）。
/// 实际流量走 <c>POST /v1/systemone</c> 原生透传端点（<c>Endpoints.SystemOneEndpoint</c>）。
/// </para>
/// <para>
/// 实现 <see cref="IModelClient"/> 的动机是探活：后台探活服务与管理台"测试连通性"
/// 对所有 Enabled 模型调用 <see cref="IModelClient.ProbeAsync"/>，这里用同域
/// OpenAI 兼容模型列表接口（GET /v1/models）做连通性 + 鉴权探测，不烧决策调用。
/// </para>
/// </summary>
public sealed class SystemOneModelClient : IModelClient
{
    private readonly HttpClient _httpClient;

    /// <inheritdoc />
    public ModelEndpointOptions Endpoint { get; }

    /// <summary>
    /// 初始化客户端。httpClient 由 <see cref="ModelClientFactory"/> 配置好 BaseAddress（不使用）与 Bearer 认证头。
    /// </summary>
    public SystemOneModelClient(ModelEndpointOptions endpoint, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        Endpoint = endpoint;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw NotChatSupported();

    /// <inheritdoc />
    public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw NotChatSupported();

    /// <inheritdoc />
    public Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw NotChatSupported();

    /// <inheritdoc />
    public IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
        => throw NotChatSupported();

    private static Exception NotChatSupported()
        => new NotSupportedException(
            "SystemOne (u2-decision) is a decision protocol, not chat. " +
            "Call POST /v1/systemone with { state, questions } instead of /v1/chat/completions.");

    /// <inheritdoc />
    public async Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        // 探活窗口：默认 5s（接口契约），caller 显式放宽则从宽。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            // SystemOne 协议无免费推理探针；同域 OpenAI 兼容 GET /v1/models 校验连通性与鉴权（401 = key 无效）。
            using var request = new HttpRequestMessage(HttpMethod.Get, ResolveApiPath(Endpoint.BaseUrl, "models"));
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            sw.Stop();
            bool healthy = response.IsSuccessStatusCode;
            return healthy
                ? new ModelHealthResult(true, (int)sw.ElapsedMilliseconds, StatusCode: response.StatusCode)
                : new ModelHealthResult(false, (int)sw.ElapsedMilliseconds, $"status {(int)response.StatusCode}", response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ModelHealthResult(false, (int)sw.ElapsedMilliseconds, "probe timed out");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new ModelHealthResult(false, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>
    /// 拼接上游 API 路径：BaseUrl 配置为 OpenAI 风格 base（以 /v1 结尾）时不再重复拼接 /v1 段。
    /// <paramref name="suffix"/> 为不带 /v1 前缀的资源路径（如 "systemone"、"models"）。
    /// </summary>
    internal static string ResolveApiPath(string baseUrl, string suffix)
    {
        string trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{trimmed}/{suffix}"
            : $"{trimmed}/v1/{suffix}";
    }
}

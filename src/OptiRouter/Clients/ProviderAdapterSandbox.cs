using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// Provider 端点沙箱校验结果。
/// </summary>
public sealed record ProviderValidationResult(
    bool IsValid,
    long LatencyMs,
    string DetectedProvider,
    string? ErrorMessage,
    IReadOnlyDictionary<string, string> Capabilities);

/// <summary>
/// Provider 适配器沙箱抽象契约。
/// </summary>
public interface IProviderAdapterSandbox
{
    /// <summary>
    /// 在沙箱环境中探活并校验模型端点的可用性与协议兼容性。
    /// </summary>
    Task<ProviderValidationResult> ValidateEndpointAsync(ModelEndpointOptions endpoint, CancellationToken ct = default);

    /// <summary>
    /// 获取受支持的 Provider 协议列表。
    /// </summary>
    IReadOnlyList<string> GetSupportedProviders();
}

/// <summary>
/// Provider 适配器沙箱实现：支持动态探测 OpenAI/Azure/Ollama/vLLM/DeepSeek 端点协议与健康校验。
/// </summary>
public sealed class ProviderAdapterSandbox : IProviderAdapterSandbox
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProviderAdapterSandbox> _logger;

    private static readonly string[] SupportedProviders =
    [
        "openai",
        "deepseek",
        "anthropic",
        "ollama",
        "vllm",
        "sglang",
        "localai",
        "azure",
        "custom"
    ];

    public ProviderAdapterSandbox(HttpClient? httpClient = null, ILogger<ProviderAdapterSandbox>? logger = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderAdapterSandbox>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSupportedProviders() => SupportedProviders;

    /// <inheritdoc />
    public async Task<ProviderValidationResult> ValidateEndpointAsync(ModelEndpointOptions endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
        {
            return new ProviderValidationResult(
                IsValid: false,
                LatencyMs: 0,
                DetectedProvider: "unknown",
                ErrorMessage: "BaseUrl cannot be empty.",
                Capabilities: new Dictionary<string, string>());
        }

        var sw = Stopwatch.StartNew();
        string provider = ModelDisplayIds.EffectiveProvider(endpoint);
        var capabilities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["model_name"] = endpoint.Name,
            ["tier"] = endpoint.Tier.ToString(),
            ["max_context_tokens"] = endpoint.MaxContextTokens.ToString(),
            ["provider"] = provider
        };

        try
        {
            // 构造简单的探测请求 URI
            string baseUrl = endpoint.BaseUrl.TrimEnd('/');
            string probeUrl = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{baseUrl}/models"
                : $"{baseUrl}/v1/models";

            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            ModelClientFactory.ConfigureAuthentication(endpoint, request.Headers);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();

            if (response.IsSuccessStatusCode)
            {
                capabilities["status"] = "online";
                capabilities["http_code"] = ((int)response.StatusCode).ToString();
                return new ProviderValidationResult(
                    IsValid: true,
                    LatencyMs: sw.ElapsedMilliseconds,
                    DetectedProvider: provider,
                    ErrorMessage: null,
                    Capabilities: capabilities);
            }

            // 若 /models 返回 404/401/403，仍可根据状态码推断有效性（某些 Provider 如 Ollama 或自建代理不开放 /models）
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return new ProviderValidationResult(
                    IsValid: false,
                    LatencyMs: sw.ElapsedMilliseconds,
                    DetectedProvider: provider,
                    ErrorMessage: $"Authentication failed with HTTP {(int)response.StatusCode}. Check API Key.",
                    Capabilities: capabilities);
            }

            // 针对 Ollama /api/tags 或 vLLM /health 进行二次探测尝试
            if (baseUrl.Contains("11434") || string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
            {
                using var ollamaReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/tags");
                using var ollamaResp = await _httpClient.SendAsync(ollamaReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (ollamaResp.IsSuccessStatusCode)
                {
                    capabilities["status"] = "online_ollama";
                    return new ProviderValidationResult(
                        IsValid: true,
                        LatencyMs: sw.ElapsedMilliseconds,
                        DetectedProvider: "ollama",
                        ErrorMessage: null,
                        Capabilities: capabilities);
                }
            }

            return new ProviderValidationResult(
                IsValid: false,
                LatencyMs: sw.ElapsedMilliseconds,
                DetectedProvider: provider,
                ErrorMessage: $"Endpoint returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                Capabilities: capabilities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Provider sandbox validation failed for endpoint {Name} at {BaseUrl}", endpoint.Name, endpoint.BaseUrl);
            return new ProviderValidationResult(
                IsValid: false,
                LatencyMs: sw.ElapsedMilliseconds,
                DetectedProvider: provider,
                ErrorMessage: ex.Message,
                Capabilities: capabilities);
        }
    }

}

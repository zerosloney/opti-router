using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OptiRouter.Clients.Protocols;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// Google Gemini generateContent API 原生协议客户端。
/// 请求经 <see cref="GeminiTranslators"/> 翻译为 Gemini JSON，响应/流式行翻译回
/// OpenAI 兼容契约——下游始终拿到 OpenAI 格式。
/// </summary>
public sealed class GeminiModelClient : IModelClient
{
    private static readonly JsonSerializerOptions _deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = new JsonSnakeCaseNamingPolicy()
    };

    private readonly HttpClient _httpClient;
    private readonly ModelEndpointOptions _endpoint;
    private readonly ILogger? _logger;

    /// <inheritdoc />
    public ModelEndpointOptions Endpoint => _endpoint;

    /// <summary>
    /// 初始化 Gemini 客户端。
    /// </summary>
    /// <param name="endpoint">端点配置（BaseUrl 为 Gemini API 根地址，如 <c>https://generativelanguage.googleapis.com</c>）。</param>
    /// <param name="httpClient">已配置 BaseAddress、Timeout 与 Authorization 的 HttpClient。</param>
    /// <param name="logger">可选日志，用于流式解析降级的诊断记录。</param>
    public GeminiModelClient(ModelEndpointOptions endpoint, HttpClient httpClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        _endpoint = endpoint;
        _httpClient = httpClient;
        _logger = logger;
    }

    private string GenerateContentPath => $"/v1beta/models/{_endpoint.Id}:generateContent";

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var raw = await CompleteRawAsync(request, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ChatResponse>(raw.Body, _deserializeOptions)
            ?? new ChatResponse { Model = _endpoint.Id };
    }

    /// <inheritdoc />
    public async Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var content = new StringContent(
            GeminiTranslators.BuildRequestBody(request, _endpoint),
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentType!.CharSet = "utf-8";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GenerateContentPath) { Content = content };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        // 有界读取：响应体超过上限立即中断，防异常/恶意上游超大响应撑爆内存（与 OpenAI 客户端一致）
        string body = await BoundedResponseReader.ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModelClientException(response.StatusCode, body);
        }

        string openAiJson = GeminiTranslators.ToOpenAiJson(body);
        return new RawChatResponse(openAiJson, ExtractUsage(openAiJson));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in StreamRawAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line.Data) || line.Data == "[DONE]") continue;

            RawStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<RawStreamChunk>(line.Data, _deserializeOptions);
            }
            catch (JsonException ex)
            {
                // 跳过无法解析的行（降级），但留下诊断线索——协议不兼容问题不再完全静默
                _logger?.LogDebug(ex, "Gemini stream line failed to parse, skipping: {Fragment}",
                    line.Data.Length > 200 ? line.Data[..200] : line.Data);
                continue;
            }
            if (chunk is null) continue;

            yield return new ChatStreamChunk
            {
                Id = chunk.Id,
                DeltaContent = chunk.Choices.Count > 0 ? chunk.Choices[0].Delta.Content : null,
                FinishReason = chunk.Choices.Count > 0 ? chunk.Choices[0].FinishReason : null,
                Usage = chunk.Usage
            };
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var response = await SendStreamRequestAsync(request, cancellationToken).ConfigureAwait(false);

        // Gemini 流式与 OpenAI 同为 data: {...} 行格式。
        // 限长行读取：单行超上限立即中断，替代无行长限制的 StreamReader.ReadLineAsync。
        bool doneSent = false;
        await foreach (string line in BoundedResponseReader.ReadLinesAsync(
            response.Content.ReadAsStream(cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            string data = line["data: ".Length..].Trim();
            if (string.IsNullOrEmpty(data)) continue;

            string? translated = GeminiTranslators.TranslateStreamLine(data);
            if (translated == "[DONE]")
            {
                yield return new RawStreamLine("[DONE]", null, null);
                doneSent = true;
                break;
            }
            else if (translated is not null)
            {
                yield return new RawStreamLine(translated, null, null);
            }
        }

        if (!doneSent)
        {
            yield return new RawStreamLine("[DONE]", null, null);
        }
    }

    /// <inheritdoc />
    public async Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var probeRequest = new ChatRequest
        {
            Model = _endpoint.UpstreamModelId,
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "ping") },
            MaxTokens = 1,
            Stream = false
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await CompleteAsync(probeRequest, cts.Token).ConfigureAwait(false);
            sw.Stop();
            return new ModelHealthResult(true, (int)sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 发送流式请求并校验状态（迭代器外执行，避免 catch 块内 yield 的编译器限制）。
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamRequestAsync(ChatRequest request, CancellationToken ct)
    {
        using var content = new StringContent(
            GeminiTranslators.BuildRequestBody(request, _endpoint),
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentType!.CharSet = "utf-8";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GenerateContentPath) { Content = content };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new ModelClientException(response.StatusCode, errorBody);
        }
        return response;
    }

    private static ChatUsage? ExtractUsage(string openAiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(openAiJson);
            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return JsonSerializer.Deserialize<ChatUsage>(usage.GetRawText(), _deserializeOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

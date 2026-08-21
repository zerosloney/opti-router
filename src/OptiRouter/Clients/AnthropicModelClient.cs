using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OptiRouter.Clients.Protocols;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// Anthropic Messages API 原生协议客户端。
/// 请求经 <see cref="AnthropicTranslators"/> 翻译为 Anthropic JSON，响应/流式事件
/// 翻译回 OpenAI 兼容契约——下游始终拿到 OpenAI 格式，无需感知上游协议差异。
/// </summary>
public sealed class AnthropicModelClient : IModelClient
{
    private const string MessagesPath = "/v1/messages";

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
    /// 初始化 Anthropic 客户端。
    /// </summary>
    /// <param name="endpoint">端点配置（BaseUrl 为 Anthropic API 根地址）。</param>
    /// <param name="httpClient">已配置 BaseAddress、Timeout 与 Authorization 的 HttpClient。</param>
    /// <param name="logger">可选日志，用于流式解析降级的诊断记录。</param>
    public AnthropicModelClient(ModelEndpointOptions endpoint, HttpClient httpClient, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        _endpoint = endpoint;
        _httpClient = httpClient;
        _logger = logger;
    }

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

        // 与 OpenAI 客户端一致的重试语义：可重试状态码（408/5xx）与瞬时网络/超时异常按 MaxRetries 退避重试，
        // 429 刻意上抛给编排层做配额记账与换源。
        int maxRetries = _endpoint.MaxRetries;
        int attempt = 0;
        TimeSpan totalTimeout = ModelClientRetry.ResolveCallTimeout(_endpoint);
        while (true)
        {
            try
            {
                var (status, body) = await ModelClientRetry.WithTotalTimeout(
                    totalTimeout, cancellationToken, async token =>
                    {
                        using var content = new StringContent(
                            AnthropicTranslators.BuildRequestBody(request, _endpoint),
                            Encoding.UTF8,
                            "application/json");
                        content.Headers.ContentType!.CharSet = "utf-8";

                        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, MessagesPath) { Content = content };
                        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        // 有界读取：响应体超过上限立即中断，防异常/恶意上游超大响应撑爆内存（与 OpenAI 客户端一致）
                        string body = await BoundedResponseReader.ReadBodyAsync(response.Content, token).ConfigureAwait(false);
                        return (response.StatusCode, body);
                    }).ConfigureAwait(false);

                if ((int)status is < 200 or > 299)
                {
                    if (ModelClientRetry.IsRetryable(status) && attempt < maxRetries)
                    {
                        attempt++;
                        await ModelClientRetry.DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new ModelClientException(status, body);
                }

                string openAiJson = AnthropicTranslators.ToOpenAiJson(body);
                var usage = ExtractUsage(openAiJson);
                return new RawChatResponse(openAiJson, usage);
            }
            catch (Exception ex) when (ModelClientRetry.IsExceptionRetryable(ex) && attempt < maxRetries)
            {
                attempt++;
                await ModelClientRetry.DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
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
                _logger?.LogDebug(ex, "Anthropic stream line failed to parse, skipping: {Fragment}",
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

        // Anthropic SSE：event: <type> 与 data: <json> 交替出现，逐行翻译为 OpenAI data 行。
        // 限长行读取：单行超上限立即中断，替代无行长限制的 StreamReader.ReadLineAsync。
        string? pendingEvent = null;
        bool doneSent = false;
        await foreach (string line in BoundedResponseReader.ReadLinesAsync(
                response.Content.ReadAsStream(cancellationToken),
                idleTimeout: ModelClientRetry.ResolveCallTimeout(_endpoint),
                cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                pendingEvent = line["event: ".Length..].Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                string data = line["data: ".Length..].Trim();
                if (string.IsNullOrEmpty(data)) continue;

                string? translated = AnthropicTranslators.TranslateStreamEvent(pendingEvent ?? string.Empty, data);
                if (translated == "[DONE]")
                {
                    yield return new RawStreamLine("[DONE]", null, null);
                    doneSent = true;
                    break;
                }
                else if (translated is not null)
                {
                    yield return new RawStreamLine(translated, ExtractUsage(translated), null);
                }
                pendingEvent = null;
            }
        }

        if (!doneSent)
        {
            yield return new RawStreamLine("[DONE]", null, null);
        }
    }

    /// <inheritdoc />
    public async Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
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
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
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
        // 重试仅覆盖“拿到成功响应头”之前的阶段（与 OpenAI 客户端流式重试一致）；
        // 响应体流一旦开始下发不再重试，避免重复输出。建连阶段按 TimeoutSeconds 施加总时长上限。
        int maxRetries = _endpoint.MaxRetries;
        int attempt = 0;
        TimeSpan connectTimeout = ModelClientRetry.ResolveCallTimeout(_endpoint);
        while (true)
        {
            try
            {
                using var content = new StringContent(
                    AnthropicTranslators.BuildRequestBody(request, _endpoint),
                    Encoding.UTF8,
                    "application/json");
                content.Headers.ContentType!.CharSet = "utf-8";

                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, MessagesPath) { Content = content };
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                var response = await ModelClientRetry.WithTotalTimeout(
                    connectTimeout, ct,
                    token => _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = response.StatusCode;
                    string errorBody = await BoundedResponseReader.ReadBodyAsync(response.Content, ct).ConfigureAwait(false);
                    response.Dispose();
                    if (ModelClientRetry.IsRetryable(statusCode) && attempt < maxRetries)
                    {
                        attempt++;
                        await ModelClientRetry.DelayWithJitterAsync(attempt, ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new ModelClientException(statusCode, errorBody);
                }
                return response;
            }
            catch (Exception ex) when (ModelClientRetry.IsExceptionRetryable(ex) && attempt < maxRetries)
            {
                attempt++;
                await ModelClientRetry.DelayWithJitterAsync(attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 从翻译后的 OpenAI JSON 提取 usage（流式行内也可能携带）。
    /// </summary>
    private static ChatUsage? ExtractUsage(string openAiLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(openAiLine);
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

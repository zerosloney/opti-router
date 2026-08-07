using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.IO.Pipelines;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// OpenAI 兼容模型客户端，基于 HttpClient 实现。
/// </summary>
public sealed class OpenAICompatibleModelClient : IModelClient
{
    /// <summary>
    /// 流式响应单行最大字节数。防恶意上游发送超长单行撑爆内存。
    /// 正常 OpenAI SSE chunk 远小于此（通常 &lt;1KB）。
    /// </summary>
    private const int MaxStreamLineBytes = 1024 * 1024; // 1 MB

    private static readonly JsonSerializerOptions _serializeOptions = new()
    {
        PropertyNamingPolicy = new JsonSnakeCaseNamingPolicy(),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions _deserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = new JsonSnakeCaseNamingPolicy()
    };

    private readonly HttpClient _httpClient;
    private readonly ModelEndpointOptions _endpoint;

    /// <inheritdoc />
    public ModelEndpointOptions Endpoint => _endpoint;

    /// <summary>
    /// 初始化 OpenAI 兼容模型客户端。
    /// </summary>
    /// <param name="endpoint">端点配置。</param>
    /// <param name="httpClient">已配置 BaseAddress、Timeout 与 Authorization 的 HttpClient。</param>
    public OpenAICompatibleModelClient(ModelEndpointOptions endpoint, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);

        _endpoint = endpoint;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request with { Model = _endpoint.Name, Stream = false };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(json, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ModelClientException(response.StatusCode, responseBody);
        }

        return JsonSerializer.Deserialize<ChatResponse>(responseBody, _deserializeOptions)
            ?? throw new ModelClientException(response.StatusCode, responseBody, "Empty response body.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request with { Model = _endpoint.Name, Stream = true };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(json, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ModelClientException(response.StatusCode, errorBody);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // PipeReader 限单行字节，防恶意上游发超长单行撑爆内存（与 StreamRawAsync 一致的防御）。
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 8 * 1024));
        int pendingLineBytes = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            while (TryReadLine(ref buffer, out var lineBytes))
            {
                pendingLineBytes = 0;
                if (lineBytes.IsEmpty) continue;

                byte[] lineArr = lineBytes.ToArray();
                var lineSpan = (ReadOnlySpan<byte>)lineArr;
                if (!lineSpan.StartsWith("data: "u8))
                    continue;

                var dataSpan = lineSpan.Slice("data: ".Length);
                if (dataSpan.Length > 0 && dataSpan[^1] == (byte)'\r')
                    dataSpan = dataSpan[..^1];

                if (dataSpan.SequenceEqual("[DONE]"u8))
                {
                    await reader.CompleteAsync().ConfigureAwait(false);
                    yield break;
                }

                var data = System.Text.Encoding.UTF8.GetString(dataSpan);

                // 解析 JSON，失败则跳过该行并继续。
                RawStreamChunk? raw = null;
                try
                {
                    raw = JsonSerializer.Deserialize<RawStreamChunk>(data, _deserializeOptions);
                }
                catch
                {
                    continue;
                }

                if (raw is null)
                    continue;

            var chunk = new ChatStreamChunk
            {
                Id = raw.Id,
                DeltaContent = raw.Choices.Count > 0 ? raw.Choices[0].Delta.Content : null,
                FinishReason = raw.Choices.Count > 0 ? raw.Choices[0].FinishReason : null,
                Usage = raw.Usage
            };

                yield return chunk;
            }

            // 剩余未遇换行的字节：累计，超限则中断。
            pendingLineBytes += (int)buffer.Length;
            if (pendingLineBytes > MaxStreamLineBytes)
            {
                await reader.CompleteAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Upstream stream line exceeded {MaxStreamLineBytes} bytes; aborting to prevent OOM.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                await reader.CompleteAsync().ConfigureAwait(false);
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public async Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var probeRequest = new ChatRequest
        {
            Model = _endpoint.Name,
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds, "Probe timed out.");
        }
        catch (ModelClientException ex)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<RawChatResponse> CompleteRawAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request with { Model = _endpoint.Name, Stream = false };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        int maxRetries = _endpoint.MaxRetries;
        int attempt = 0;

        while (true)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                httpRequest.Content = new StringContent(json, Encoding.UTF8);
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var exception = new ModelClientException(response.StatusCode, responseBody);
                    if (IsRetryable(response.StatusCode) && attempt < maxRetries)
                    {
                        attempt++;
                        await DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw exception;
                }

                return new RawChatResponse(responseBody, TryExtractUsage(responseBody));
            }
            catch (Exception ex) when (IsExceptionRetryable(ex) && attempt < maxRetries)
            {
                attempt++;
                await DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawStreamLine> StreamRawAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request with { Model = _endpoint.Name, Stream = true };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        HttpResponseMessage? response = null;
        int maxRetries = _endpoint.MaxRetries;
        int attempt = 0;

        while (true)
        {
            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                httpRequest.Content = new StringContent(json, Encoding.UTF8);
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = response.StatusCode;
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var exception = new ModelClientException(statusCode, errorBody);
                    response.Dispose();
                    response = null;

                    if (IsRetryable(statusCode) && attempt < maxRetries)
                    {
                        attempt++;
                        await DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw exception;
                }

                break; // 成功
            }
            catch (Exception ex) when (IsExceptionRetryable(ex) && attempt < maxRetries)
            {
                response?.Dispose();
                response = null;
                attempt++;
                await DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // PipeReader 逐行扫描，单行累计字节超 MaxStreamLineBytes 则中断，防恶意上游发超长单行撑爆内存
            // （StreamReader.ReadLineAsync 无单行上限，先读入再检查为时已晚）。
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 8 * 1024));
            int pendingLineBytes = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                // 在 buffer 中找换行符，逐行处理。
                while (TryReadLine(ref buffer, out var lineBytes))
                {
                    pendingLineBytes = 0;
                    if (lineBytes.IsEmpty) continue; // 空行跳过

                    // 取行内容为字节数组（SSE 单行通常 <1KB，ToArray 开销可忽略）。
                    byte[] lineArr = lineBytes.ToArray();
                    var lineSpan = (ReadOnlySpan<byte>)lineArr;
                    if (!lineSpan.StartsWith("data: "u8))
                        continue;

                    var dataSpan = lineSpan.Slice("data: ".Length);
                    // 去尾随 \r（兼容 CRLF）。
                    if (dataSpan.Length > 0 && dataSpan[^1] == (byte)'\r')
                        dataSpan = dataSpan[..^1];

                    if (dataSpan.SequenceEqual("[DONE]"u8))
                    {
                        await reader.CompleteAsync().ConfigureAwait(false);
                        yield return new RawStreamLine("[DONE]", null);
                        yield break;
                    }

                    var data = System.Text.Encoding.UTF8.GetString(dataSpan);
                    yield return new RawStreamLine(data, TryExtractUsage(data));
                }

                // 剩余未遇换行的字节：累计，超限则中断。
                pendingLineBytes += (int)buffer.Length;
                if (pendingLineBytes > MaxStreamLineBytes)
                {
                    await reader.CompleteAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Upstream stream line exceeded {MaxStreamLineBytes} bytes; aborting to prevent OOM.");
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    await reader.CompleteAsync().ConfigureAwait(false);
                    yield break;
                }
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>从 buffer 头部读取一行（到 \n，不含），推进 buffer。返回 false 表示无完整行。</summary>
    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var position = buffer.PositionOf((byte)'\n');
        if (position is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private static bool IsRetryable(System.Net.HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is 408 or 429 or >= 500 and <= 599;
    }

    private static bool IsExceptionRetryable(Exception ex)
    {
        // HttpRequestException（DNS/连接/RST 等网络错）→ 重试。
        if (ex is HttpRequestException)
            return true;

        // HttpClient 超时抛 TaskCanceledException/OperationCanceledException，InnerException 为 TimeoutException。
        // 此为客户端内部超时（瞬时），应重试；外部 cancellationToken 主动取消（无 TimeoutException inner）不重试。
        if (ex is OperationCanceledException)
            return ex.InnerException is TimeoutException;

        return false;
    }

    private static async Task DelayWithJitterAsync(int attempt, CancellationToken cancellationToken)
    {
        // Exponential backoff: base = 2^attempt * 100 ms
        int baseDelayMs = (int)Math.Pow(2, attempt) * 100;
        int jitterMs = Random.Shared.Next(0, 100);
        await Task.Delay(baseDelayMs + jitterMs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从 OpenAI 兼容 JSON 中提取 token 用量（usage.prompt_tokens 等）。
    /// 字段缺失或非 JSON 时返回 null。
    /// </summary>
    private static ChatUsage? TryExtractUsage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return null;

            int prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out int pv) ? pv : 0;
            int completion = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out int cv) ? cv : 0;
            int total = usage.TryGetProperty("total_tokens", out var t) && t.TryGetInt32(out int tv) ? tv : prompt + completion;
            return new ChatUsage { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = total };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

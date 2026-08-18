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
    /// 正常 OpenAI SSE chunk 远小于此（通常 &lt;1KB）。与 <see cref="BoundedResponseReader"/> 共用同值。
    /// </summary>
    private const int MaxStreamLineBytes = BoundedResponseReader.MaxStreamLineBytes;

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
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;

    /// <inheritdoc />
    public ModelEndpointOptions Endpoint => _endpoint;

    /// <summary>
    /// 初始化 OpenAI 兼容模型客户端。
    /// </summary>
    /// <param name="endpoint">端点配置。</param>
    /// <param name="httpClient">已配置 BaseAddress、Timeout 与 Authorization 的 HttpClient。</param>
    /// <param name="logger">可选日志，用于流式解析降级的诊断记录。</param>
    public OpenAICompatibleModelClient(ModelEndpointOptions endpoint, HttpClient httpClient, Microsoft.Extensions.Logging.ILogger? logger = null)
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
        ArgumentNullException.ThrowIfNull(request);

        var body = request with { Model = _endpoint.UpstreamModelId, Stream = false };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(json, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var responseSw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        responseSw.Stop();
        var metadata = UpstreamResponseMetadataNormalizer.Normalize(response, responseSw.ElapsedMilliseconds);
        var responseBody = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new ModelClientException(response.StatusCode, responseBody, metadata: metadata);
        }

        return JsonSerializer.Deserialize<ChatResponse>(responseBody, _deserializeOptions)
            ?? throw new ModelClientException(response.StatusCode, responseBody, "Empty response body.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request with { Model = _endpoint.UpstreamModelId, Stream = true };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(json, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var responseSw = System.Diagnostics.Stopwatch.StartNew();
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        responseSw.Stop();
        var metadata = UpstreamResponseMetadataNormalizer.Normalize(response, responseSw.ElapsedMilliseconds);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            throw new ModelClientException(response.StatusCode, errorBody, metadata: metadata);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // PipeReader 限单行字节，防恶意上游发超长单行撑爆内存（与 StreamRawAsync 一致的防御）。
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 8 * 1024));
        int pendingLineBytes = 0;

        // 同步局部函数：解析 SSE 行数据，避免在 async 方法中使用 ReadOnlySpan<byte>
        string? ProcessLineData(in ReadOnlySequence<byte> lineBytes, ref byte[]? rented)
        {
            rented = null;
            if (lineBytes.IsEmpty) return null;

            var lineSpan = lineBytes.IsSingleSegment
                ? lineBytes.FirstSpan
                : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent((int)lineBytes.Length)).AsSpan(0, (int)lineBytes.Length);

            if (!lineBytes.IsSingleSegment && rented is not null)
            {
                lineBytes.CopyTo(rented);
            }

            if (!lineSpan.StartsWith("data: "u8))
                return null;

            var dataSpan = lineSpan.Slice("data: ".Length);
            if (dataSpan.Length > 0 && dataSpan[^1] == (byte)'\r')
                dataSpan = dataSpan[..^1];

            if (dataSpan.SequenceEqual("[DONE]"u8))
                return "[DONE]";

            return System.Text.Encoding.UTF8.GetString(dataSpan);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            while (TryReadLine(ref buffer, out var lineBytes))
            {
                pendingLineBytes = 0;

                byte[]? rented = null;
                try
                {
                    var data = ProcessLineData(lineBytes, ref rented);
                    if (data is null) continue;

                    if (data == "[DONE]")
                    {
                        await reader.CompleteAsync().ConfigureAwait(false);
                        yield break;
                    }

                    // 解析 JSON，失败则跳过该行并继续。
                    RawStreamChunk? raw = null;
                    try
                    {
                        raw = JsonSerializer.Deserialize<RawStreamChunk>(data, _deserializeOptions);
                    }
                    catch (Exception ex)
                    {
                        // 降级保留诊断线索：协议不兼容问题不再完全静默
                        _logger?.LogDebug(ex, "OpenAI stream line failed to parse, skipping: {Fragment}",
                            data.Length > 200 ? data[..200] : data);
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
                finally
                {
                    if (rented is not null)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            // 剩余未遇换行的字节：即当前 buffer 中的未消费字节长度，超限则中断。
            pendingLineBytes = (int)buffer.Length;
            if (pendingLineBytes > MaxStreamLineBytes)
            {
                await reader.CompleteAsync().ConfigureAwait(false);
                throw new ResponseSizeLimitExceededException(MaxStreamLineBytes,
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds, "Probe timed out.");
        }
        catch (ModelClientException ex)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds, ex.Message, ex.StatusCode, ex.Metadata);
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

        var body = request with { Model = _endpoint.UpstreamModelId, Stream = false };
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

                var responseSw = System.Diagnostics.Stopwatch.StartNew();
                using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                responseSw.Stop();
                var metadata = UpstreamResponseMetadataNormalizer.Normalize(response, responseSw.ElapsedMilliseconds);
                var responseBody = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var exception = new ModelClientException(response.StatusCode, responseBody, metadata: metadata);
                    if (IsRetryable(response.StatusCode) && attempt < maxRetries)
                    {
                        attempt++;
                        await DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw exception;
                }

                return new RawChatResponse(responseBody, TryExtractUsage(responseBody), metadata);
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

        var body = request with { Model = _endpoint.UpstreamModelId, Stream = true };
        var json = JsonSerializer.Serialize(body, _serializeOptions);

        HttpResponseMessage? response = null;
        UpstreamResponseMetadata? responseMetadata = null;
        System.Diagnostics.Stopwatch? responseSw = null;
        int maxRetries = _endpoint.MaxRetries;
        int attempt = 0;

        while (true)
        {
            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                httpRequest.Content = new StringContent(json, Encoding.UTF8);
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                responseSw = System.Diagnostics.Stopwatch.StartNew();
                response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                responseMetadata = UpstreamResponseMetadataNormalizer.Normalize(response, responseSw.ElapsedMilliseconds);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = response.StatusCode;
                    try
                    {
                        var errorBody = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                        var exception = new ModelClientException(statusCode, errorBody, metadata: responseMetadata);

                        if (IsRetryable(statusCode) && attempt < maxRetries)
                        {
                            attempt++;
                            await DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        throw exception;
                    }
                    finally
                    {
                        response.Dispose();
                        response = null;
                    }
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
            bool isFirstDataItem = true;
            var successfulResponse = response ?? throw new InvalidOperationException("Upstream response was not available after retries.");
            await using var stream = await successfulResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // PipeReader 逐行扫描，单行累计字节超 MaxStreamLineBytes 则中断，防恶意上游发超长单行撑爆内存
            // （StreamReader.ReadLineAsync 无单行上限，先读入再检查为时已晚）。
            var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 8 * 1024));
            int pendingLineBytes = 0;

            // 同步局部函数：解析 SSE 行数据，避免在 async 方法中使用 ReadOnlySpan<byte>
            string? ProcessLineData(in ReadOnlySequence<byte> lineBytes, ref byte[]? rented)
            {
                rented = null;
                if (lineBytes.IsEmpty) return null;

                var lineSpan = lineBytes.IsSingleSegment
                    ? lineBytes.FirstSpan
                    : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent((int)lineBytes.Length)).AsSpan(0, (int)lineBytes.Length);

                if (!lineBytes.IsSingleSegment && rented is not null)
                {
                    lineBytes.CopyTo(rented);
                }

                if (!lineSpan.StartsWith("data: "u8))
                    return null;

                var dataSpan = lineSpan.Slice("data: ".Length);
                // 去尾随 \r（兼容 CRLF）。
                if (dataSpan.Length > 0 && dataSpan[^1] == (byte)'\r')
                    dataSpan = dataSpan[..^1];

                if (dataSpan.SequenceEqual("[DONE]"u8))
                    return "[DONE]";

                return System.Text.Encoding.UTF8.GetString(dataSpan);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                // 在 buffer 中找换行符，逐行处理。
                while (TryReadLine(ref buffer, out var lineBytes))
                {
                    pendingLineBytes = 0;

                    byte[]? rented = null;
                    try
                    {
                        var data = ProcessLineData(lineBytes, ref rented);
                        if (data is null) continue;

                        if (data == "[DONE]")
                        {
                            await reader.CompleteAsync().ConfigureAwait(false);
                            UpstreamResponseMetadata? firstMetadata = null;
                            if (isFirstDataItem && responseMetadata is not null)
                            {
                                responseSw?.Stop();
                                firstMetadata = responseMetadata with
                                {
                                    TimeToFirstTokenMs = responseSw?.ElapsedMilliseconds
                                };
                                isFirstDataItem = false;
                            }
                            yield return new RawStreamLine("[DONE]", null, firstMetadata);
                            yield break;
                        }
                        UpstreamResponseMetadata? lineMetadata = null;
                        if (isFirstDataItem && responseMetadata is not null)
                        {
                            responseSw?.Stop();
                            lineMetadata = responseMetadata with
                            {
                                TimeToFirstTokenMs = responseSw?.ElapsedMilliseconds
                            };
                            isFirstDataItem = false;
                        }
                        yield return new RawStreamLine(data, TryExtractUsage(data), lineMetadata);
                    }
                    finally
                    {
                        if (rented is not null)
                        {
                            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                        }
                    }
                }

                // 剩余未遇换行的字节：即当前 buffer 中的未消费字节长度，超限则中断。
                pendingLineBytes = (int)buffer.Length;
                if (pendingLineBytes > MaxStreamLineBytes)
                {
                    await reader.CompleteAsync().ConfigureAwait(false);
                    throw new ResponseSizeLimitExceededException(MaxStreamLineBytes,
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
        // 429 is intentionally surfaced to request-level orchestration so quota
        // state is updated and another candidate can be selected immediately.
        return code is 408 or >= 500 and <= 599;
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

    /// <summary>
    /// 在完整物化前读取有限大小的 UTF-8 响应体（共享实现，见 <see cref="BoundedResponseReader"/>）。
    /// </summary>
    private static Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
        => BoundedResponseReader.ReadBodyAsync(content, cancellationToken);

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
            if (!doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
                return null;

            int prompt = GetNonNegativeInt32(usage, "prompt_tokens") ?? 0;
            int completion = GetNonNegativeInt32(usage, "completion_tokens") ?? 0;
            int inferredTotal = prompt > int.MaxValue - completion ? int.MaxValue : prompt + completion;
            int total = GetNonNegativeInt32(usage, "total_tokens") ?? inferredTotal;

            int? cachedRaw = null;
            int? writeRaw = null;
            if (usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                cachedRaw = GetNonNegativeInt32(details, "cached_tokens");
                writeRaw = GetNonNegativeInt32(details, "cache_write_tokens")
                    ?? GetNonNegativeInt32(details, "cache_creation_tokens");
            }

            cachedRaw ??= GetNonNegativeInt32(usage, "prompt_cache_hit_tokens");
            writeRaw ??= GetNonNegativeInt32(usage, "cache_write_input_tokens")
                ?? GetNonNegativeInt32(usage, "cache_creation_input_tokens");
            int? missRaw = GetNonNegativeInt32(usage, "prompt_cache_miss_tokens");

            int cached = Math.Min(cachedRaw ?? 0, prompt);
            int write = Math.Min(writeRaw ?? 0, Math.Max(0, prompt - cached));
            int availableUncached = Math.Max(0, prompt - cached - write);
            bool hasBreakdown = cachedRaw is not null || writeRaw is not null || missRaw is not null;
            // A provider-reported miss count is trustworthy only when the complete
            // normalized breakdown agrees with prompt_tokens. Otherwise charge and
            // audit the safe remainder so inconsistent optional fields cannot make
            // input tokens disappear (or become negative).
            int uncached = hasBreakdown ? availableUncached : 0;

            return new ChatUsage
            {
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = Math.Max(total, inferredTotal),
                CachedInputTokens = cached,
                CacheWriteInputTokens = write,
                UncachedInputTokens = uncached
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? GetNonNegativeInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int parsed)
            && parsed >= 0
            ? parsed
            : null;
    }
}

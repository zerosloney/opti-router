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

        // 200 但 body 携带 error 字段（OpenRouter/Zen 类聚合网关形态）：视为上游失败而非内容，
        // 让消费方走既有失败路径（failover/审计/熔断），不再假成功。
        var typedInBand = ExtractInBandError(responseBody, response.StatusCode, metadata);
        if (typedInBand is not null)
            throw typedInBand;

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

                    // 流内 error 事件：200 流里直接推 {"error":{...}}，抛异常走失败路径而非当内容中继。
                    var typedInBand = ExtractInBandError(data, response.StatusCode, metadata);
                    if (typedInBand is not null)
                    {
                        await reader.CompleteAsync().ConfigureAwait(false);
                        throw typedInBand;
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
    public async Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var probeRequest = new ChatRequest
        {
            Model = _endpoint.UpstreamModelId,
            // 探活问题带身份核对语义，回答经 Reply 回显管理台（"看回答"验证模型身份/连通）。
            Messages = new List<ChatMessage> { ChatMessage.FromText("user", "你是什么模型") },
            // 不设 max_tokens（null 不序列化）：reasoning 模型（如 ox-alpha）思考会耗尽
            // 小额度导致内容为空，上游直接 500 "empty response content"（实测 1~32 均 500）。
            // 不限额由上游取默认，探活回复本身极短，成本可忽略。
            // 流式探活：部分网关非流式补全会无限挂起（commandcode.ai 的 GLM-5.3-Flash 实测
            // 60s+ 不回响应头，流式正常）——探活走流式并消费到 [DONE]/流结束即判成功。
            MaxTokens = null,
            Stream = true
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));

            var replyBuilder = new System.Text.StringBuilder();
            await foreach (var line in StreamRawAsync(probeRequest, cts.Token).ConfigureAwait(false))
            {
                if (line.Data == "[DONE]")
                    break;
                string? delta = ExtractProbeDelta(line.Data);
                if (!string.IsNullOrEmpty(delta))
                    replyBuilder.Append(delta);
            }
            sw.Stop();

            string? reply = replyBuilder.ToString().Trim();
            return new ModelHealthResult(true, (int)sw.Elapsed.TotalMilliseconds, Reply: string.IsNullOrWhiteSpace(reply) ? null : reply);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new ModelHealthResult(false, (int)sw.Elapsed.TotalMilliseconds, ex.Message);
        }
        // 外部取消（cancellationToken 已取消）：异常向上传播，由探活服务识别为关停信号，
        // 不计失败——曾在此被转成 Healthy=false，导致关停噪声熔断健康模型。
    }

    /// <summary>探活流式 data 行 → 增量文本。解析失败静默跳过；SSE 注释行在 ProcessLineData 已过滤。</summary>
    private string? ExtractProbeDelta(string? data)
    {
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            return null;
        try
        {
            var raw = JsonSerializer.Deserialize<RawStreamChunk>(data, _deserializeOptions);
            return raw is { Choices.Count: > 0 } ? raw.Choices[0].Delta.Content : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 从原始响应体提取首条回答文本：标准格式 choices 在根节点；
    /// 部分聚合上游（如 cline/stealth 的 ox-alpha）把 choices 包在 data 字段下。
    /// </summary>
    private static string? ExtractReplyText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement choices = root.TryGetProperty("choices", out var standard) ? standard
                : root.TryGetProperty("data", out var wrapped)
                  && wrapped.ValueKind == JsonValueKind.Object
                  && wrapped.TryGetProperty("choices", out var nested) ? nested
                : default;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;

            return choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
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
        TimeSpan totalTimeout = ModelClientRetry.ResolveCallTimeout(_endpoint);

        while (true)
        {
            try
            {
                var (status, responseBody, metadata) = await ModelClientRetry.WithTotalTimeout(
                    totalTimeout, cancellationToken, async token =>
                    {
                        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                        httpRequest.Content = new StringContent(json, Encoding.UTF8);
                        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                        var responseSw = System.Diagnostics.Stopwatch.StartNew();
                        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        responseSw.Stop();
                        var metadata = UpstreamResponseMetadataNormalizer.Normalize(response, responseSw.ElapsedMilliseconds);
                        var responseBody = await ReadResponseBodyAsync(response.Content, token).ConfigureAwait(false);
                        return (response.StatusCode, responseBody, metadata);
                    }).ConfigureAwait(false);

                if (!IsSuccessStatusCode(status))
                {
                    if (ModelClientRetry.IsRetryable(status) && attempt < maxRetries)
                    {
                        attempt++;
                        await ModelClientRetry.DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new ModelClientException(status, responseBody, metadata: metadata);
                }

                // 200 但 body 携带 error 字段（聚合网关排队后吐错误的形态）：抛异常走失败路径，
                // 此前被当成功返回，编排器审计假成功、熔断器无感。
                var rawInBand = ExtractInBandError(responseBody, status, metadata);
                if (rawInBand is not null)
                    throw rawInBand;

                return new RawChatResponse(responseBody, TryExtractUsage(responseBody), metadata);
            }
            catch (Exception ex) when (ModelClientRetry.IsExceptionRetryable(ex) && attempt < maxRetries)
            {
                attempt++;
                await ModelClientRetry.DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);
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
        // 建连（响应头）阶段 = 总时长上限；响应体阶段 = 空闲上限（同值复用 TimeoutSeconds 语义）。
        TimeSpan callTimeout = ModelClientRetry.ResolveCallTimeout(_endpoint);

        while (true)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
                httpRequest.Content = new StringContent(json, Encoding.UTF8);
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                responseSw = System.Diagnostics.Stopwatch.StartNew();
                response = await ModelClientRetry.WithTotalTimeout(
                    callTimeout, cancellationToken,
                    token => _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token)).ConfigureAwait(false);
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

        // 空闲超时：读到数据即重置计时；超过 callTimeout 无任何新字节 → 断流。
        // 不设总时长上限——持续推进的长生成流不会被中途腰斩（TTFB 由 StreamFirstTokenTimeoutMs/建连超时约束）。
        CancellationTokenSource? idleCts = null;
        TimeSpan idleTimeout = callTimeout;

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

                ReadResult result;
                try
                {
                    CancellationToken readToken = ModelClientRetry.IdleReadToken(ref idleCts, idleTimeout, cancellationToken);
                    result = await reader.ReadAsync(readToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idleCts is not null && ModelClientRetry.IsIdleTimeout(idleCts, cancellationToken))
                {
                    await reader.CompleteAsync().ConfigureAwait(false);
                    throw ModelClientRetry.IdleTimeoutException(idleTimeout);
                }
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

                        // 流内 error 事件：200 流里直接推 {"error":{...}}（Zen/OpenRouter 排队后吐错误
                        // 的形态），抛异常交由消费方既有失败机器接管（failover/审计/熔断/探活），
                        // 此前被当普通数据行原样中继，客户端渲染成错误而审计假成功。
                        var rawInBand = ExtractInBandError(data, response!.StatusCode, responseMetadata);
                        if (rawInBand is not null)
                        {
                            await reader.CompleteAsync().ConfigureAwait(false);
                            throw rawInBand;
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
            idleCts?.Dispose();
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
        => ModelClientRetry.IsRetryable(statusCode);

    private static bool IsSuccessStatusCode(System.Net.HttpStatusCode statusCode)
        => (int)statusCode is >= 200 and <= 299;

    private static bool IsExceptionRetryable(Exception ex)
        => ModelClientRetry.IsExceptionRetryable(ex);

    /// <summary>
    /// 在完整物化前读取有限大小的 UTF-8 响应体（共享实现，见 <see cref="BoundedResponseReader"/>）。
    /// </summary>
    private static Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
        => BoundedResponseReader.ReadBodyAsync(content, cancellationToken);

    private static async Task DelayWithJitterAsync(int attempt, CancellationToken cancellationToken)
        => await ModelClientRetry.DelayWithJitterAsync(attempt, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 从 OpenAI 兼容 JSON 中提取 token 用量（usage.prompt_tokens 等）。
    /// 字段缺失或非 JSON 时返回 null。
    /// </summary>
    /// <summary>
    /// 流内/体内 error 事件检测：200 响应中直接携带 {"error":{...}}（Zen/OpenRouter 类聚合网关
    /// 排队或上游故障时的形态，如 hy3 队列 79s 后吐 "An internal error occurred..."）。
    /// 命中则构造 ModelClientException——消息取上游原文、code 归一到 [400,599]（越界回退 500），
    /// 让既有失败机器（failover/审计/熔断/探活）接管。此前被当普通内容中继，审计假成功、熔断器无感。
    /// </summary>
    internal static ModelClientException? ExtractInBandError(string? payload, System.Net.HttpStatusCode fallbackStatus, UpstreamResponseMetadata? metadata)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Trim() == "[DONE]")
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var err) || err.ValueKind == JsonValueKind.Null)
                return null;

            string? message;
            int? code = null;
            if (err.ValueKind == JsonValueKind.String)
            {
                message = err.GetString();
            }
            else if (err.ValueKind == JsonValueKind.Object)
            {
                message = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : err.GetRawText();
                if (err.TryGetProperty("code", out var c))
                {
                    if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n))
                        code = n;
                    else if (c.ValueKind == JsonValueKind.String && int.TryParse(c.GetString(), out var parsed))
                        code = parsed;
                }
            }
            else
            {
                message = err.GetRawText();
            }
            if (string.IsNullOrWhiteSpace(message))
                message = err.GetRawText();

            int resolved = code is >= 400 and <= 599 ? code.Value
                : (int)fallbackStatus is >= 400 and <= 599 ? (int)fallbackStatus
                : 500;
            return new ModelClientException((System.Net.HttpStatusCode)resolved, payload, message: message, metadata: metadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

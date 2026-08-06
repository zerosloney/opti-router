using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OptiRouter.Configuration;

namespace OptiRouter.Clients;

/// <summary>
/// OpenAI 兼容模型客户端，基于 HttpClient 实现。
/// </summary>
public sealed class OpenAICompatibleModelClient : IModelClient
{
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions");
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

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions");
        httpRequest.Content = new StringContent(json, Encoding.UTF8);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ModelClientException(response.StatusCode, errorBody);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
                yield break;

            // 跳过空行。
            if (line.Length == 0)
                continue;

            // SSE 格式：每行以 "data: " 开头。
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line.Substring("data: ".Length).Trim();

            // 结束标记。
            if (data == "[DONE]")
                yield break;

            // 解析 JSON，失败则跳过该行并继续。
            RawStreamChunk? raw = null;
            try
            {
                raw = JsonSerializer.Deserialize<RawStreamChunk>(data, _deserializeOptions);
            }
            catch
            {
                // JSON 解析失败，跳过该行。
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
    }

    /// <inheritdoc />
    public async Task<ModelHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var probeRequest = new ChatRequest
        {
            Model = _endpoint.Name,
            Messages = new List<ChatMessage> { new ChatMessage { Role = "user", Content = "ping" } },
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
}

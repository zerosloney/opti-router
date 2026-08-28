using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI 兼容 Chat Completions HTTP 端点。
/// 暴露 POST /v1/chat/completions，支持非流式与 SSE 流式两种模式，透明透传上游原始响应。
/// </summary>
public static class ChatCompletionsEndpoint
{
    /// <summary>
    /// 将 /v1/chat/completions 端点映射到路由图。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>同一个 <paramref name="app"/>，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapChatCompletions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", async (HttpContext httpContext, IOptionsMonitor<OptiRouter.Configuration.RouterOptions> optionsMonitor, ProxyOrchestrator orchestrator, CancellationToken ct) =>
        {
            // 手动解析 JSON body：框架参数绑定在 JSON 非法时返回 ProblemDetails（RFC 7231），
            // 而非 OpenAI 兼容的 {"error":{"message","type","code"}} 信封。
            // 改为 ReadFromJsonAsync + catch JsonException，与 Anthropic/Gemini 入口行为一致。
            ChatRequest request;
            try
            {
                request = await httpContext.Request.ReadFromJsonAsync<ChatRequest>(ct).ConfigureAwait(false)
                    ?? new ChatRequest();
            }
            catch (JsonException ex)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    $"Invalid JSON body: {ex.Message}",
                    "invalid_request_error");
            }

            if (TryGetValidationError(request, out var validationError))
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    validationError,
                    "invalid_request_error");
            }

            // 未知模型名按 OpenAI 兼容语义拒绝（404 model_not_found），不静默改路由：
            // 显式指定模型是对路由结果的强约束，拼错的模型名应尽早暴露给客户端。
            var modelProblem = ValidateRequestedModel(request, optionsMonitor.CurrentValue);
            if (modelProblem is not null)
            {
                return modelProblem;
            }

            string? sessionId = httpContext.Request.Headers.TryGetValue("X-Session-Id", out var sid) && !string.IsNullOrWhiteSpace(sid)
                ? sid.ToString()
                : null;

            if (request.Stream)
            {
                IAsyncEnumerator<RawStreamLine>? enumerator = null;
                try
                {
                    enumerator = orchestrator.StreamAsync(request, ct, sessionId).GetAsyncEnumerator(ct);
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                        return Results.Stream(
                            stream => WriteDoneAsync(stream, ct),
                            "text/event-stream");
                    }

                    var firstLine = enumerator.Current;
                    var streamEnumerator = enumerator;
                    return Results.Stream(async stream =>
                    {
                        bool sawFinishReason = false;

                        // 单行转发守卫：[DONE] 前流内从未出现非 null finish_reason（部分聚合网关
                        // 的流从不发终结 chunk），合成 stop 兜底——否则 OpenAI 系客户端（AI SDK）
                        // 判定 "Stream ended without finish_reason" 整个响应失败。
                        async Task RelayLineAsync(RawStreamLine line)
                        {
                            if (line.Data == "[DONE]" && !sawFinishReason)
                            {
                                await WriteLineAsync(stream, CreateFinishStopLine(), ct).ConfigureAwait(false);
                                sawFinishReason = true;
                            }
                            else if (!sawFinishReason && HasFinishReason(line.Data))
                            {
                                sawFinishReason = true;
                            }
                            await WriteLineAsync(stream, line, ct).ConfigureAwait(false);
                        }

                        try
                        {
                            await RelayLineAsync(firstLine).ConfigureAwait(false);
                            while (await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                await RelayLineAsync(streamEnumerator.Current).ConfigureAwait(false);
                            }
                        }
                        catch (BudgetExhaustedException)
                        {
                            await WriteErrorAsync(stream, "budget exhausted", "BUDGET_EXHAUSTED", ct).ConfigureAwait(false);
                        }
                        catch (AllCandidatesFailedException)
                        {
                            await WriteErrorAsync(stream, "all model candidates failed", "ALL_CANDIDATES_FAILED", ct).ConfigureAwait(false);
                        }
                        catch (OptiRouter.Compliance.ComplianceViolationException ex)
                        {
                            await WriteErrorAsync(stream, ex.Message, "CONTENT_MODERATED", ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // 中途失败 code 按异常类型细分，客户端据此判定是否值得重试：
                            // - OperationCanceledException（非外部 ct 取消）：HttpClient 内部超时 → TIMEOUT（可重试）
                            // - HttpRequestException / IOException：上游断连/IO 错误 → UPSTREAM_ERROR（可重试）
                            // - InvalidOperationException：size limit（MaxResponseStreamBytes/MaxStreamLineBytes）→ RESPONSE_TOO_LARGE（不可重试）
                            // - 其余：INTERNAL_ERROR（不可重试）。未预见异常不外发 ex.Message，细节进服务端日志。
                            string code = ClassifyMidStreamError(ex);
                            if (code == "INTERNAL_ERROR")
                            {
                                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "chat.completions");
                                await WriteErrorAsync(stream, ProtocolErrorHelper.InternalErrorMessage, code, ct).ConfigureAwait(false);
                            }
                            else
                            {
                                await WriteErrorAsync(stream, ex.Message, code, ct).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            await streamEnumerator.DisposeAsync().ConfigureAwait(false);
                        }
                    }, "text/event-stream");
                }
                catch (ModelClientException ex)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateUpstreamRejection(ex, httpContext);
                }
                catch (BudgetExhaustedException)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream("budget exhausted", "BUDGET_EXHAUSTED", ct);
                }
                catch (AllCandidatesFailedException)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream("all model candidates failed", "ALL_CANDIDATES_FAILED", ct);
                }
                catch (OptiRouter.Compliance.ComplianceViolationException ex)
                {
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    return CreateErrorStream(ex.Message, "CONTENT_MODERATED", ct);
                }
                catch (Exception ex)
                {
                    // 流式首 MoveNextAsync 期间的未预见异常（如 ResponseSizeLimitExceededException）兜底：
                    // 按异常类型分类为机读 code，返回 SSE 错误流而非逃逸为框架 500。
                    if (enumerator is not null)
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    string code = ClassifyMidStreamError(ex);
                    if (code == "INTERNAL_ERROR")
                    {
                        ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "chat.completions");
                        return CreateErrorStream(ProtocolErrorHelper.InternalErrorMessage, code, ct);
                    }
                    return CreateErrorStream(ex.Message, code, ct);
                }
            }

            try
            {
                var response = await orchestrator.SendAsync(request, ct, sessionId).ConfigureAwait(false);
                // 透明透传：直接回传上游原始 JSON，不 re-serialize。
                return Results.Content(response.Body, "application/json", Encoding.UTF8);
            }
            catch (BudgetExhaustedException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                // 日预算在 UTC 午夜重置，Retry-After 设为到午夜的剩余秒数（1~86400）。
                int retryAfterSeconds = Math.Max(1, (int)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds);
                httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
                var details = new Dictionary<string, object?>
                {
                    ["retryAfterSeconds"] = retryAfterSeconds
                };
                if (requestId != null) details["requestId"] = requestId;

                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status429TooManyRequests,
                    $"Budget exhausted: {ex.Message}",
                    "BUDGET_EXHAUSTED",
                    details);
            }
            catch (OptiRouter.Compliance.ComplianceViolationException ex)
            {
                // 内容审核拦截（输入违规拒绝 / 输出违规中断）：客户端可据此调整输入或终止重试。
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var details = new Dictionary<string, object?>();
                if (ex.MatchedKeyword != null) details["category"] = ex.MatchedKeyword;
                if (requestId != null) details["requestId"] = requestId;

                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    ex.Message,
                    "CONTENT_MODERATED",
                    details);
            }
            catch (AllCandidatesFailedException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                string message = $"All model candidates failed. Attempted: {string.Join(", ", ex.AttemptedModels)}. Last failure: Model '{ex.LastModelName}' returned status {ex.LastStatusCode}.";
                var details = new Dictionary<string, object?>
                {
                    ["attemptedModels"] = ex.AttemptedModels
                };
                if (requestId != null) details["requestId"] = requestId;
                if (ex.LastModelName != null)
                {
                    details["lastError"] = new Dictionary<string, object?>
                    {
                        ["model"] = ex.LastModelName,
                        ["statusCode"] = ex.LastStatusCode,
                        ["message"] = ex.LastErrorMessage
                    };
                }

                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status503ServiceUnavailable,
                    message,
                    "ALL_CANDIDATES_FAILED",
                    details);
            }
            catch (ModelClientException ex)
            {
                return CreateUpstreamRejection(ex, httpContext);
            }
            catch (Exception ex)
            {
                // 非流式路径 catch-all：CostCalculator/策略链/翻译管线等抛出的未预见异常兜底，
                // 返回 OpenAI 兼容 500 错误信封而非逃逸为框架默认 ProblemDetails。
                // 未预见异常不外发 ex.Message（可能含内部细节），细节进服务端日志。
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "chat.completions");
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var details = new Dictionary<string, object?>();
                if (requestId != null) details["requestId"] = requestId;

                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status500InternalServerError,
                    ProtocolErrorHelper.InternalErrorMessage,
                    "INTERNAL_ERROR",
                    details);
            }
        });

        return app;
    }

    /// <summary>
    /// 校验请求的 model 字段：auto 语义（空/auto）与可解析的模型引用放行——
    /// 路由名、显示 ID "{供应商}/{Id}"（可带 #N）或裸上游 Id；未知模型名返回 OpenAI 兼容 404。
    /// </summary>
    private static IResult? ValidateRequestedModel(ChatRequest request, OptiRouter.Configuration.RouterOptions options)
    {
        if (!IsKnownModel(request.Model, options))
        {
            return ProtocolErrorHelper.CreateOpenAiResult(
                StatusCodes.Status404NotFound,
                $"The model '{request.Model}' does not exist or is not enabled. Use 'auto' for smart routing, or GET /v1/models for available model ids.",
                "model_not_found");
        }

        return null;
    }

    /// <summary>
    /// 模型引用是否可解析：auto 语义（空/auto）、路由名、显示 ID 或裸上游 Id。
    /// 各协议入口（OpenAI/Anthropic/Gemini）共享同一判定，仅错误信封不同。
    /// </summary>
    internal static bool IsKnownModel(string? model, OptiRouter.Configuration.RouterOptions options)
    {
        if (Routing.ExplicitModelPolicy.IsAutoRouting(model))
        {
            return true;
        }

        var enabled = options.Models.Where(m => m.Enabled).ToList();
        return ModelDisplayIds.Resolve(enabled, model ?? string.Empty).Count > 0;
    }

    private static bool TryGetValidationError(ChatRequest request, out string error)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            error = "Messages must contain at least one item.";
        else if (request.Messages.Any(message => message is null || string.IsNullOrWhiteSpace(message.Role)))
            error = "Each message must have a role.";
        else if (request.Messages.Any(message => !HasValidContent(message)))
            error = "Message content must not be null.";
        else if (request.Temperature is < 0 or > 2)
            error = "Temperature must be between 0 and 2.";
        else if (request.MaxTokens is <= 0)
            error = "MaxTokens must be greater than zero.";
        else
            error = string.Empty;

        return error.Length > 0;
    }

    private static bool HasValidContent(ChatMessage message)
    {
        if (message.Content is { } content
            && content.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            return true;
        }

        return string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            && message.ExtensionData?.TryGetValue("tool_calls", out var toolCalls) == true
            && toolCalls.ValueKind == JsonValueKind.Array
            && toolCalls.GetArrayLength() > 0;
    }

    private static IResult CreateUpstreamRejection(ModelClientException exception, HttpContext httpContext)
    {
        string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
        var details = new Dictionary<string, object?>
        {
            ["statusCode"] = (int)exception.StatusCode
        };
        if (requestId != null) details["requestId"] = requestId;

        return ProtocolErrorHelper.CreateOpenAiResult(
            (int)exception.StatusCode,
            "Upstream request rejected",
            "UPSTREAM_REJECTION",
            details);
    }

    private static IResult CreateErrorStream(string error, string code, CancellationToken ct)
    {
        return Results.Stream(
            stream => WriteErrorAsync(stream, error, code, ct),
            "text/event-stream");
    }

    /// <summary>
    /// 透传原始 SSE data 行。客户端自己负责按 OpenAI 格式发送 [DONE]。
    /// </summary>
    private static async Task WriteLineAsync(Stream stream, RawStreamLine line, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {line.Data}\n\n"), ct).ConfigureAwait(false);
    }

    // finish_reason 检测：JSON 转义下 content 内字面引号必为 \"，裸 "finish_reason":" 只能
    // 来自结构字段，模型输出里讨论该字段也不会误报。
    private static bool HasFinishReason(string? data)
        => data is not null && data.Contains("\"finish_reason\":\"", StringComparison.Ordinal);

    // 合成终结 chunk：无上游终结行时保证客户端收到的最后一个数据 chunk 带 finish_reason。
    private static RawStreamLine CreateFinishStopLine()
        => new($"{{\"id\":\"optirouter-finish\",\"object\":\"chat.completion.chunk\",\"created\":{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}", null);

    private static async Task WriteErrorAsync(Stream stream, string message, string code, CancellationToken ct)
    {
        // OpenAI 兼容嵌套 error 结构：{"error":{"message":...,"type":...,"code":...}}
        // type 映射与非流式/中间件错误共用同一 helper，客户端按 OpenAI SDK 的 error.type 字段机读错误类别。
        var json = JsonSerializer.Serialize(ProtocolErrorHelper.CreateOpenAiErrorPayload(message, code));
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"data: {json}\n\n"), ct).ConfigureAwait(false);
        await WriteDoneAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 按异常类型把流式中途失败分类为机读 code。
    /// 外部 ct 取消（客户端主动断开）不归类——此时响应流已不可写，调用方不会进 catch。
    /// </summary>
    private static string ClassifyMidStreamError(Exception ex)
    {
        // OperationCanceledException：HttpClient 内部超时（外部 ct 取消时连接已不可写，理论不进 catch）。
        if (ex is OperationCanceledException)
            return "TIMEOUT";
        // ModelClientException 流中途抛出 = 上游故障（断流 502 检测 / 流内 error），外发真实原因
        // 供客户端判断重试——此前落 INTERNAL_ERROR 兜底桶，客户端只见不可读的内部错误文案。
        if (ex is HttpRequestException or IOException or ModelClientException)
            return "UPSTREAM_ERROR";
        // size limit（MaxResponseStreamBytes / MaxStreamLineBytes）专用异常，精确分类。
        if (ex is ResponseSizeLimitExceededException)
            return "RESPONSE_TOO_LARGE";
        // 其余（含通用 InvalidOperationException，即代理真内部 bug）。
        return "INTERNAL_ERROR";
    }

    private static Task WriteDoneAsync(Stream stream, CancellationToken ct)
    {
        return stream.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct).AsTask();
    }
}

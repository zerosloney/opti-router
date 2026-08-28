using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Clients.Protocols;
using OptiRouter.Configuration;

namespace OptiRouter.Endpoints;

/// <summary>
/// Google Gemini generateContent API 兼容 HTTP 端点。
/// 暴露 POST /v1beta/models/{model}:generateContent 与 :streamGenerateContent，
/// 接收 Gemini 原生请求（model 在 URL 路径），翻译为内部 OpenAI 契约进路由管线，
/// 响应（非流式 JSON / 流式 GenerateContentResponse 块）再翻译回 Gemini 格式。
/// 鉴权复用代理鉴权：Authorization: Bearer、x-goog-api-key 头或 ?key= 查询参数。
/// </summary>
public static class GeminiGenerateContentEndpoint
{
    /// <summary>
    /// 将 Gemini generateContent 端点映射到路由图。
    /// 用 catch-all 参数承接模型名：显示 id 形如 "{供应商}/{Id}" 含斜杠，
    /// 常规路由参数不匹配路径分隔符；动作后缀（:generateContent / :streamGenerateContent）在此解析。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>同一个 <paramref name="app"/>，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapGeminiGenerateContent(this IEndpointRouteBuilder app)
    {
        // Gemini 流式要求 alt=sse（SDK 默认携带）；无 alt 时也按 SSE 输出，行为一致
        app.MapPost("/v1beta/models/{**modelAction}",
            async (string modelAction, HttpContext httpContext, IOptionsMonitor<OptiRouter.Configuration.RouterOptions> optionsMonitor, ProxyOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (modelAction.EndsWith(":generateContent", StringComparison.Ordinal))
            {
                return await HandleAsync(modelAction[..^":generateContent".Length], stream: false, httpContext, optionsMonitor, orchestrator, ct);
            }
            if (modelAction.EndsWith(":streamGenerateContent", StringComparison.Ordinal))
            {
                return await HandleAsync(modelAction[..^":streamGenerateContent".Length], stream: true, httpContext, optionsMonitor, orchestrator, ct);
            }
            return GeminiError(StatusCodes.Status404NotFound, "NOT_FOUND",
                $"Unknown Gemini action for model path '{modelAction}'. Expected :generateContent or :streamGenerateContent.");
        });

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string model,
        bool stream,
        HttpContext httpContext,
        IOptionsMonitor<OptiRouter.Configuration.RouterOptions> optionsMonitor,
        ProxyOrchestrator orchestrator,
        CancellationToken ct)
    {
        string body;
        using (var reader = new StreamReader(httpContext.Request.Body))
        {
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        ChatRequest request;
        try
        {
            request = GeminiTranslators.FromGeminiJson(body, model);
        }
        catch (JsonException ex)
        {
            return GeminiError(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", $"Invalid JSON body: {ex.Message}");
        }

        request = request with { Stream = stream };

        if (request.Messages.Count == 0)
        {
            return GeminiError(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "contents: at least one content entry is required.");
        }
        if (request.MaxTokens is <= 0)
        {
            return GeminiError(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "generationConfig.maxOutputTokens must be greater than zero.");
        }
        if (!ChatCompletionsEndpoint.IsKnownModel(request.Model, optionsMonitor.CurrentValue))
        {
            return GeminiError(StatusCodes.Status404NotFound, "NOT_FOUND",
                $"The model '{model}' does not exist or is not enabled. Use 'auto' for smart routing, or GET /v1/models for available model ids.");
        }

        string? sessionId = httpContext.Request.Headers.TryGetValue("X-Session-Id", out var sid) && !string.IsNullOrWhiteSpace(sid)
            ? sid.ToString()
            : null;

        if (stream)
        {
            return await StreamAsync(orchestrator, request, sessionId, httpContext, ct).ConfigureAwait(false);
        }

        try
        {
            var response = await orchestrator.SendAsync(request, ct, sessionId).ConfigureAwait(false);
            string geminiJson = GeminiTranslators.ToGeminiJson(response.Body);
            return Results.Content(geminiJson, "application/json", Encoding.UTF8);
        }
        catch (BudgetExhaustedException)
        {
            // 日预算在 UTC 午夜重置，Retry-After 设为到午夜的剩余秒数。
            int retryAfterSeconds = Math.Max(1, (int)(DateTime.UtcNow.Date.AddDays(1) - DateTime.UtcNow).TotalSeconds);
            httpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
            return GeminiError(StatusCodes.Status429TooManyRequests, "RESOURCE_EXHAUSTED", "budget exhausted");
        }
        catch (OptiRouter.Compliance.ComplianceViolationException ex)
        {
            return GeminiError(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message);
        }
        catch (AllCandidatesFailedException ex)
        {
            return GeminiError(StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE",
                $"Attempted: {string.Join(", ", ex.AttemptedModels)}. Last failure: Model '{ex.LastModelName}' returned status {ex.LastStatusCode}.");
        }
        catch (ModelClientException ex)
        {
            return GeminiError((int)ex.StatusCode, "UNAVAILABLE", $"Upstream request rejected (status {(int)ex.StatusCode}).");
        }
        catch (Exception ex)
        {
            // 非流式路径 catch-all：未预见异常兜底，返回 Gemini 兼容 500 错误信封。
            // 未预见异常不外发 ex.Message（可能含内部细节），细节进服务端日志。
            ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "gemini.generateContent");
            return GeminiError(StatusCodes.Status500InternalServerError, "INTERNAL", ProtocolErrorHelper.InternalErrorMessage);
        }
    }

    private static async Task<IResult> StreamAsync(ProxyOrchestrator orchestrator, ChatRequest request, string? sessionId,
        HttpContext httpContext, CancellationToken ct)
    {
        IAsyncEnumerator<RawStreamLine>? enumerator = null;
        try
        {
            enumerator = orchestrator.StreamAsync(request, ct, sessionId).GetAsyncEnumerator(ct);
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                // 空流也输出带 finishReason 的终结块，客户端才能正常收尾
                var emptyTranslator = new GeminiTranslators.GeminiStreamTranslator(request.Model);
                string payload = string.Join(string.Empty, emptyTranslator.OnData("[DONE]"));
                return Results.Stream(
                    stream => stream.WriteAsync(Encoding.UTF8.GetBytes(payload), ct).AsTask(),
                    "text/event-stream");
            }

            var translator = new GeminiTranslators.GeminiStreamTranslator(request.Model);
            var firstLine = enumerator.Current;
            var streamEnumerator = enumerator;
            return Results.Stream(async stream =>
            {
                try
                {
                    foreach (string block in translator.OnData(firstLine.Data))
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(block), ct).ConfigureAwait(false);
                    }
                    while (await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        foreach (string block in translator.OnData(streamEnumerator.Current.Data))
                        {
                            await stream.WriteAsync(Encoding.UTF8.GetBytes(block), ct).ConfigureAwait(false);
                        }
                    }
                }
                catch (BudgetExhaustedException)
                {
                    await WriteStreamErrorAsync(stream, StatusCodes.Status429TooManyRequests, "RESOURCE_EXHAUSTED", "budget exhausted", ct).ConfigureAwait(false);
                }
                catch (AllCandidatesFailedException)
                {
                    await WriteStreamErrorAsync(stream, StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", "all model candidates failed", ct).ConfigureAwait(false);
                }
                catch (OptiRouter.Compliance.ComplianceViolationException ex)
                {
                    await WriteStreamErrorAsync(stream, StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 上游故障（断流 502 检测/流内 error/连接失败）外发真实原因（可重试信号）；
                    // 其余未预见异常不外发 ex.Message，细节进服务端日志。
                    if (ex is ModelClientException or HttpRequestException or IOException)
                    {
                        await WriteStreamErrorAsync(stream, StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", ex.Message, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "gemini.generateContent");
                        await WriteStreamErrorAsync(stream, StatusCodes.Status500InternalServerError, "INTERNAL",
                            ProtocolErrorHelper.InternalErrorMessage, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await streamEnumerator.DisposeAsync().ConfigureAwait(false);
                }
            }, "text/event-stream");
        }
        catch (ModelClientException)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", "upstream request rejected before first chunk", ct),
                "text/event-stream");
        }
        catch (BudgetExhaustedException)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, StatusCodes.Status429TooManyRequests, "RESOURCE_EXHAUSTED", "budget exhausted", ct),
                "text/event-stream");
        }
        catch (AllCandidatesFailedException)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", "all model candidates failed", ct),
                "text/event-stream");
        }
        catch (OptiRouter.Compliance.ComplianceViolationException ex)
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message, ct),
                "text/event-stream");
        }
        catch (Exception ex)
        {
            // 流式首 MoveNextAsync 期间的异常兜底：返回 SSE 错误流而非逃逸为框架 500。
            // 上游故障外发真实原因；取消 → 超时；其余未预见不外发 ex.Message，细节进服务端日志。
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            int code;
            string status;
            string message;
            if (ex is ModelClientException or HttpRequestException or IOException)
            {
                code = StatusCodes.Status503ServiceUnavailable;
                status = "UNAVAILABLE";
                message = ex.Message;
            }
            else if (ex is OperationCanceledException)
            {
                code = StatusCodes.Status504GatewayTimeout;
                status = "DEADLINE_EXCEEDED";
                message = ex.Message;
            }
            else
            {
                code = StatusCodes.Status500InternalServerError;
                status = "INTERNAL";
                message = ProtocolErrorHelper.InternalErrorMessage;
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "gemini.generateContent");
            }
            return Results.Stream(
                stream => WriteStreamErrorAsync(stream, code, status, message, ct),
                "text/event-stream");
        }
    }

    private static Task WriteStreamErrorAsync(Stream stream, int code, string status, string message, CancellationToken ct)
        => stream.WriteAsync(Encoding.UTF8.GetBytes(GeminiTranslators.GeminiStreamTranslator.OnError(code, status, message)), ct).AsTask();

    private static IResult GeminiError(int code, string status, string message)
        => Results.Json(
            new { error = new { code, message, status } },
            statusCode: code);
}

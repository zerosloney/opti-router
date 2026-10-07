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

        string? sessionId = ChatCompletionsEndpoint.ResolveSessionId(httpContext);

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
            httpContext.Response.Headers["Retry-After"] = ProtocolErrorHelper.SecondsUntilUtcMidnight().ToString();
            return GeminiError(StatusCodes.Status429TooManyRequests, "RESOURCE_EXHAUSTED", "budget exhausted");
        }
        catch (OptiRouter.Compliance.ComplianceViolationException ex)
        {
            return GeminiError(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message);
        }
        catch (AllCandidatesFailedException ex)
        {
            return GeminiError(StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE",
                ProtocolErrorHelper.AllCandidatesFailedSummary(ex));
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

    /// <summary>
    /// 流式路径外壳在 <see cref="ProtocolStreamPipeline"/>，这里只留 Gemini 词汇：
    /// data 行 → SSE 块，异常 → 带 Google status/code 的错误块。
    /// </summary>
    private static Task<IResult> StreamAsync(ProxyOrchestrator orchestrator, ChatRequest request, string? sessionId,
        HttpContext httpContext, CancellationToken ct)
    {
        var translator = new GeminiTranslators.GeminiStreamTranslator(request.Model);
        return ProtocolStreamPipeline.RunAsync(
            orchestrator,
            request,
            sessionId,
            // 空流也输出带 finishReason 的终结块，客户端才能正常收尾
            () => string.Concat(new GeminiTranslators.GeminiStreamTranslator(request.Model).OnData("[DONE]")),
            data => translator.OnData(data),
            (ex, beforeFirstEvent) => FailureFrame(ex, httpContext, beforeFirstEvent),
            ct);
    }

    private static string FailureFrame(Exception ex, HttpContext httpContext, bool beforeFirstEvent)
    {
        switch (ex)
        {
            case BudgetExhaustedException:
                return ErrorFrame(StatusCodes.Status429TooManyRequests, "RESOURCE_EXHAUSTED", "budget exhausted");
            case AllCandidatesFailedException:
                return ErrorFrame(StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", "all model candidates failed");
            case OptiRouter.Compliance.ComplianceViolationException compliance:
                return ErrorFrame(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", compliance.Message);
            // 首帧前被上游拒绝：客户端还没收到任何内容，给固定可重试文案
            case ModelClientException when beforeFirstEvent:
                return ErrorFrame(StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", "upstream request rejected before first chunk");
            // 上游故障（断流 502 检测/流内 error/连接失败）外发真实原因（可重试信号）
            case var e when ProtocolStreamPipeline.IsUpstreamStreamFailure(e):
                return ErrorFrame(StatusCodes.Status503ServiceUnavailable, "UNAVAILABLE", e.Message);
            // 只有首帧前的取消按超时处理；流内取消落 default（与重构前口径一致）
            case OperationCanceledException canceled when beforeFirstEvent:
                return ErrorFrame(StatusCodes.Status504GatewayTimeout, "DEADLINE_EXCEEDED", canceled.Message);
            default:
                // 其余未预见异常不外发 ex.Message，细节进服务端日志。
                ProtocolErrorHelper.LogUnhandledProtocolError(httpContext, ex, "gemini.generateContent");
                return ErrorFrame(StatusCodes.Status500InternalServerError, "INTERNAL", ProtocolErrorHelper.InternalErrorMessage);
        }
    }

    private static string ErrorFrame(int code, string status, string message)
        => GeminiTranslators.GeminiStreamTranslator.OnError(code, status, message);

    private static IResult GeminiError(int code, string status, string message)
        => Results.Json(
            new { error = new { code, message, status } },
            statusCode: code);
}

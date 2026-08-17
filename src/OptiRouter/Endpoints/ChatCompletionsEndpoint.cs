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
        app.MapPost("/v1/chat/completions", async (ChatRequest request, HttpContext httpContext, IOptionsMonitor<OptiRouter.Configuration.RouterOptions> optionsMonitor, ProxyOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (TryGetValidationError(request, out var validationError))
            {
                return Results.Problem(
                    title: "Invalid request",
                    detail: validationError,
                    statusCode: StatusCodes.Status400BadRequest);
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
                        try
                        {
                            await WriteLineAsync(stream, firstLine, ct).ConfigureAwait(false);
                            while (await streamEnumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                await WriteLineAsync(stream, streamEnumerator.Current, ct).ConfigureAwait(false);
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
                        catch (Exception ex)
                        {
                            // 中途失败 code 按异常类型细分，客户端据此判定是否值得重试：
                            // - OperationCanceledException（非外部 ct 取消）：HttpClient 内部超时 → TIMEOUT（可重试）
                            // - HttpRequestException / IOException：上游断连/IO 错误 → UPSTREAM_ERROR（可重试）
                            // - InvalidOperationException：size limit（MaxResponseStreamBytes/MaxStreamLineBytes）→ RESPONSE_TOO_LARGE（不可重试）
                            // - 其余：INTERNAL_ERROR（不可重试）
                            string code = ClassifyMidStreamError(ex);
                            await WriteErrorAsync(stream, ex.Message, code, ct).ConfigureAwait(false);
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
                httpContext.Response.Headers["Retry-After"] = "3600";

                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Budget exhausted",
                    Detail = ex.Message,
                    Status = StatusCodes.Status429TooManyRequests
                };
                problem.Extensions["code"] = "BUDGET_EXHAUSTED";
                if (requestId != null) problem.Extensions["requestId"] = requestId;
                problem.Extensions["retryAfterSeconds"] = 3600;

                return Results.Json(problem, statusCode: StatusCodes.Status429TooManyRequests, contentType: "application/problem+json");
            }
            catch (OptiRouter.Compliance.ComplianceViolationException ex)
            {
                // 内容审核拦截（输入违规拒绝 / 输出违规中断）：客户端可据此调整输入或终止重试。
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Content moderated",
                    Detail = ex.Message,
                    Status = StatusCodes.Status400BadRequest
                };
                problem.Extensions["code"] = "CONTENT_MODERATED";
                if (ex.MatchedKeyword != null) problem.Extensions["category"] = ex.MatchedKeyword;
                if (requestId != null) problem.Extensions["requestId"] = requestId;

                return Results.Json(problem, statusCode: StatusCodes.Status400BadRequest, contentType: "application/problem+json");
            }
            catch (AllCandidatesFailedException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "All model candidates failed",
                    Detail = $"Attempted: {string.Join(", ", ex.AttemptedModels)}. Last failure: Model '{ex.LastModelName}' returned status {ex.LastStatusCode}.",
                    Status = StatusCodes.Status503ServiceUnavailable
                };
                problem.Extensions["code"] = "ALL_CANDIDATES_FAILED";
                if (requestId != null) problem.Extensions["requestId"] = requestId;
                problem.Extensions["attemptedModels"] = ex.AttemptedModels;
                if (ex.LastModelName != null)
                {
                    problem.Extensions["lastError"] = new Dictionary<string, object?>
                    {
                        ["model"] = ex.LastModelName,
                        ["statusCode"] = ex.LastStatusCode,
                        ["message"] = ex.LastErrorMessage
                    };
                }

                return Results.Json(problem, statusCode: StatusCodes.Status503ServiceUnavailable, contentType: "application/problem+json");
            }
            catch (ModelClientException ex)
            {
                string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
                var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Upstream request rejected",
                    Status = (int)ex.StatusCode
                };
                problem.Extensions["code"] = "UPSTREAM_REJECTION";
                if (requestId != null) problem.Extensions["requestId"] = requestId;
                problem.Extensions["statusCode"] = (int)ex.StatusCode;

                return Results.Json(problem, statusCode: (int)ex.StatusCode, contentType: "application/problem+json");
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
            return Results.Json(
                new
                {
                    error = new
                    {
                        message = $"The model '{request.Model}' does not exist or is not enabled. Use 'auto' for smart routing, or GET /v1/models for available model ids.",
                        type = "invalid_request_error",
                        code = "model_not_found"
                    }
                },
                statusCode: StatusCodes.Status404NotFound);
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
        else if (request.Messages.Any(message => message.Content is null || message.Content.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null))
            error = "Message content must not be null.";
        else if (request.Temperature is < 0 or > 2)
            error = "Temperature must be between 0 and 2.";
        else if (request.MaxTokens is <= 0)
            error = "MaxTokens must be greater than zero.";
        else
            error = string.Empty;

        return error.Length > 0;
    }

    private static IResult CreateUpstreamRejection(ModelClientException exception, HttpContext httpContext)
    {
        string? requestId = httpContext.Items.TryGetValue("RequestId", out var rid) ? rid?.ToString() : null;
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Title = "Upstream request rejected",
            Status = (int)exception.StatusCode
        };
        problem.Extensions["code"] = "UPSTREAM_REJECTION";
        if (requestId != null) problem.Extensions["requestId"] = requestId;
        problem.Extensions["statusCode"] = (int)exception.StatusCode;

        return Results.Json(problem, statusCode: (int)exception.StatusCode, contentType: "application/problem+json");
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

    private static async Task WriteErrorAsync(Stream stream, string message, string code, CancellationToken ct)
    {
        // OpenAI 兼容嵌套 error 结构：{"error":{"message":...,"type":...,"code":...}}
        // type 映射自 code，客户端按 OpenAI SDK 的 error.type 字段机读错误类别。
        // 旧实现 {"error":"<string>"} 为裸串，非 OpenAI 规范，OpenAI SDK 解析失败——故改为嵌套对象。
        string type = code switch
        {
            "BUDGET_EXHAUSTED" => "budget_exceeded",
            "ALL_CANDIDATES_FAILED" => "all_candidates_failed",
            "INTERNAL_ERROR" => "server_error",
            "UPSTREAM_ERROR" => "upstream_error",
            "TIMEOUT" => "timeout",
            "RESPONSE_TOO_LARGE" => "response_too_large",
            _ => "server_error"
        };
        var errorPayload = new { error = new { message, type, code } };
        var json = JsonSerializer.Serialize(errorPayload);
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
        if (ex is HttpRequestException or IOException)
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

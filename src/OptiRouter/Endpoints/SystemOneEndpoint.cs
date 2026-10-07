using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OptiRouter.Clients;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// 云知声 SystemOne 决策协议原生透传端点（POST /v1/systemone）。
/// <para>
/// SystemOne（u2-decision）不是 chat 协议：请求为 state + questions（choice/noul/score），
/// 响应为带概率分布的 answers，没有流式、没有 token usage。因此不走 chat 翻译管线，
/// 而是原样透传：客户端用租户 ClientKey 调网关，网关鉴权（/v1 前缀中间件自动生效）后
/// 把 Authorization 换成该协议端点配置的上游 ApiKey 直连上游，请求/响应体保真转发。
/// </para>
/// <para>
/// 模型寻址：请求体 model 字段按 ModelDisplayIds 规则（路由名/显示 ID/裸上游 Id）解析到
/// Protocol=SystemOne 的端点；缺省时仅一条 SystemOne 端点则自动命中。发往上游前强制覆写
/// 为端点的 UpstreamModelId。计费：上游无 usage 回传，按请求体 token 粗估 × 输入单价入账。
/// </para>
/// </summary>
public static class SystemOneEndpoint
{
    /// <summary>上游转发用命名 HttpClient；测试经 IHttpClientFactory 注入 stub handler。</summary>
    public const string UpstreamHttpClientName = "systemone-upstream";

    /// <summary>
    /// 透传 body 序列化选项：UnsafeRelaxedJsonEscaping 保持非 ASCII（中文 state）字节保真，
    /// 避免 \uXXXX 转义把大 state 体积放大三倍；仅用于 application/json 上行，无 HTML 注入面。
    /// </summary>
    private static readonly JsonSerializerOptions ForwardJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 将 /v1/systemone 端点映射到路由图。
    /// </summary>
    public static IEndpointRouteBuilder MapSystemOne(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/systemone", async (HttpContext httpContext,
            IOptionsMonitor<RouterOptions> optionsMonitor,
            IHttpClientFactory httpClientFactory,
            OutcomeRecorder recorder,
            CancellationToken ct) =>
        {
            var stopwatch = Stopwatch.StartNew();

            // 手动解析 JSON：校验必填字段 + 覆写 model 后需要重组 body，
            // SystemOne 请求体是紧凑结构化 JSON（state + questions），无透传保真诉求。
            string rawBody;
            try
            {
                using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
                rawBody = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }

            JsonNode? root;
            try
            {
                root = string.IsNullOrWhiteSpace(rawBody) ? null : JsonNode.Parse(rawBody);
            }
            catch (JsonException ex)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    $"Invalid JSON body: {ex.Message}",
                    "invalid_request_error");
            }

            if (root is not JsonObject body)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    "Request body must be a JSON object with required fields: state, questions.",
                    "invalid_request_error");
            }

            // 协议必填项校验（上游同样拒绝，但提前到网关避免无效上游调用与计费）。
            if (body["state"] is null)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    "Field 'state' is required.",
                    "invalid_request_error");
            }
            if (body["questions"] is not JsonObject questions || questions.Count == 0)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    "Field 'questions' must be an object with at least one question.",
                    "invalid_request_error");
            }

            // 模型寻址：SystemOne 端点池内按请求 model 解析（路由名/显示 ID/裸上游 Id）。
            // ToString() 而非 GetValue<string>()：非字符串 model 不抛异常，走 404/400 语义。
            string? requestedModel = body["model"]?.ToString();
            var systemOneModels = optionsMonitor.CurrentValue.Models
                .Where(m => m.Enabled && m.Protocol == ProviderProtocol.SystemOne)
                .ToList();
            ModelEndpointOptions? endpoint;
            if (!string.IsNullOrWhiteSpace(requestedModel))
            {
                endpoint = ModelDisplayIds.Resolve(systemOneModels, requestedModel).FirstOrDefault();
                if (endpoint is null)
                {
                    return ProtocolErrorHelper.CreateOpenAiResult(
                        StatusCodes.Status404NotFound,
                        $"The model '{requestedModel}' does not exist or is not enabled. " +
                        "Configure a SystemOne protocol endpoint in the models admin page.",
                        "model_not_found");
                }
            }
            else if (systemOneModels.Count == 1)
            {
                endpoint = systemOneModels[0];
            }
            else
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status400BadRequest,
                    "Field 'model' is required when multiple SystemOne endpoints are configured.",
                    "invalid_request_error");
            }

            // 强制覆写为上游真实模型 id，与 chat 管线的模型名改写语义一致。
            body["model"] = endpoint.UpstreamModelId;
            string forwardedBody = body.ToJsonString(ForwardJsonOptions);

            var httpClient = httpClientFactory.CreateClient(UpstreamHttpClientName);
            // 总时长约束由下方 CTS 按端点 TimeoutSeconds 施加；工厂客户端默认 100s 硬顶会截断更慢的决策请求。
            httpClient.Timeout = Timeout.InfiniteTimeSpan;

            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, SystemOneModelClient.ResolveApiPath(endpoint.BaseUrl, "systemone"))
            {
                Content = new StringContent(forwardedBody, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
            {
                upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, endpoint.TimeoutSeconds)));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            HttpResponseMessage upstreamResponse;
            try
            {
                upstreamResponse = await httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty;
            }
            catch (OperationCanceledException)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status504GatewayTimeout,
                    $"Upstream request timed out after {endpoint.TimeoutSeconds}s.",
                    "UPSTREAM_TIMEOUT");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                return ProtocolErrorHelper.CreateOpenAiResult(
                    StatusCodes.Status502BadGateway,
                    $"Upstream unreachable: {ex.Message}",
                    "UPSTREAM_ERROR");
            }

            string responseBody;
            try
            {
                responseBody = await upstreamResponse.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                upstreamResponse.Dispose();
                return Results.Empty;
            }

            stopwatch.Stop();
            int statusCode = (int)upstreamResponse.StatusCode;
            bool success = statusCode is >= 200 and < 300;
            string contentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
            upstreamResponse.Dispose();

            // 计费：SystemOne 响应无 token usage，按请求体 token 粗估 × 输入单价入账（按次语义的天花板：
            // 上游实际按 state+questions 计费，粗估系数与 chat 分桶一致，误差可接受）。
            string? sessionId = ChatCompletionsEndpoint.ResolveSessionId(httpContext);
            int estimatedTokens = TokenEstimator.EstimateText(forwardedBody);
            decimal cost = estimatedTokens * endpoint.InputPricePerMillion / 1_000_000m;
            recorder.RecordCost(cost, sessionId);
            recorder.RecordAudit(
                null, endpoint.Name, estimatedTokens, usage: null, cost, stopwatch.ElapsedMilliseconds, sessionId,
                routingReason: "systemone passthrough",
                success: success,
                errorMessage: success ? null : $"upstream status {statusCode}",
                isStreaming: false,
                routedTier: endpoint.Tier,
                isEstimated: true,
                upstreamStatusCode: statusCode);

            return Results.Content(responseBody, contentType, Encoding.UTF8, statusCode);
        });

        return app;
    }
}

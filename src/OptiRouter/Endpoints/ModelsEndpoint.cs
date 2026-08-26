using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using OptiRouter.Configuration;
using OptiRouter.Routing;

namespace OptiRouter.Endpoints;

/// <summary>
/// OpenAI 兼容的 <c>GET /v1/models</c> 端点，列出当前启用的模型，供客户端发现可用模型。
/// </summary>
/// <remarks>
/// 受 /v1/* 鉴权与限流中间件保护。仅返回 <c>Enabled=true</c> 的模型，脱敏（不含 ApiKey/BaseUrl）。
/// 首位固定暴露虚拟模型 <c>auto</c>（智能路由别名），其后为三模式预设
/// <c>auto:cost / auto:balanced / auto:intel</c>（同为智能路由，档位偏好不同）；
/// 真实模型的 <c>id</c> 统一为「{供应商}/{上游真实模型 Id}」格式（如 "deepseek/deepseek-chat"，
/// 同供应商同模型多 Key 追加 #2），请求该 id 时自动解析并转换为上游内部模型 ID 发送。
/// </remarks>
public static class ModelsEndpoint
{
    /// <summary>
    /// 将 /v1/models 端点映射到路由图。
    /// </summary>
    public static IEndpointRouteBuilder MapModelsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (IOptionsMonitor<OptiRouter.Configuration.RouterOptions> options) =>
        {
            // IOptionsMonitor 而非 IOptions：models-config.json 热重载后列表即时反映新端点。
            var enabled = options.CurrentValue.Models.Where(m => m.Enabled).ToList();

            // 显示 ID 统一为 "{供应商}/{真实模型 Id}"；同基础 ID 重复时追加 " #2"、" #3"。
            var displayIds = ModelDisplayIds.Compute(enabled);

            // auto 的上下文窗口取启用模型的最大值：路由长输入过滤会从中挑装得下的候选，
            // 用最大值可避免客户端按过小窗口提前截断。空列表时为 0（无候选可承接）。
            int autoContextTokens = enabled.Count == 0 ? 0 : enabled.Max(m => m.MaxContextTokens);

            var data = new List<object>
            {
                // 虚拟智能路由模型：请求 model="auto" 或缺省 model 时由 RouterEngine 全链路选择。
                new
                {
                    id = ExplicitModelPolicy.AutoAlias,
                    @object = "model",
                    created = 0,
                    owned_by = "opti-router",
                    routing = "auto",
                    description = "Smart routing: OptiRouter selects the best enabled model per request.",
                    candidates = enabled.Count,
                    context_length = autoContextTokens,
                    max_model_len = autoContextTokens,
                    max_context_tokens = autoContextTokens
                }
            };

            // 三模式预设虚拟模型：同为智能路由（RoutingModePolicy 解析预设并过滤到目标档），
            // 供 agent 端直接配置。context 取目标档启用模型的最大窗口——agent 按它配置压缩
            // 阈值，若按全量最大值配置而实际只能路由到小窗口档位会提前截断失败；
            // 目标档无模型时回落全量最大（与该模式的路由兜底行为一致）。
            var modePresets = new (string Id, string Description, ModelTier Tier)[]
            {
                ("auto:cost", "Cost-first smart routing: prefer cheap-tier models, aggressive prompt compression.", ModelTier.Cheap),
                ("auto:balanced", "Balanced smart routing: cost/quality trade-off on medium tier.", ModelTier.Medium),
                ("auto:intel", "Quality-first smart routing: prefer strong-tier models, conservative compression.", ModelTier.Strong)
            };
            foreach (var preset in modePresets)
            {
                int tierContext = enabled.Where(m => m.Tier == preset.Tier)
                    .Select(m => (int?)m.MaxContextTokens).Max() ?? autoContextTokens;
                data.Add(new
                {
                    id = preset.Id,
                    @object = "model",
                    created = 0,
                    owned_by = "opti-router",
                    routing = "auto",
                    description = preset.Description,
                    candidates = enabled.Count(m => m.Tier == preset.Tier),
                    context_length = tierContext,
                    max_model_len = tierContext,
                    max_context_tokens = tierContext
                });
            }

            for (int i = 0; i < enabled.Count; i++)
            {
                var m = enabled[i];
                // 供应商：显式 Provider 配置优先，缺省从 BaseUrl 推断；同供应商多端点加序号区分。
                string provider = ModelDisplayIds.EffectiveProvider(m);

                // created 用固定纪元 0（无持久化创建时间）；owned_by 标识由本路由代理。
                data.Add(new
                {
                    id = displayIds[i],
                    @object = "model",
                    created = 0,
                    owned_by = "opti-router",
                    // 指定该 id（或裸 upstream_id / 路由名 name）时固定直连；指定 auto 才走智能路由。
                    routing = "direct",
                    // 配置里的路由名（Dashboard/审计使用的内部标识）。
                    name = m.Name,
                    // 上游真实模型 id（发往供应商 API 的 model 值）。
                    upstream_id = m.UpstreamModelId,
                    // 扩展字段：帮助客户端按能力/上下文选择。OpenAI 客户端会忽略未知字段。
                    tier = m.Tier.ToString().ToLowerInvariant(),
                    provider,
                    family = m.Family,
                    // 上下文窗口按三大生态的字段名冗余暴露，第三方 agent（Cherry Studio/LobeChat
                    // 读 context_length，vLLM 系读 max_model_len 等）不再回退默认值（如 256K）。
                    context_length = m.MaxContextTokens,
                    max_model_len = m.MaxContextTokens,
                    max_context_tokens = m.MaxContextTokens,
                    tags = m.Tags
                });
            }

            return Results.Json(new { @object = "list", data });
        });

        return app;
    }
}

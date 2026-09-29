using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>单条配置组合诊断。Severity: "warning"（组合静默失效/成本陷阱）/ "info"（使用注意）。</summary>
public sealed record ConfigDiagnostic(string Severity, string Code, string Message);

/// <summary>
/// 路由配置组合诊断：RouterOptionsValidator 管「单值合法性」，这里管「组合语义」——
/// 开关互斥、依赖缺失、静默失效与成本陷阱。只提示不阻断（保存照常生效）。
/// 每条规则都对应可验证的代码行为，注释给出证据位置；保存配置时随响应返回，Dashboard 展示。
/// </summary>
public static class RoutingConfigDiagnostics
{
    public static IReadOnlyList<ConfigDiagnostic> Analyze(RouterOptions options)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var routing = options.Routing;

        // 拜占庭共识只在融合路由的非流式路径生效（README quality-first 预设注记 + ProxyOrchestrator 融合分支）。
        if (routing.EnableByzantineConsensus && !routing.EnableFusionRouter)
        {
            diagnostics.Add(new("warning", "byzantine-without-fusion",
                "EnableByzantineConsensus 已开启但 EnableFusionRouter 未开启：拜占庭共识只在融合路由的非流式路径生效，当前配置下不会起任何作用。"));
        }

        // 融合与竞速同开：融合优先，竞速仅在其失败后兜底（SendAsync 先 fusionRouterAttempted 后 fusionModeAttempted）。
        if (routing.EnableFusionRouter && routing.EnableFusionMode)
        {
            diagnostics.Add(new("info", "fusion-and-race",
                "EnableFusionRouter 与 EnableFusionMode 同开：融合路由优先执行，竞速（Fusion-lite）只在融合失败后兜底——请确认愿意为非流式请求承担两条并行路径的成本。"));
        }

        // 质量评审静默失效：TryJudge 在未配置打分模型时直接 return（LlmQualityJudge.TryJudge）。
        if (routing.EnableQualityJudge && string.IsNullOrWhiteSpace(routing.QualityJudgeModel))
        {
            diagnostics.Add(new("warning", "quality-judge-no-model",
                "EnableQualityJudge 已开启但 QualityJudgeModel 未配置：质量采样会静默跳过，学习回路收不到语义质量信号。"));
        }

        if (routing.EnableQualityJudge && !string.IsNullOrWhiteSpace(routing.QualityJudgeModel))
        {
            var judgeModel = ModelDisplayIds.Resolve(EnabledModels(options), routing.QualityJudgeModel);
            if (judgeModel.Count == 0)
            {
                diagnostics.Add(new("warning", "quality-judge-model-unresolved",
                    $"QualityJudgeModel '{routing.QualityJudgeModel}' 无法解析到任何启用模型：质量采样会静默跳过。"));
            }
            else if (routing.EnableDataSovereignty && !DataSovereigntyPolicy.IsLocalOrPrivateCandidate(judgeModel[0]))
            {
                diagnostics.Add(new("warning", "quality-judge-sovereignty",
                    "数据主权过滤已开启且打分模型不是本地/私有节点：judge 采样会被主权门控跳过（LlmQualityJudge.TryJudge）。"));
            }
        }

        // 首行竞速需要候选链：hedgeSuccessor 只在 EnableFailover 时寻找（StreamAsync 门控）。
        if (routing.StreamHedgeDelayMs > 0 && !routing.EnableFailover)
        {
            diagnostics.Add(new("warning", "hedge-without-failover",
                "StreamHedgeDelayMs > 0 但 EnableFailover 未开启：流式首行竞速依赖候选链寻找继任者，当前配置下不会生效。"));
        }

        // 数据主权过滤云端候选后，融合/竞速的门控（候选数 ≥ 2）可能凑不齐（SendAsync/FusionRouter 门控）。
        if (routing.EnableDataSovereignty && (routing.EnableFusionRouter || routing.EnableFusionMode))
        {
            int privateCount = 0;
            var models = options.Models;
            for (int i = 0; i < models.Count; i++)
            {
                if (models[i].Enabled && models[i].IsLocalOrPrivate) privateCount++;
            }
            if (privateCount < 2)
            {
                diagnostics.Add(new("warning", "sovereignty-starves-parallel",
                    $"数据主权过滤开启且本地/私有启用端点仅 {privateCount} 个：融合/竞速需要至少 2 个候选，多数请求将退回串行路径。"));
            }
        }

        // 级联校验模型解析不到时回退自评（CascadeUpgradeHandler），自利偏差回归。
        if (routing.EnableCascadeUpgrade && !string.IsNullOrWhiteSpace(routing.CascadeUpgradeVerifierModel)
            && ModelDisplayIds.Resolve(EnabledModels(options), routing.CascadeUpgradeVerifierModel).Count == 0)
        {
            diagnostics.Add(new("warning", "cascade-verifier-unresolved",
                $"CascadeUpgradeVerifierModel '{routing.CascadeUpgradeVerifierModel}' 无法解析到任何启用模型：级联校验将回退为模型自评（自利偏差）。"));
        }

        // regenerate 负反馈对固定 prompt 定时任务误判（README 明示的使用注意）。
        if (routing.EnableRegenerateFeedback)
        {
            diagnostics.Add(new("info", "regenerate-fixed-prompt",
                "EnableRegenerateFeedback 已开启：固定 prompt 的定时/轮询任务会被误判为「用户不满意」并惩罚上次模型，此类场景建议关闭该开关。"));
        }

        // ε 探索是延迟感知重排的一部分（LatencyAwarePolicy.MaybePromoteTailForExploration 消费）。
        if (routing.ExplorationEpsilon > 0 && !routing.EnableLatencyAware)
        {
            diagnostics.Add(new("info", "epsilon-without-latency-aware",
                "ExplorationEpsilon > 0 但 EnableLatencyAware 未开启：ε 探索在延迟感知重排内执行，当前配置下不会生效。"));
        }

        return diagnostics;
    }

    private static List<ModelEndpointOptions> EnabledModels(RouterOptions options)
    {
        var enabled = new List<ModelEndpointOptions>(options.Models.Count);
        for (int i = 0; i < options.Models.Count; i++)
        {
            if (options.Models[i].Enabled) enabled.Add(options.Models[i]);
        }
        return enabled;
    }
}

using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 推理计算预算与 Reasoning Effort 配置规则。
/// </summary>
public sealed record ReasoningBudgetResult(
    string ReasoningEffort,
    int RecommendedMaxTokens,
    double ComplexityScore,
    string Reason);

/// <summary>
/// 思考链与推理 Token 计算预算调节器 (Reasoning Token Compute-Budget Controller)。
/// 针对 DeepSeek-R1、OpenAI o1/o3-mini 等具备思考推理链（Chain-of-Thought / Reasoning Effort）的大模型，
/// 根据 Prompt 复杂度与分类信号动态匹配 `reasoning_effort` (low/medium/high) 与 `max_completion_tokens`，
/// 避免简单问题无谓消耗数千思考 Token，同时保证高难度数学/代码/架构任务获得充分思考深度。
/// </summary>
public sealed class ReasoningEffortController
{
    /// <summary>
    /// 分析 ChatRequest 的思考复杂度 [0.0, 1.0]。
    /// </summary>
    public static double EstimatePromptComplexity(ChatRequest request)
    {
        if (request?.Messages == null || request.Messages.Count == 0)
            return 0.5;

        string fullText = string.Join("\n", request.Messages.Select(m => m.GetText()));
        if (string.IsNullOrWhiteSpace(fullText)) return 0.2;

        double score = 0.3; // 默认基础分

        // 1. 文本长度信号
        if (fullText.Length > 2000) score += 0.2;
        if (fullText.Length > 5000) score += 0.2;

        // 2. 高思考密度关键词信号 (数学/算法/复杂逻辑/推理证明)
        string lower = fullText.ToLowerInvariant();
        string[] highComplexityKeywords = { "proof", "prove", "algorithm", "complexity", "theorem", "math", "calculus", "refactor", "architecture", "debug", "leetcode", "优化", "证明", "算法", "推导", "复杂度", "架构" };
        int matchCount = 0;
        foreach (var kw in highComplexityKeywords)
        {
            if (lower.Contains(kw)) matchCount++;
        }

        score += Math.Min(0.4, matchCount * 0.1);

        // 3. 低思考密度简单问答关键词信号
        string[] lowComplexityKeywords = { "hi", "hello", "who are you", "capital of", "translate", "summarize", "你好", "翻译", "总结", "你是谁" };
        foreach (var kw in lowComplexityKeywords)
        {
            if (lower.Contains(kw) && fullText.Length < 100)
            {
                score -= 0.2;
                break;
            }
        }

        return Math.Clamp(score, 0.1, 1.0);
    }

    /// <summary>
    /// 根据请求与路由配置计算最优 Reasoning 预算。
    /// </summary>
    public ReasoningBudgetResult CalculateBudget(ChatRequest request, RouterOptions options)
    {
        double complexity = EstimatePromptComplexity(request);
        var cfg = options?.Routing;

        string effort;
        int maxTokens;
        string reason;

        if (complexity < 0.35)
        {
            effort = "low";
            maxTokens = cfg?.ReasoningLowMaxTokens ?? 1024;
            reason = $"simple query (complexity={complexity:F2}) -> low reasoning effort";
        }
        else if (complexity < 0.70)
        {
            effort = "medium";
            maxTokens = cfg?.ReasoningMediumMaxTokens ?? 4096;
            reason = $"standard query (complexity={complexity:F2}) -> medium reasoning effort";
        }
        else
        {
            effort = "high";
            maxTokens = cfg?.ReasoningHighMaxTokens ?? 16384;
            reason = $"complex task (complexity={complexity:F2}) -> high reasoning effort";
        }

        return new ReasoningBudgetResult(effort, maxTokens, complexity, reason);
    }
}

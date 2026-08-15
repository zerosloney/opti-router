using OptiRouter.Clients;
using OptiRouter.Configuration;
using System.Text.Json;

namespace OptiRouter.Routing;

/// <summary>
/// 融合四策略的路由引擎。
/// </summary>
public sealed class RouterEngine
{
    private readonly CostLedger _ledger;
    private readonly IReadOnlyList<IRouterPolicy> _policies;
    private readonly ITokenEstimator _tokenEstimator;

    /// <summary>
    /// 构造路由引擎。
    /// </summary>
    /// <param name="ledger">成本账本。</param>
    /// <param name="policies">策略链，按顺序应用。</param>
    /// <param name="tokenEstimator">token 估算器；不传则用分桶粗估（保持既有测试行为）。</param>
    public RouterEngine(CostLedger ledger, IEnumerable<IRouterPolicy> policies, ITokenEstimator? tokenEstimator = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _policies = policies?.ToList() ?? throw new ArgumentNullException(nameof(policies));
        _tokenEstimator = tokenEstimator ?? new BucketTokenEstimator();
    }

    /// <summary>
    /// 决策：给定请求和配置，返回候选模型链。
    /// </summary>
    public RouterDecision Decide(ChatRequest request, RouterOptions options, IReadOnlySet<string>? failedModels = null, string? sessionId = null)
    {
        // 1. 估算 token
        int estTokens = _tokenEstimator.Estimate(request);

        // 2. 构造初始 context
        var context = new RouterContext
        {
            Request = request,
            AllModels = options.Models.Where(m => m.Enabled).ToList(),
            Options = options,
            EstimatedInputTokens = estTokens,
            FailedModels = failedModels ?? new HashSet<string>(),
            SessionId = sessionId
        };

        // Keep a monotonic eligibility pool for the complete policy chain. Filter
        // policies narrow this pool; later policies can only select from it. This
        // prevents fallback/degrade policies from rebuilding candidates from the
        // original enabled-model list and undoing an earlier hard filter.
        var eligibleModels = context.AllModels.ToList();

        // 3. 初始决策：所有 enabled 模型按 tier 升序（Strong 优先）作为候选
        var initialCandidates = eligibleModels
            .OrderBy(m => TierOrder.Rank(m.Tier))
            .ThenByDescending(m => m.MaxContextTokens)
            .ToList();

        // 计算新特征
        double cjkRatio = CalculateCjkRatio(request);
        int maxTokens = request.MaxTokens ?? 0;
        bool hasTools = HasTools(request);

        var decision = new RouterDecision
        {
            Candidates = initialCandidates,
            Reason = $"initial: {initialCandidates.Count} candidates, est {estTokens} tokens",
            EstimatedInputTokens = estTokens,
            RequestIsStreaming = request.Stream,
            RequestMessageCount = request.Messages?.Count ?? 0,
            CjkRatio = cjkRatio,
            MaxTokens = maxTokens,
            HasTools = hasTools
        };

        // 4. 按分组依赖序应用策略（Filter→Classify→Order→Constraint），组内保留串行。
        //    分组契约（PolicyGroup）是未来并行化的地基；当前组内串行以保留叠加过滤/fallback/重排语义。
        var groups = new[] { PolicyGroup.Filter, PolicyGroup.Classify, PolicyGroup.Order, PolicyGroup.Constraint };
        foreach (var group in groups)
        {
            foreach (var policy in _policies.Where(p => p.Group == group))
            {
                // All policies see the current eligible pool. A filter policy may
                // only narrow it; every result is intersected defensively so a
                // policy that rebuilds from its own source cannot reintroduce a
                // model removed by an earlier filter.
                var policyContext = context with { AllModels = eligibleModels };
                var policyDecision = policy.Apply(policyContext, decision);
                var candidates = IntersectWithEligible(policyDecision.Candidates, eligibleModels);

                if (group == PolicyGroup.Filter)
                {
                    eligibleModels = candidates;
                }

                decision = policyDecision with { Candidates = candidates };
            }
        }

        return decision;
    }

    private static List<ModelEndpointOptions> IntersectWithEligible(
        IReadOnlyList<ModelEndpointOptions> candidates,
        IReadOnlyList<ModelEndpointOptions> eligibleModels)
    {
        var eligibleNames = eligibleModels
            .Select(model => model.Name)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(model => eligibleNames.Contains(model.Name))
            .ToList();
    }

    /// <summary>
    /// 计算请求文本的 CJK 字符占比。
    /// </summary>
    private static double CalculateCjkRatio(ChatRequest request)
    {
        int totalNonWhitespace = 0;
        int cjkCount = 0;

        foreach (var msg in request.Messages ?? [])
        {
            string text = msg.GetText();
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c))
                {
                    totalNonWhitespace++;
                    // CJK 范围：U+4E00-U+9FFF（CJK 统一表意文字）、U+3400-U+4DBF（CJK 扩展A）、
                    // U+20000-U+2A6DF（CJK 扩展B）、U+2A700-U+2B73F（CJK 扩展C）、U+2B740-U+2B81F（CJK 扩展D）、
                    // U+2B820-U+2CEAF（CJK 扩展E）、U+2CEB0-U+2EBEF（CJK 扩展F）、
                    // U+3000-U+303F（CJK 符号和标点）、U+FF00-U+FFEF（半角及全角形式）
                    if (IsCjkCharacter(c))
                        cjkCount++;
                }
            }
        }

        return totalNonWhitespace > 0 ? (double)cjkCount / totalNonWhitespace : 0.0;
    }

    /// <summary>
    /// 判断字符是否为 CJK 字符。
    /// </summary>
    private static bool IsCjkCharacter(char c)
    {
        // CJK 统一表意文字
        if (c >= 0x4E00 && c <= 0x9FFF) return true;
        // CJK 扩展 A
        if (c >= 0x3400 && c <= 0x4DBF) return true;
        // CJK 符号和标点
        if (c >= 0x3000 && c <= 0x303F) return true;
        // 半角及全角形式
        if (c >= 0xFF00 && c <= 0xFFEF) return true;
        // 谚文（可选，根据需求是否包含韩文）
        // if (c >= 0xAC00 && c <= 0xD7AF) return true;
        // 日文假名（可选）
        // if (c >= 0x3040 && c <= 0x309F) return true; // 平假名
        // if (c >= 0x30A0 && c <= 0x30FF) return true; // 片假名

        return false;
    }

    /// <summary>
    /// 检测请求是否携带工具调用。
    /// </summary>
    private static bool HasTools(ChatRequest request)
    {
        if (request.ExtensionData is null) return false;

        // 检查是否存在 "tools" 键且值为非空数组
        if (request.ExtensionData.TryGetValue("tools", out var toolsElement))
        {
            if (toolsElement.ValueKind == JsonValueKind.Array)
            {
                return toolsElement.GetArrayLength() > 0;
            }
        }

        return false;
    }
}

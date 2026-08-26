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
    private readonly IReadOnlyList<IRouterPolicy>[] _groupedPolicies;
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

        // 预先按 PolicyGroup 依赖顺序分组分区（Filter -> Classify -> Order -> Constraint），
        // 消除每次请求 Decide() 时的 LINQ Where 分配与数组创建
        var groups = new[] { PolicyGroup.Filter, PolicyGroup.Classify, PolicyGroup.Order, PolicyGroup.Constraint };
        _groupedPolicies = new IReadOnlyList<IRouterPolicy>[groups.Length];
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            _groupedPolicies[i] = _policies.Where(p => p.Group == group).ToArray();
        }
    }

    /// <summary>
    /// 决策：给定请求和配置，返回候选模型链。
    /// </summary>
    public RouterDecision Decide(ChatRequest request, RouterOptions options, IReadOnlySet<string>? failedModels = null, string? sessionId = null)
    {
        // 1. 估算 token
        int estTokens = _tokenEstimator.Estimate(request);

        // 2. 构造初始 context
        var allModels = new List<ModelEndpointOptions>(options.Models.Count);
        for (int i = 0; i < options.Models.Count; i++)
        {
            var m = options.Models[i];
            if (m.Enabled) allModels.Add(m);
        }

        var context = new RouterContext
        {
            Request = request,
            AllModels = allModels,
            Options = options,
            EstimatedInputTokens = estTokens,
            FailedModels = failedModels ?? new HashSet<string>(),
            SessionId = sessionId
        };

        // Keep a monotonic eligibility pool for the complete policy chain. Filter
        // policies narrow this pool; later policies can only select from it. This
        // prevents fallback/degrade policies from rebuilding candidates from the
        // original enabled-model list and undoing an earlier hard filter.
        var eligibleModels = new List<ModelEndpointOptions>(allModels);

        // 3. 初始决策：所有 enabled 模型按 tier 升序（Strong 优先）作为候选（原地高效排序）
        var initialCandidates = new List<ModelEndpointOptions>(eligibleModels);
        initialCandidates.Sort(InitialCandidateComparer.Instance);

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

        // 4. 按预编译分组依赖序应用策略（Filter→Classify→Order→Constraint），消除动态分配。
        for (int g = 0; g < _groupedPolicies.Length; g++)
        {
            var groupPolicies = _groupedPolicies[g];
            bool isFilterGroup = g == 0; // PolicyGroup.Filter

            for (int i = 0; i < groupPolicies.Count; i++)
            {
                var policy = groupPolicies[i];
                var policyContext = context with { AllModels = eligibleModels };
                var policyDecision = policy.Apply(policyContext, decision);
                var candidates = IntersectWithEligible(policyDecision.Candidates, eligibleModels);

                if (isFilterGroup)
                {
                    if (candidates.Count == 0 && policyDecision.Candidates.Count > 0)
                    {
                        // 全灭补链逃生门：池内候选已被本策略全部替换为池外降级链
                        // （FailoverPolicy 全灭时从全量配置补链）。此时接受补链结果并同步
                        // 扩展池——否则交集清零 → all model candidates failed（503）。
                        // 这与资格池初衷不冲突：池防的是"有存活候选时降级策略重建大集合
                        // undo 硬过滤"；全灭时无可 undo，活命优先。注意补链未重放
                        // Capability/Sovereignty 硬过滤（两者默认关，启用方须知此窗口）。
                        candidates = policyDecision.Candidates.ToList();
                        eligibleModels = candidates;
                    }
                    else
                    {
                        // 资格池保持集合语义：收缩时按池内原（配置）顺序保留，不能用 candidates
                        // 的顺序——候选链按 tier 排序，泄漏进池会改变 ModelDisplayIds #N 编号的
                        // 基准（配置顺序，与 /v1/models 一致），导致 "provider/id #2" 解析错位
                        // （同 tier 并列端点被不稳定排序互换）。
                        var kept = new HashSet<string>(
                            candidates.Select(c => c.Name), StringComparer.Ordinal);
                        eligibleModels = eligibleModels.Where(m => kept.Contains(m.Name)).ToList();
                    }
                }

                decision = policyDecision with { Candidates = candidates };
            }
        }

        return decision;
    }

    private sealed class InitialCandidateComparer : IComparer<ModelEndpointOptions>
    {
        public static readonly InitialCandidateComparer Instance = new();

        public int Compare(ModelEndpointOptions? x, ModelEndpointOptions? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            int tierCmp = TierOrder.Rank(x.Tier).CompareTo(TierOrder.Rank(y.Tier));
            if (tierCmp != 0) return tierCmp;
            return y.MaxContextTokens.CompareTo(x.MaxContextTokens);
        }
    }

    private static List<ModelEndpointOptions> IntersectWithEligible(
        IReadOnlyList<ModelEndpointOptions> candidates,
        IReadOnlyList<ModelEndpointOptions> eligibleModels)
    {
        if (candidates.Count == 0 || eligibleModels.Count == 0)
            return [];

        var eligibleNames = new HashSet<string>(eligibleModels.Count, StringComparer.Ordinal);
        for (int i = 0; i < eligibleModels.Count; i++)
        {
            eligibleNames.Add(eligibleModels[i].Name);
        }

        var result = new List<ModelEndpointOptions>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (eligibleNames.Contains(candidate.Name))
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// 计算请求文本的 CJK 字符占比。
    /// </summary>
    private static double CalculateCjkRatio(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return 0.0;

        int totalNonWhitespace = 0;
        int cjkCount = 0;

        for (int m = 0; m < request.Messages.Count; m++)
        {
            string text = request.Messages[m].GetText();
            if (string.IsNullOrEmpty(text)) continue;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
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

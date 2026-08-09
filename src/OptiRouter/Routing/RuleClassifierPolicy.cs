using System.Text.RegularExpressions;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// 规则分级策略：根据请求特征推断目标能力分档。
/// </summary>
/// <remarks>
/// intentional-simple: 以下阈值是经验值，用于快速路由分级。
/// 如需更精确，可替换为轻量级分类模型或更复杂的启发式。
/// </remarks>
public sealed class RuleClassifierPolicy : IRouterPolicy
{
    /// <summary>
    /// 数学/公式标记：LaTeX 环境、分数、函数式、中文求解/计算方程。
    /// 仅匹配明确的数学符号/结构，避免自然语言误报（"等于" / "平均" 不触发）。
    /// </summary>
    /// <remarks>
    /// intentional-simple: 正则覆盖常见 LaTeX 与中文数学请求，非穷尽。
    /// </remarks>
    private static readonly Regex MathIndicatorRegex = new(
        @"\\begin\{equation\}|\\frac\{|\\sum_|\\int_|f\([^)]*\)\s*=|求解|计算.*方程|证明.*不等式|求导|积分",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 翻译请求模式：英文 "translate X to/into Y" 与中文 "翻译...为/成/到"。
    /// 要求动词 + 方向结构，避免 "translation of" / "翻译质量" 等讨论性误报。
    /// </summary>
    /// <remarks>
    /// intentional-simple: 限定 translate/翻译 作动词（后接内容 + 方向词），降低误报。
    /// "translate this book to French" 命中；"the translation of this book" 不命中。
    /// </remarks>
    private static readonly Regex TranslationPatternRegex = new(
        @"translate\s+\S.{0,80}?\s+(?:to|into)\s+\S|" +
        @"翻译.{1,60}?(?:为|成|到)\S|" +
        @"把.{1,40}?翻译成",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>分类原因常量，ClassifyRequest 与 GetWeightsForClassification 共用，避免字符串漂移。</summary>
    private const string ReasonCodeDetected = "code-detected";
    private const string ReasonMathDetected = "math-detected";
    private const string ReasonComplexInstruction = "complex-instruction";
    private const string ReasonTranslationRequest = "translation-request";
    private const string ReasonSimpleQA = "simple-qa";
    private const string ReasonDefault = "default";
    private const string ReasonFallbackToDefault = "fallback-to-default";

    /// <summary>
    /// 多维路由能力分数容差：分数差距在此范围内的候选视为「能力相近」，改按价格择廉。
    /// 避免 tier 回退值（Strong 0.9 vs Cheap 0.3）让简单查询过度路由到昂贵模型。
    /// 0.15 对应单维度约半档能力差距，足以让 simple-qa 等低门槛查询在能力足够时选便宜模型，
    /// 同时保留 coding/reasoning 等强需求场景的能力主导排序。
    /// </summary>
    private const double CapabilityScoreTolerance = 0.15;

    /// <summary>
    /// 代码特征匹配正则：结合上下文与词边界，避免自然语言中 "select a dress" / "high class hotel" 等误判。
    /// </summary>
    private static readonly Regex CodeIndicatorRegex = new(
        @"```|" +
        @"#!/bin/|" +
        @"\bdef\s+[A-Za-z_]\w*\s*\(|" +
        @"\bfunction\s*(?:\*\s*)?[A-Za-z_]\w*\s*\(|" +
        @"\bclass\s+\w+\s*(?:[:\{\(]|extends\b|implements\b)|" +
        @"\bpublic\s+(?:class|interface|enum|struct|static|void|int|string|bool|double|float|long|var|async|override|virtual|sealed|abstract|event|delegate|[A-Z]\w*\s+\w+\s*[\(;=])\b|" +
        @"\bimport\s+(?:[""']|\{[\s\w,]+\}\s+from\b|\w+\s+from\s+[""']|\w+\s+as\s+\w+|\w+(?:\.\w+)*\s*;|(?:sys|os|json|re|math|datetime|typing|collections|asyncio|time|pathlib|subprocess)\b)|" +
        @"\bfrom\s+\w+\s+import\b|" +
        // SQL select：要求 select 与 from 之间或之后出现 SQL 子句关键词/分号，
        // 以排除自然语言 "select a dress from the catalog"（与注释 L50 误判防护一致）。
        // [\s\S]{1,200}? 不跨过远；(?=...) 锚定 from 后续关键词。
        @"\bselect\b[\s\S]{1,200}?\bfrom\b[\s\S]{0,300}?(?:\b(?:where|group|order|having|union|join|left|right|inner|outer|on|limit|offset|values|into|distinct)\b|;|"")|" +
        @"\bcreate\s+table\b|" +
        @"\binsert\s+into\b|" +
        @"\bsudo\s+(?:apt|apt-get|yum|dnf|pacman|systemctl|service|docker|netstat|chmod|chown|mkdir|rm|cp|mv|systemd|zypper|apk)\b|" +
        @"\bchmod\s+(?:\+[xrw]|[0-7]{3,4})\b|" +
        @"\bfunc\s+(?:\([^)]+\)\s*)?[A-Za-z_]\w*|" +
        @"\bpackage\s+[A-Za-z_]\w*|" +
        @"\bfn\s+[A-Za-z_]\w*\s*[\(<]|" +
        @"\bimpl(?:\s*<[^>]+>)?\s+(?:[A-Za-z_]\w*\s+for\s+)?[A-Za-z_]\w*|" +
        @"\bcargo\s+(?:build|run|test|check|clean|install|update|publish|bench|new|init)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableRuleClassifier)
        {
            return previous with { Reason = $"{previous.Reason}; rule-classifier: disabled" };
        }

        var (targetTier, targetReason, complexity) = ClassifyRequest(context);

        if (context.Options.Routing.EnableMultiDimensionalRouting)
        {
            var weights = GetWeightsForClassification(targetReason);
            // 多维路由修复：原实现纯按 capability score 降序、仅 score 相等才看价格。
            // tier 回退（Strong=0.9/Medium=0.6/Cheap=0.3）使 simple-qa 等仅靠 language 维度
            // 的查询里 Cheap 模型系统性垫底，Strong 必胜——成本分层被反转。
            // 修复语义：capability 是硬门槛，但 capability「相近」（差距 ≤ 阈值）时按价格择廉，
            // 让 cheap 模型在能力足够（分数不显著落后）时胜出。
            var scored = previous.Candidates
                .Select(m => (Model: m, Score: CalculateMatchScore(m, weights)))
                .ToList();
            scored.Sort((a, b) =>
            {
                double diff = b.Score - a.Score; // 降序
                if (Math.Abs(diff) > CapabilityScoreTolerance)
                    return diff.CompareTo(0);
                // 分数相近：便宜优先，避免为微弱能力差距多付成本。
                return a.Model.InputPricePerMillion.CompareTo(b.Model.InputPricePerMillion);
            });
            var reordered = scored.Select(s => s.Model).ToList();

            string mdReason = $"rule-classifier: multi-dimensional active ({targetReason}), weights=[{string.Join(", ", weights.Select(kv => $"{kv.Key}:{kv.Value:F1}"))}], {reordered.Count} candidates";
            return previous with
            {
                Candidates = reordered,
                Reason = $"{previous.Reason}; {mdReason}",
                RequestComplexity = complexity
            };
        }

        // 在上游策略（如 CapabilityFilter）已过滤的候选上再按 tier 筛，保持策略链叠加语义。
        // 从 AllModels 取会丢弃上游过滤结果（如 vision 标注），导致能力过滤失效。
        var candidates = FilterByTier(previous.Candidates, targetTier);

        if (candidates.Count == 0)
        {
            // 回落到 DefaultTier（仍基于 previous.Candidates，保持叠加）
            targetTier = context.Options.Routing.DefaultTier;
            targetReason = ReasonFallbackToDefault;
            candidates = FilterByTier(previous.Candidates, targetTier);
        }

        // 按 MaxContextTokens 降序
        candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();

        string reason = $"rule-classifier: target={targetTier}({targetReason}), {candidates.Count} candidates";

        return previous with
        {
            Candidates = candidates,
            Reason = $"{previous.Reason}; {reason}",
            RequestComplexity = complexity
        };
    }

    private static Dictionary<string, double> GetWeightsForClassification(string reason)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        switch (reason)
        {
            case ReasonCodeDetected:
                weights["coding"] = 1.0;
                weights["reasoning"] = 0.6;
                weights["language"] = 0.3;
                break;
            case ReasonMathDetected:
                weights["reasoning"] = 1.0;
                weights["coding"] = 0.5;
                weights["language"] = 0.3;
                break;
            case ReasonComplexInstruction:
                weights["reasoning"] = 0.8;
                weights["language"] = 0.7;
                break;
            case ReasonTranslationRequest:
                weights["language"] = 1.0;
                weights["coding"] = 0.1;
                break;
            case ReasonSimpleQA:
                weights["language"] = 1.0;
                weights["reasoning"] = 0.1;
                break;
            default:
                weights["language"] = 0.8;
                weights["reasoning"] = 0.5;
                break;
        }
        return weights;
    }

    private static double CalculateMatchScore(ModelEndpointOptions model, Dictionary<string, double> weights)
    {
        double score = 0.0;
        foreach (var (key, weight) in weights)
        {
            score += weight * model.GetEffectiveCapability(key);
        }
        return score;
    }

    private static (ModelTier Tier, string Reason, RequestComplexity Complexity) ClassifyRequest(RouterContext context)
    {
        var request = context.Request;
        bool hasCode = false;
        int totalMessageCount = request.Messages?.Count ?? 0;
        bool hasLongSystemPrompt = false;

        foreach (var msg in request.Messages ?? Enumerable.Empty<Clients.ChatMessage>())
        {
            var text = msg.GetText();
            if (msg.Role.Equals("system", StringComparison.OrdinalIgnoreCase) && text.Length > 2000)
            {
                hasLongSystemPrompt = true;
            }

            if (ContainsCodeIndicators(text))
            {
                hasCode = true;
            }
        }

        bool isSingleShortMessage = totalMessageCount == 1
            && (request.Messages?.FirstOrDefault()?.GetText().Length ?? 0) < 100
            && !hasCode;

        if (hasCode)
        {
            return (ModelTier.Strong, ReasonCodeDetected, RequestComplexity.Complex);
        }

        // 数学/公式：优先级仅次于代码。需 Strong 模型（符号推理、LaTeX 生成准确）。
        // 拼接全部文本后正则匹配，避免跨消息公式被截断漏检。
        if (ContainsMathIndicators(request))
        {
            return (ModelTier.Strong, ReasonMathDetected, RequestComplexity.Complex);
        }

        if (totalMessageCount > 1 && hasLongSystemPrompt)
        {
            return (ModelTier.Strong, ReasonComplexInstruction, RequestComplexity.Complex);
        }

        // 翻译：Medium 足够（现代中等模型翻译质量已达实用水平，Strong 边际收益低）。
        // 放在 simple-qa 检测之前——翻译请求即使单轮短消息也应走 Medium 而非 Cheap。
        if (ContainsTranslationPattern(request))
        {
            return (ModelTier.Medium, ReasonTranslationRequest, RequestComplexity.Standard);
        }

        if (isSingleShortMessage)
        {
            return (ModelTier.Cheap, ReasonSimpleQA, RequestComplexity.Simple);
        }

        return (context.Options.Routing.DefaultTier, ReasonDefault, RequestComplexity.Standard);
    }

    /// <summary>将所有消息文本用换行拼接，供跨消息正则匹配使用。</summary>
    private static string ConcatMessages(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var msg in request.Messages)
        {
            sb.Append(msg.GetText());
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 拼接所有消息文本，检测数学/公式标记。跨消息拼接避免公式被消息边界截断。
    /// </summary>
    private static bool ContainsMathIndicators(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0) return false;
        return MathIndicatorRegex.IsMatch(ConcatMessages(request));
    }

    /// <summary>
    /// 拼接所有消息文本，检测翻译请求模式。跨消息拼接覆盖"帮我翻译"在 system + user 分开的情况。
    /// </summary>
    private static bool ContainsTranslationPattern(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0) return false;
        return TranslationPatternRegex.IsMatch(ConcatMessages(request));
    }

    private static bool ContainsCodeIndicators(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        return CodeIndicatorRegex.IsMatch(content);
    }

    private static List<ModelEndpointOptions> FilterByTier(
        IReadOnlyList<ModelEndpointOptions> candidates,
        ModelTier tier)
    {
        // 不重复过滤 Enabled：初始候选链（RouterEngine）已排除 disabled 模型，
        // 上游策略亦不应引入 disabled 候选。此处仅按 tier 筛选。
        return candidates
            .Where(m => m.Tier == tier)
            .ToList();
    }
}

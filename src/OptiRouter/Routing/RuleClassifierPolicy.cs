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
    /// <inheritdoc />
    public PolicyGroup Group => PolicyGroup.Classify;

    /// <summary>
    /// 数学/公式标记：LaTeX 环境、Unicode 数学符号、函数式、中英文数学词汇。
    /// 仅匹配明确的数学符号/结构/术语，避免自然语言误报（"等于" / "平均" 不触发）。
    /// </summary>
    /// <remarks>
    /// intentional-simple: 覆盖常见 LaTeX、Unicode 符号（∑∫√π≤≥≠∞±×÷）与中英文数学
    /// 词汇（解方程/微积分/矩阵/概率分布/derivative/eigenvalue 等）；裸词如"概率""统计"
    /// 歧义大不收录，需组合形式（概率分布/统计检验）。
    /// </remarks>
    private static readonly Regex MathIndicatorRegex = new(
        @"\\begin\{equation\}|\\frac\{|\\sum_|\\int_|f\([^)]*\)\s*=" +
        // Unicode 数学符号：正文出现即数学性极强的信号。
        @"|[∑∫√≤≥≠∞±≡⊂⊃∈∉⊙⊗⊕∇∂²³ⁿ]" +
        @"|求解|解方程|方程组|计算.*方程|证明|不等式|求导|求积分|积分|微分方程|微积分|导数|偏导" +
        @"|极限|级数|收敛|发散|矩阵|特征值|特征向量|线性代数|排列组合|数列|因式分解|概率分布" +
        @"|期望值|方差|标准差|贝叶斯|统计检验|几何证明|充分必要|充要条件" +
        @"|\bsolve\s+(?:for\s+)?\w|\bequation|\bderivative|\bintegral\b|\bmatrix|\beigen" +
        @"|\bprove\s+(?:that|the)|\btheorem|\bcalculus|\bfactorial|\blimit\s+of\b" +
        @"|\bpower\s+series\b|\blinear\s+algebra\b|\bcombinatoric|\bprobability\s+distribution\b" +
        @"|\bstandard\s+deviation\b|\bprime\s+number\b|\blcm\b|\bgcd\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 翻译请求模式：结构化形式（动词 + 方向词）与口语化指令。
    /// 要求动词/方向结构，避免 "translation of" / "翻译质量" 等讨论性误报。
    /// </summary>
    /// <remarks>
    /// intentional-simple: "translate this book to French" 命中；"the translation of this book"
    /// 不命中。"帮我翻译/翻译一下/中译英" 等口语指令是明确的翻译意图，一并收录；
    /// "翻译理论/翻译质量" 等讨论话题不含这些指令结构，不误报。
    /// </remarks>
    private static readonly Regex TranslationPatternRegex = new(
        @"translate\s+\S.{0,80}?\s+(?:to|into)\s+\S|" +
        @"\btranslate\s+(?:this|that|it|the\s+\w+)\b|" +
        @"翻译.{1,60}?(?:为|成|到)\S|" +
        @"把.{1,40}?翻译成|" +
        @"(?:帮我|请|麻烦|给我)翻译|" +
        @"翻译(?:一下|这段|这个|过来)|" +
        @"中译英|英译中|日译中|中译日|韩译中|中译韩|中英互译|中日互译",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>分类原因常量，ClassifyRequest 与 GetWeightsForClassification 共用，避免字符串漂移。</summary>
    private const string ReasonCodeDetected = "code-detected";
    private const string ReasonCodeComplex = "code-complex";
    private const string ReasonCodeSimple = "code-simple";
    private const string ReasonMathDetected = "math-detected";
    private const string ReasonComplexInstruction = "complex-instruction";
    private const string ReasonTranslationRequest = "translation-request";
    private const string ReasonWritingRequest = "writing-request";
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

    /// <summary>
    /// 复杂代码意图：调试/修复/重构/优化/崩溃/报错/算法/性能/复杂度。
    /// 命中 → Strong（这类任务需要强推理与深层代码理解）。
    /// 与 <see cref="SimpleCodeIntentRegex"/> 同现时，复杂优先（代码能力优先，不降级）。
    /// </summary>
    /// <remarks>
    /// intentional-simple: 覆盖常见中英文代码任务动词，非穷尽；用于「是否值得上 Strong」的粗分。
    /// </remarks>
    private static readonly Regex ComplexCodeIntentRegex = new(
        @"\b(?:debug(?:ging)?|fix(?:ing)?|refactor(?:ing)?|optimize?|troubleshoot|bug|crash|exception|algorithm|complexity|performance)\b|" +
        @"修复|调试|重构|优化|崩溃|报错|异常|算法|性能|复杂度|为什么.*(?:报错|失败|不起作用|抛异常)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 简单代码意图：hello world / 简单示例 / 脚手架。
    /// 命中 → Medium（简单代码生成/脚手架不需要 Strong 的深度能力）。
    /// 仅用明确、低歧义的信号：英文裸名词 example/simple/basic 会误配代码里的
    /// 类名/注释（如 "public class Example {}" / "BasicAuth"），故排除。
    /// `explain`/`解释` 不在此列——解释复杂代码需要 Strong 推理，归入保守 Strong。
    /// </summary>
    /// <remarks>
    /// intentional-simple: 触发词限定明确「简单/脚手架」语义；意图检测只跑在指令文本上
    /// （见 <see cref="ExtractInstructionText"/>），不污染代码正文。
    /// </remarks>
    private static readonly Regex SimpleCodeIntentRegex = new(
        @"\bhello\s*world\b|\b(?:scaffold|boilerplate)\b|" +
        @"hello ?world|简单|脚手架|模板|示例|入门",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 复杂指令特征：深度分析/多步骤/结构化长文任务。命中 → Strong。
    /// 与 <see cref="SimpleCodeIntentRegex"/> 独立（本正则不限于代码请求）。
    /// 收录语义明确的组合词（深入分析/可行性分析/step by step/pros and cons），
    /// 裸词如"分析""总结"歧义大不收录。
    /// </summary>
    private static readonly Regex ComplexInstructionRegex = new(
        @"深入分析|详细分析|可行性分析|对比分析|利弊分析|竞品分析|多角度分析" +
        @"|架构设计|方案设计|技术选型|技术方案|系统设计" +
        @"|论文|研究报告|调研报告|文献综述|开题报告" +
        @"|一步一步(?:教|推导|讲|说明|解释|实现|走完)|分步骤|逐步推导|详细说明|详细解释|深入探讨|头脑风暴" +
        @"|\bstep[- ]by[- ]step\b|\bin[- ]depth\s+analysis\b|\bpros\s+and\s+cons\b" +
        @"|\bcompare\s+and\s+contrast\b|\btrade[- ]offs?\b|\bliterature\s+review\b" +
        @"|\bresearch\s+(?:report|proposal)\b|\bfeasibility\s+(?:study|analysis)\b" +
        @"|\bessay\s+outline\b|\bline\s+by\s+line\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 写作类任务：邮件/文案/报告/简历等语言生成为主的请求。命中 → Medium
    /// （语言能力主导、推理需求低，多维路由下让语言能力足够且便宜的模型胜出）。
    /// 收录明确的体裁信号，"写作"裸词与"帮我写代码"（先命中代码路径）不受影响。
    /// </summary>
    private static readonly Regex WritingRequestRegex = new(
        @"写一封|写封|帮我写.{0,12}(?:邮件|信|文案|周报|月报|总结|简历|通知|公告)" +
        @"|起草|润色|改写.{0,8}(?:成|为)|文案撰写|标题党" +
        @"|\bwrite\s+(?:an?\s+)?(?:email|letter|poem|essay|blog|copy|speech|slogan|headline)" +
        @"|\bdraft\s+(?:an?\s+)?(?:email|letter|proposal|memo|press\s+release)" +
        @"|\bpolish\s+(?:my|the|this)\b.{0,30}\b(?:email|essay|letter|paragraph|text)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableRuleClassifier)
        {
            return previous with { Reason = $"{previous.Reason}; rule-classifier: disabled" };
        }

        var (targetTier, targetReason, complexity) = ClassifyRequest(context);

        // 档位意图守卫：TargetTier 已携带显式意图（ExplicitModelPolicy pin 锁定/释放留痕、
        // RoutingModePolicy 模式预设）时按意图档过滤，不做内容分类——pin strong-a 失败释放后
        // 意图仍是 Strong 档，内容分类 simple-qa 筛 Cheap 会让降级跳档到与用户意图不符的模型。
        // 意图档在当前候选中无匹配时保留原候选（与下方兜底语义一致，不让意图清空候选）。
        if (previous.TargetTier is { } intentTier)
        {
            var intentCandidates = FilterByTier(previous.Candidates, intentTier);
            if (intentCandidates.Count == 0)
            {
                var keptMeta = previous with
                {
                    RequestComplexity = complexity,
                    ClassificationSignal = targetReason,
                    ClassificationTargetTier = intentTier
                };
                return keptMeta.Append("rule-classifier",
                    $"intent={intentTier} no candidates, keeping original {previous.Candidates.Count}");
            }

            intentCandidates = intentCandidates.OrderByDescending(m => m.MaxContextTokens).ToList();
            var intentDecision = previous with
            {
                Candidates = intentCandidates,
                RequestComplexity = complexity,
                ClassificationSignal = targetReason,
                ClassificationTargetTier = intentTier
            };
            return intentDecision.Append("rule-classifier",
                $"intent={intentTier} (classified {targetReason}), {intentCandidates.Count} candidates");
        }

        if (context.Options.Routing.EnableMultiDimensionalRouting)
        {
            var weights = GetWeightsForClassification(targetReason);
            // 多维路由语义：capability 是硬门槛，但 capability「相近」（差距 ≤ 阈值）时按价格择廉，
            // 让 cheap 模型在能力足够（分数不显著落后）时胜出，避免 tier 回退值让简单查询系统性选昂贵模型。
            // 实现：原比较器按「分差 ≤ 阈值则比价」成对排序，非传递（A~B、B~C 各自相近按价格排，
            // 但 A、C 分差跨过阈值按能力排，与价格序冲突），List.Sort 对非传递比较器结果是实现相关的。
            // 改为先把能力分数量化到 tolerance 桶（floor），同桶视为「能力相近」，
            // 再按 (桶降序, 价格升序) 排序：两段定序键传递且确定。
            var scored = previous.Candidates
                .Select(m => (Model: m, Score: CalculateMatchScore(m, weights)))
                .ToList();
            var reordered = scored
                .Select(s => (s.Model, Bucket: (int)Math.Floor(s.Score / CapabilityScoreTolerance)))
                .OrderByDescending(x => x.Bucket)
                .ThenBy(x => x.Model.InputPricePerMillion)
                .Select(x => x.Model)
                .ToList();

            string mdReason = $"multi-dimensional active ({targetReason}), weights=[{string.Join(", ", weights.Select(kv => $"{kv.Key}:{kv.Value:F1}"))}], {reordered.Count} candidates";
            var withMetadata = previous with
            {
                Candidates = reordered,
                RequestComplexity = complexity,
                ClassificationSignal = targetReason,
                ClassificationTargetTier = targetTier
            };
            return withMetadata.Append("rule-classifier", mdReason);
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

        // 目标 tier 与 DefaultTier 均无候选：保留原候选而非清空。
        // 与 LongInputPolicy/CapabilityFilterPolicy 的兜底语义一致——tier 不匹配不应
        // 让唯一可用模型（如 Strong+Cheap 配置下的翻译请求）被排除成空候选直接 503。
        if (candidates.Count == 0)
        {
            var withMetadata = previous with
            {
                RequestComplexity = complexity,
                ClassificationSignal = targetReason,
                ClassificationTargetTier = targetTier
            };
            return withMetadata.Append("rule-classifier", $"no {targetTier}({targetReason}) candidates, keeping original {previous.Candidates.Count}");
        }

        // 按 MaxContextTokens 降序
        candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();

        var final = previous with
        {
            Candidates = candidates,
            RequestComplexity = complexity,
            ClassificationSignal = targetReason,
            ClassificationTargetTier = targetTier
        };
        return final.Append("rule-classifier", $"target={targetTier}({targetReason}), {candidates.Count} candidates");
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
            case ReasonCodeComplex:
                // 调试/修复/重构/算法：coding 主导 + 更高 reasoning（深层代码理解）。
                weights["coding"] = 1.0;
                weights["reasoning"] = 0.8;
                weights["language"] = 0.2;
                break;
            case ReasonCodeSimple:
                // 简单代码生成/解释：coding 主导但 reasoning 需求低，能力相近时允许择廉。
                weights["coding"] = 1.0;
                weights["reasoning"] = 0.3;
                weights["language"] = 0.4;
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
            case ReasonWritingRequest:
                // 写作类：语言能力主导，推理需求低——能力相近时让便宜的语言模型胜出。
                weights["language"] = 1.0;
                weights["reasoning"] = 0.2;
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
            // 代码意图细分：不再一律 Strong。复杂代码（调试/修复/重构/算法）→ Strong；
            // 简单代码（hello world/脚手架/示例）→ Medium；无明确意图 → 保守 Strong（代码能力优先）。
            // 意图检测只跑在指令文本（最后一条 user 消息、剔除代码块）上，避免代码正文
            // 里的注释/字符串/标识符（如 "// simple"、"hello world"）被误判为意图。
            return ClassifyCodeIntent(ExtractInstructionText(request));
        }

        // 数学/公式：优先级仅次于代码。需 Strong 模型（符号推理、LaTeX 生成准确）。
        // 拼接全部文本后正则匹配，避免跨消息公式被截断漏检。
        if (ContainsMathIndicators(request))
        {
            return (ModelTier.Strong, ReasonMathDetected, RequestComplexity.Complex);
        }

        // 翻译：Medium 足够（现代中等模型翻译质量已达实用水平，Strong 边际收益低）。
        // 放在复杂指令之前——"帮我翻译这篇论文"是翻译任务，不应被"论文"关键词抢入 Strong。
        if (ContainsTranslationPattern(request))
        {
            return (ModelTier.Medium, ReasonTranslationRequest, RequestComplexity.Standard);
        }

        // 复杂指令：长系统提示的多轮任务（agent 编排类），或指令文本命中深度分析/
        // 多步骤/结构化长文信号。需 Strong 推理。
        var instructionText = ExtractInstructionText(request);
        if ((totalMessageCount > 1 && hasLongSystemPrompt)
            || ComplexInstructionRegex.IsMatch(instructionText))
        {
            return (ModelTier.Strong, ReasonComplexInstruction, RequestComplexity.Complex);
        }

        // 写作类：邮件/文案/周报等体裁明确的语言生成，Medium 足够。
        if (WritingRequestRegex.IsMatch(instructionText))
        {
            return (ModelTier.Medium, ReasonWritingRequest, RequestComplexity.Standard);
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

    /// <summary>
    /// 对含代码的请求做意图子分类。优先级：复杂 > 简单 > 默认 Strong。
    /// 复杂信号命中即 Strong（不降级）；简单信号命中才 Medium；均未命中保守 Strong（代码能力优先）。
    /// </summary>
    private static (ModelTier Tier, string Reason, RequestComplexity Complexity) ClassifyCodeIntent(string text)
    {
        if (ComplexCodeIntentRegex.IsMatch(text))
            return (ModelTier.Strong, ReasonCodeComplex, RequestComplexity.Complex);

        if (SimpleCodeIntentRegex.IsMatch(text))
            return (ModelTier.Medium, ReasonCodeSimple, RequestComplexity.Standard);

        return (ModelTier.Strong, ReasonCodeDetected, RequestComplexity.Complex);
    }

    /// <summary>
    /// fenced code block 匹配（``` 或 ~~~），用于从指令文本中剔除代码正文。
    /// </summary>
    private static readonly Regex FencedCodeBlockRegex = new(
        @"```[\s\S]*?```|~~~[\s\S]*?~~~",
        RegexOptions.Compiled);

    /// <summary>
    /// 提取意图检测用的指令文本：最后一条非空 user 消息，剔除 fenced code block。
    /// 意图信号应来自用户的自然语言指令，而非代码正文——代码里的注释/字符串/标识符
    /// （"// simple"、"hello world"）不应触发意图匹配。
    /// </summary>
    private static string ExtractInstructionText(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0) return string.Empty;
        for (int i = request.Messages.Count - 1; i >= 0; i--)
        {
            var msg = request.Messages[i];
            if (msg is null || !msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase)) continue;
            var text = msg.GetText();
            if (string.IsNullOrEmpty(text)) continue;
            return StripFencedCodeBlocks(text);
        }
        return string.Empty;
    }

    private static string StripFencedCodeBlocks(string text)
    {
        if (!text.Contains("```", StringComparison.Ordinal) && !text.Contains("~~~", StringComparison.Ordinal))
            return text;
        return FencedCodeBlockRegex.Replace(text, " ");
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

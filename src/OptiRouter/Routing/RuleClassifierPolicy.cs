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

    /// <inheritdoc />
    public RouterDecision Apply(RouterContext context, RouterDecision previous)
    {
        if (!context.Options.Routing.EnableRuleClassifier)
        {
            return previous with { Reason = $"{previous.Reason}; rule-classifier: disabled" };
        }

        var (targetTier, targetReason) = ClassifyRequest(context);
        var candidates = FilterByTier(context.AllModels, targetTier);

        if (candidates.Count == 0)
        {
            // 回落到 DefaultTier
            targetTier = context.Options.Routing.DefaultTier;
            targetReason = "fallback-to-default";
            candidates = FilterByTier(context.AllModels, targetTier);
        }

        // 按 MaxContextTokens 降序
        candidates = candidates.OrderByDescending(m => m.MaxContextTokens).ToList();

        string reason = $"rule-classifier: target={targetTier}({targetReason}), {candidates.Count} candidates";

        return previous with
        {
            Candidates = candidates,
            Reason = $"{previous.Reason}; {reason}"
        };
    }

    private static (ModelTier Tier, string Reason) ClassifyRequest(RouterContext context)
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
            return (ModelTier.Strong, "code-detected");
        }

        // 数学/公式：优先级仅次于代码。需 Strong 模型（符号推理、LaTeX 生成准确）。
        // 拼接全部文本后正则匹配，避免跨消息公式被截断漏检。
        if (ContainsMathIndicators(request))
        {
            return (ModelTier.Strong, "math-detected");
        }

        if (totalMessageCount > 1 && hasLongSystemPrompt)
        {
            return (ModelTier.Strong, "complex-instruction");
        }

        // 翻译：Medium 足够（现代中等模型翻译质量已达实用水平，Strong 边际收益低）。
        // 放在 simple-qa 检测之前——翻译请求即使单轮短消息也应走 Medium 而非 Cheap。
        if (ContainsTranslationPattern(request))
        {
            return (ModelTier.Medium, "translation-request");
        }

        if (isSingleShortMessage)
        {
            return (ModelTier.Cheap, "simple-qa");
        }

        return (context.Options.Routing.DefaultTier, "default");
    }

    /// <summary>
    /// 拼接所有消息文本，检测数学/公式标记。跨消息拼接避免公式被消息边界截断。
    /// </summary>
    private static bool ContainsMathIndicators(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0) return false;
        // intentional-simple: 拼接全部文本扫描一次正则。消息数通常 <50，总长度 <100KB，开销可忽略。
        var sb = new System.Text.StringBuilder();
        foreach (var msg in request.Messages)
        {
            sb.Append(msg.GetText());
            sb.Append('\n');
        }
        return MathIndicatorRegex.IsMatch(sb.ToString());
    }

    /// <summary>
    /// 拼接所有消息文本，检测翻译请求模式。跨消息拼接覆盖"帮我翻译"在 system + user 分开的情况。
    /// </summary>
    private static bool ContainsTranslationPattern(Clients.ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0) return false;
        var sb = new System.Text.StringBuilder();
        foreach (var msg in request.Messages)
        {
            sb.Append(msg.GetText());
            sb.Append('\n');
        }
        return TranslationPatternRegex.IsMatch(sb.ToString());
    }

    private static bool ContainsCodeIndicators(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;

        // intentional-simple: 常见代码标记，覆盖通用 + SQL/Shell/Go/Rust。
        // 不含中文标记（"函数"/"类" 在自然语言误报率高）。
        ReadOnlySpan<string> indicators = new[]
        {
            // 通用 / 围栏代码块
            "```",
            "function ",
            "def ",
            "class ",
            "public ",
            "import ",
            // SQL
            "select ",
            "create table",
            "insert into",
            // Shell
            "#!/bin/",
            "sudo ",
            "chmod ",
            // Go
            "func ",
            "package ",
            "go func",
            // Rust
            "fn ",
            "impl ",
            "cargo "
        };

        foreach (var indicator in indicators)
        {
            // OrdinalIgnoreCase：覆盖跨大小写的语言关键字（SQL SELECT/select、Rust fn/FN）。
            // 指标词均为代码/命令专属（select/fn/impl/cargo/sudo/chmod），自然语言误报率低。
            if (content.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static List<ModelEndpointOptions> FilterByTier(
        IReadOnlyList<ModelEndpointOptions> models,
        ModelTier tier)
    {
        return models
            .Where(m => m.Enabled && m.Tier == tier)
            .ToList();
    }
}

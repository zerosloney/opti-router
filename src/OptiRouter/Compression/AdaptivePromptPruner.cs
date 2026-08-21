using System.Text;
using System.Text.RegularExpressions;
using OptiRouter.Clients;
using OptiRouter.Routing;

namespace OptiRouter.Compression;

/// <summary>
/// 提示词压缩器抽象契约。
/// </summary>
public interface IPromptPruner
{
    /// <summary>
    /// 对输入的 ChatRequest 执行智能提示词压缩与 Token 动态瘦身。
    /// </summary>
    /// <param name="request">原始请求。</param>
    /// <param name="options">压缩参数（为 null 时使用默认配置）。</param>
    /// <returns>压缩后的请求与统计指标。</returns>
    PromptCompressionResult Compress(ChatRequest request, PromptCompressionOptions? options = null);
}

/// <summary>
/// 自适应提示词压缩与 Token 动态瘦身引擎 (Adaptive Prompt Compression &amp; Token Pruner)。
/// 具备系统指令多轮去重、历史轮次滑动窗口折叠、寒暄填充语剔除与代码块/JSON 严格无损防护能力。
/// </summary>
public sealed class AdaptivePromptPruner : IPromptPruner
{
    private readonly ITokenEstimator _tokenEstimator;

    private static readonly Regex FillerRegex = new(
        @"(?i)\b(sure,?\s*(i (can|would be happy to) help( with that)?|here (is|are)|certainly|of course|gladly))\b[.,!:]?|" +
        @"(?i)\b(as an ai language model|as an ai assistant|as a large language model)\b[.,!:]?|" +
        @"(?i)\b(let me know if you (have any questions|need (any )?more (help|details|information))|hope this helps!?|feel free to ask)\b[.,!:]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RedundantWhitespaceRegex = new(
        @"[ \t]+",
        RegexOptions.Compiled);

    private static readonly Regex MultiNewlineRegex = new(
        @"\n{3,}",
        RegexOptions.Compiled);

    private static readonly Regex CodeBlockRegex = new(
        @"```[\s\S]*?```",
        RegexOptions.Compiled);

    /// <summary>
    /// 初始化自适应压缩器。
    /// </summary>
    public AdaptivePromptPruner(ITokenEstimator? tokenEstimator = null)
    {
        _tokenEstimator = tokenEstimator ?? new BucketTokenEstimator();
    }

    /// <inheritdoc />
    public PromptCompressionResult Compress(ChatRequest request, PromptCompressionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        options ??= new PromptCompressionOptions();

        if (!options.Enabled || request.Messages is null || request.Messages.Count == 0)
        {
            int tokens = _tokenEstimator.Estimate(request);
            return new PromptCompressionResult(request, tokens, tokens, 0.0, false, "disabled_or_empty");
        }

        int origTokens = _tokenEstimator.Estimate(request);
        if (origTokens < options.MinTokensToTrigger)
        {
            return new PromptCompressionResult(request, origTokens, origTokens, 0.0, false, $"below_min_threshold_{origTokens}_lt_{options.MinTokensToTrigger}");
        }

        var messages = request.Messages;
        var newMessages = new List<ChatMessage>(messages.Count);

        // 1. 系统指令去重与归并 (System Prompt Deduplication)
        var seenSystemTexts = new HashSet<string>(StringComparer.Ordinal);
        var nonSystemMessages = new List<ChatMessage>();

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                string text = msg.GetText().Trim();
                if (!options.DeduplicateSystemPrompts || seenSystemTexts.Add(text))
                {
                    newMessages.Add(msg);
                }
            }
            else
            {
                nonSystemMessages.Add(msg);
            }
        }

        // 2. 多轮历史滑动窗口划分 (Preserve Recent Turns)
        int preserveCount = Math.Max(0, options.PreserveRecentTurns * 2);
        int historyCount = Math.Max(0, nonSystemMessages.Count - preserveCount);

        var strategiesApplied = new List<string>();
        if (seenSystemTexts.Count > 0 && options.DeduplicateSystemPrompts)
        {
            strategiesApplied.Add("system_dedup");
        }

        // 3. 对历史陈旧轮次执行剪枝与填充语剔除
        for (int i = 0; i < nonSystemMessages.Count; i++)
        {
            var msg = nonSystemMessages[i];
            bool isRecent = i >= historyCount;

            if (isRecent)
            {
                // 最近对话原样保留
                newMessages.Add(msg);
            }
            else
            {
                // 历史对话进行安全剪枝
                string rawText = msg.GetText();
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    newMessages.Add(msg);
                    continue;
                }

                // 仅对纯文本消息剪枝重建：多模态 content（如 vision 的 image_url 数组）经
                // FromText 重建会丢失非文本部分，原样保留以不破坏消息结构。
                if (msg.Content is not { ValueKind: System.Text.Json.JsonValueKind.String })
                {
                    newMessages.Add(msg);
                    continue;
                }

                string prunedText = PruneMessageText(rawText, options);
                if (prunedText.Length < rawText.Length)
                {
                    strategiesApplied.Add("history_filler_prune");
                }

                // 剪枝后文本未变化时保留原消息引用，避免无谓重建。
                // 重建必须用 `with` 只替换 Content：FromText 会丢弃 ExtensionData
                // （tool_calls / tool_call_id / reasoning_content 等），
                // 破坏工具调用配对，严格校验的上游（如 stepfun）直接 400。
                newMessages.Add(prunedText.Equals(rawText, StringComparison.Ordinal)
                    ? msg
                    : msg with { Content = System.Text.Json.JsonSerializer.SerializeToElement(prunedText) });
            }
        }

        var compressedRequest = request with { Messages = newMessages };
        int compressedTokens = _tokenEstimator.Estimate(compressedRequest);
        double reduction = origTokens > 0
            ? Math.Max(0.0, 1.0 - ((double)compressedTokens / origTokens))
            : 0.0;

        string summary = strategiesApplied.Count > 0
            ? string.Join("+", strategiesApplied.Distinct())
            : "no_reduction_needed";

        return new PromptCompressionResult(
            CompressedRequest: compressedRequest,
            OriginalEstimatedTokens: origTokens,
            CompressedEstimatedTokens: compressedTokens,
            ReductionRatio: reduction,
            WasCompressed: compressedTokens < origTokens,
            StrategySummary: summary);
    }

    private static string PruneMessageText(string text, PromptCompressionOptions options)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var placeholders = new Dictionary<string, string>();

        // 保护代码块与 JSON（用占位符置换）
        if (options.PreserveCodeAndJson)
        {
            int blockIndex = 0;
            text = CodeBlockRegex.Replace(text, match =>
            {
                string key = $"__CODE_BLOCK_{blockIndex++}__";
                placeholders[key] = match.Value;
                return key;
            });
        }

        // 剔除客套填充语与无意义问候
        if (options.StripConversationalFillers)
        {
            text = FillerRegex.Replace(text, string.Empty);
        }

        // 规范化多余空行与水平空白
        text = RedundantWhitespaceRegex.Replace(text, " ");
        text = MultiNewlineRegex.Replace(text, "\n\n");
        text = text.Trim();

        // 还原受保护的代码块
        if (options.PreserveCodeAndJson && placeholders.Count > 0)
        {
            foreach (var (key, originalValue) in placeholders)
            {
                text = text.Replace(key, originalValue, StringComparison.Ordinal);
            }
        }

        return text;
    }
}

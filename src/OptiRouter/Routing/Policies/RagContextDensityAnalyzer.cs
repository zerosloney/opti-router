using System.Text.RegularExpressions;
using OptiRouter.Clients;
using OptiRouter.Configuration;

namespace OptiRouter.Routing;

/// <summary>
/// RAG 上下文充分度评估等级。
/// </summary>
public enum RagSufficiency
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Conflict = 4
}

/// <summary>
/// RAG 上下文密度与充分度分析结果。
/// </summary>
public sealed record RagAnalysisResult(
    bool HasRagContext,
    int DocumentCount,
    int TotalContextChars,
    double QueryCoverageRatio,
    double InformationDensityScore,
    RagSufficiency Sufficiency,
    ModelTier RecommendedTier,
    string Summary);

/// <summary>
/// 知识库与 RAG (Retrieval-Augmented Generation) 上下文密度分析器。
/// 零外部依赖，自动检测提示词中嵌入的知识库片段，计算查询词覆盖率与信息密度，
/// 评估知识充分度并推荐最适模型分档（高充分度推荐 Cheap 抽取；匮乏或冲突推荐 Strong 深度推理）。
/// </summary>
public sealed class RagContextDensityAnalyzer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "what", "is", "the", "of", "in", "to", "a", "an", "and", "or", "its", "it",
        "how", "why", "where", "when", "which", "who", "whom", "this", "that", "these",
        "those", "can", "could", "would", "should", "does", "do", "did", "for", "with",
        "about", "as", "at", "by", "from", "on", "into", "through", "during", "before",
        "after", "above", "below", "all", "any", "both", "each", "few", "more", "most",
        "other", "some", "such", "no", "nor", "not", "only", "own", "same", "so", "than",
        "too", "very", "just", "now", "are", "was", "were", "be", "been", "being", "have",
        "has", "had", "having", "do", "does", "did", "doing", "would", "should", "could",
        "ought", "i", "you", "he", "she", "we", "they", "me", "him", "her", "us", "them",
        "my", "your", "his", "her", "our", "their", "mine", "yours", "ours", "theirs",
        "请问", "什么", "怎么", "如何", "是", "的", "了", "在", "和", "有", "及", "与", "等", "个", "这", "那"
    };

    private static readonly Regex TaggedContextRegex = new(
        @"(?:<context>([\s\S]*?)<\/context>|<documents>([\s\S]*?)<\/documents>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeaderContextRegex = new(
        @"^(?:\[(?:Context|Reference Material|References|知识库|参考资料|已知信息)\]|参考资料[：:]|知识库片段[：:]|已知信息[：:]|Background Documents[：:])\s*([\s\S]*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DocItemRegex = new(
        @"(?:\[\d+\]|Doc \d+:|Document \d+:|文档 \d+[：:]|片段 \d+[：:])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 对 ChatRequest 进行 RAG 上下文提取与充分度分析。
    /// </summary>
    public RagAnalysisResult Analyze(ChatRequest request)
    {
        if (request?.Messages == null || request.Messages.Count == 0)
        {
            return new RagAnalysisResult(
                HasRagContext: false,
                DocumentCount: 0,
                TotalContextChars: 0,
                QueryCoverageRatio: 0.0,
                InformationDensityScore: 0.0,
                Sufficiency: RagSufficiency.None,
                RecommendedTier: ModelTier.Medium,
                Summary: "No messages in request.");
        }

        string userQuery = string.Empty;
        var contextBlocks = new List<string>();

        // 提取用户 Query 与 System / User 消息中的 Context 块
        foreach (var msg in request.Messages)
        {
            string text = msg.GetText() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                // 1. 检查 <context>...</context> 或 <documents>...</documents>
                var tagMatches = TaggedContextRegex.Matches(text);
                if (tagMatches.Count > 0)
                {
                    foreach (Match m in tagMatches)
                    {
                        string block = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(block)) contextBlocks.Add(block.Trim());
                    }
                    string cleaned = TaggedContextRegex.Replace(text, string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        userQuery = cleaned;
                    }
                }
                else
                {
                    // 2. 检查 [Reference Material] 或 [Context] 等标头
                    var headerMatch = HeaderContextRegex.Match(text);
                    if (headerMatch.Success)
                    {
                        string contentAfterHeader = headerMatch.Groups[1].Value.Trim();
                        // 寻找末尾的问题行（通常是最后一行或包含问号）
                        var lines = contentAfterHeader.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 1)
                        {
                            string lastLine = lines[^1].Trim();
                            if (lastLine.EndsWith('?') || lastLine.EndsWith('？') || lines.Length >= 2)
                            {
                                userQuery = lastLine;
                                string ctx = string.Join("\n", lines.Take(lines.Length - 1));
                                contextBlocks.Add(ctx.Trim());
                            }
                            else
                            {
                                contextBlocks.Add(contentAfterHeader);
                            }
                        }
                        else
                        {
                            contextBlocks.Add(contentAfterHeader);
                        }
                    }
                    else
                    {
                        userQuery = text;
                    }
                }
            }
            else if (string.Equals(msg.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                var tagMatches = TaggedContextRegex.Matches(text);
                if (tagMatches.Count > 0)
                {
                    foreach (Match m in tagMatches)
                    {
                        string block = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        if (!string.IsNullOrWhiteSpace(block)) contextBlocks.Add(block.Trim());
                    }
                }
                else
                {
                    var headerMatch = HeaderContextRegex.Match(text);
                    if (headerMatch.Success)
                    {
                        contextBlocks.Add(headerMatch.Groups[1].Value.Trim());
                    }
                }
            }
        }

        if (contextBlocks.Count == 0)
        {
            return new RagAnalysisResult(
                HasRagContext: false,
                DocumentCount: 0,
                TotalContextChars: 0,
                QueryCoverageRatio: 0.0,
                InformationDensityScore: 0.0,
                Sufficiency: RagSufficiency.None,
                RecommendedTier: ModelTier.Medium,
                Summary: "No RAG context blocks detected.");
        }

        string combinedContext = string.Join("\n", contextBlocks);
        int totalChars = combinedContext.Length;

        // 计算文档片段数
        int docCount = 0;
        var docMatches = DocItemRegex.Matches(combinedContext);
        docCount = docMatches.Count > 0 ? docMatches.Count : contextBlocks.Count;

        // 分词并过滤停用词，计算 Query 关键词覆盖率
        var rawQueryTokens = TfIdfSemanticVectorEngine.Tokenize(userQuery);
        var contentQueryTokens = rawQueryTokens.Where(t => !StopWords.Contains(t)).ToList();
        var contextTokens = new HashSet<string>(TfIdfSemanticVectorEngine.Tokenize(combinedContext), StringComparer.OrdinalIgnoreCase);

        double queryCoverage = 0.0;
        if (contentQueryTokens.Count > 0)
        {
            int covered = contentQueryTokens.Count(t => contextTokens.Contains(t));
            queryCoverage = (double)covered / contentQueryTokens.Count;
        }
        else if (rawQueryTokens.Count > 0)
        {
            int covered = rawQueryTokens.Count(t => contextTokens.Contains(t));
            queryCoverage = (double)covered / rawQueryTokens.Count;
        }

        // 计算信息密度（去重词汇数 / 总词汇数）
        var rawContextTokens = TfIdfSemanticVectorEngine.Tokenize(combinedContext);
        double infoDensity = rawContextTokens.Count > 0
            ? Math.Clamp((double)contextTokens.Count / rawContextTokens.Count, 0.1, 1.0)
            : 0.5;

        // 冲突检测：检查是否存在矛盾关键词（如 "然而不是", "相反", "并非", "contradict", "disagree", "dispute"）
        bool hasConflict = combinedContext.Contains("冲突", StringComparison.OrdinalIgnoreCase) ||
                           combinedContext.Contains("矛盾", StringComparison.OrdinalIgnoreCase) ||
                           combinedContext.Contains("相反", StringComparison.OrdinalIgnoreCase) ||
                           combinedContext.Contains("however", StringComparison.OrdinalIgnoreCase) ||
                           combinedContext.Contains("contradict", StringComparison.OrdinalIgnoreCase) ||
                           combinedContext.Contains("disagree", StringComparison.OrdinalIgnoreCase);

        RagSufficiency sufficiency;
        ModelTier recommendedTier;

        if (hasConflict)
        {
            sufficiency = RagSufficiency.Conflict;
            recommendedTier = ModelTier.Strong;
        }
        else if (queryCoverage >= 0.70 && totalChars >= 30)
        {
            sufficiency = RagSufficiency.High;
            recommendedTier = ModelTier.Cheap;
        }
        else if (queryCoverage >= 0.35)
        {
            sufficiency = RagSufficiency.Medium;
            recommendedTier = ModelTier.Medium;
        }
        else
        {
            sufficiency = RagSufficiency.Low;
            recommendedTier = ModelTier.Strong;
        }

        string summary = $"RAG detected: docs={docCount}, chars={totalChars}, coverage={queryCoverage:P1}, density={infoDensity:F2}, sufficiency={sufficiency} -> tier={recommendedTier}";

        return new RagAnalysisResult(
            HasRagContext: true,
            DocumentCount: docCount,
            TotalContextChars: totalChars,
            QueryCoverageRatio: queryCoverage,
            InformationDensityScore: infoDensity,
            Sufficiency: sufficiency,
            RecommendedTier: recommendedTier,
            Summary: summary);
    }
}

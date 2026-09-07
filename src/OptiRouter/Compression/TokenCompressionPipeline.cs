using System.Text;
using System.Text.Json;
using OptiRouter.Clients;

namespace OptiRouter.Compression;

/// <summary>
/// Token 压缩流水线（RTK + Caveman 双引擎串联）。
///
/// 流水线阶段：
/// 1. <see cref="RtkShellOutputCompressor"/> — Shell/bash 输出压缩
/// 2. <see cref="CavemanCondenser"/> — Caveman 风格文本精简
/// 3. <see cref="AdaptivePromptPruner"/> — 对话历史结构压缩（折叠/去重）
///
/// 与 OmniRoute RTK → Caveman 串联设计对齐，但作用于不同的输入类型：
/// - 工具输出 → RTK
/// - 对话消息内容 → Caveman
/// - 对话历史结构 → AdaptivePromptPruner
/// </summary>
public sealed class TokenCompressionPipeline
{
    private readonly RtkShellOutputCompressor _rtk;
    private readonly CavemanCondenser _caveman;
    private readonly AdaptivePromptPruner _pruner;

    /// <summary>
    /// 初始化压缩流水线。
    /// </summary>
    public TokenCompressionPipeline(
        RtkShellOutputCompressor? rtk = null,
        CavemanCondenser? caveman = null,
        AdaptivePromptPruner? pruner = null)
    {
        _rtk = rtk ?? new RtkShellOutputCompressor();
        _caveman = caveman ?? new CavemanCondenser();
        _pruner = pruner ?? new AdaptivePromptPruner();
    }

    /// <summary>
    /// 压缩工具执行结果（Shell 输出等）。
    /// 使用 RTK → Caveman 两阶段。
    /// </summary>
    public OutputCompressionResult CompressToolOutput(
        string rawOutput,
        OutputCompressionOptions? options = null)
    {
        // Stage 1: RTK Shell 压缩
        var rtkResult = _rtk.Compress(rawOutput, options);

        // Stage 2: Caveman 精简（仅在 RTK 有收益时）
        if (rtkResult.WasCompressed && rtkResult.Result.Length > 50)
        {
            var cavemanResult = _caveman.Condense(rtkResult.Result, new TextCondensationOptions { Enabled = true });
            if (cavemanResult.WasCompressed)
            {
                return new OutputCompressionResult(
                    Result: cavemanResult.Result,
                    OriginalLength: rtkResult.OriginalLength,
                    CompressedLength: cavemanResult.CompressedLength,
                    ReductionRatio: 1.0 - ((double)cavemanResult.CompressedLength / rtkResult.OriginalLength),
                    WasCompressed: true,
                    StrategySummary: $"rtk+caveman({rtkResult.StrategySummary}|{cavemanResult.StrategySummary})");
            }
        }

        return rtkResult;
    }

    /// <summary>
    /// 压缩对话请求（历史折叠 + 内容精简）。
    /// 使用 Caveman → AdaptivePromptPruner 两阶段。
    /// </summary>
    public PromptCompressionResult CompressRequest(
        ChatRequest request,
        PromptCompressionOptions? options = null)
    {
        // Stage 1: Caveman 内容精简（只处理最近 N 条消息，避免破坏早期上下文）
        var modified = request;
        if (request.Messages is { Count: > 0 } messages)
        {
            // 对最近 6 条消息做 Caveman 精简（保护工具调用配对）
            int recentCount = Math.Min(6, messages.Count);
            int startIdx = messages.Count - recentCount;
            var preservedHead = messages.Take(startIdx).ToList();

            var condensed = messages.Skip(startIdx)
                .Select(m =>
                {
                    if (m.Role == "system") return m; // 系统消息不做内容精简
                    if (m.ExtensionData?.ContainsKey("tool_calls") == true) return m; // 工具调用对不做精简

                    var text = m.GetText();
                    if (string.IsNullOrWhiteSpace(text)) return m;

                    var result = _caveman.Condense(text, new TextCondensationOptions { Enabled = true });
                    if (!result.WasCompressed) return m;

                    return m with
                    {
                        Content = JsonSerializer.SerializeToElement(result.Result)
                    };
                })
                .ToList();

            modified = request with { Messages = preservedHead.Concat(condensed).ToList() };
        }

        // Stage 2: AdaptivePromptPruner 历史结构压缩
        var prunerResult = _pruner.Compress(modified, options);

        return new PromptCompressionResult(
            CompressedRequest: prunerResult.CompressedRequest,
            OriginalEstimatedTokens: prunerResult.OriginalEstimatedTokens,
            CompressedEstimatedTokens: prunerResult.CompressedEstimatedTokens,
            ReductionRatio: prunerResult.ReductionRatio,
            WasCompressed: prunerResult.WasCompressed,
            StrategySummary: $"caveman+pruner({prunerResult.StrategySummary})");
    }
}

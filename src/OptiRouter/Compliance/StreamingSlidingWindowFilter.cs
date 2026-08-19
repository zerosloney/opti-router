using System.Text;
using OptiRouter.Configuration;

namespace OptiRouter.Compliance;

/// <summary>
/// 零拷贝 SSE 流式滑动窗口敏感词在线拦截器实现。
/// 基于 Span/Memory 滑动窗口算法，以零额内存开销解决跨 Chunk 拆分敏感词在线检出。
/// </summary>
public sealed class StreamingSlidingWindowFilter : IStreamingComplianceFilter
{
    private readonly string[] _keywords;
    private readonly ComplianceAction _action;
    private readonly int _maxKeywordLength;
    private readonly string _replacementMask;

    public StreamingSlidingWindowFilter(
        IEnumerable<string> sensitiveKeywords,
        ComplianceAction action = ComplianceAction.Block,
        string replacementMask = "***")
    {
        _keywords = sensitiveKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _action = action;
        _replacementMask = replacementMask;
        _maxKeywordLength = _keywords.Length > 0 ? _keywords.Max(k => k.Length) : 0;
    }

    public StreamingSlidingWindowFilter(RoutingOptions options)
        : this(
            options.StreamingSensitiveKeywords,
            options.StreamingComplianceAction,
            options.StreamingComplianceReplacementMask)
    {
    }

    public ComplianceCheckResult ProcessChunk(string chunkText, StreamingSlidingWindowBuffer buffer)
    {
        if (_keywords.Length == 0 || string.IsNullOrEmpty(chunkText))
        {
            return new ComplianceCheckResult(false, chunkText);
        }

        ReadOnlySpan<char> chunkSpan = chunkText.AsSpan();
        ReadOnlySpan<char> tailSpan = buffer.TailSpan;

        // 1. 构建滑动窗口 View Span（前一 Chunk 尾部 + 当前 Chunk 增量）
        int combinedLen = tailSpan.Length + chunkSpan.Length;
        char[]? rented = null;
        Span<char> combinedSpan = combinedLen <= 512
            ? stackalloc char[combinedLen]
            : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(combinedLen)).AsSpan(0, combinedLen);

        try
        {
            tailSpan.CopyTo(combinedSpan);
            chunkSpan.CopyTo(combinedSpan.Slice(tailSpan.Length));

            // 2. 检查敏感词匹配
            string? matchedKeyword = null;
            int matchedIndexInCombined = -1;

            ReadOnlySpan<char> readOnlyCombinedSpan = combinedSpan;

            foreach (var kw in _keywords)
            {
                int idx = readOnlyCombinedSpan.IndexOf(kw.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    if (matchedIndexInCombined < 0 || idx < matchedIndexInCombined)
                    {
                        matchedIndexInCombined = idx;
                        matchedKeyword = kw;
                    }
                }
            }

            // 3. 触发命中处理
            if (matchedKeyword != null && matchedIndexInCombined >= 0)
            {
                if (_action == ComplianceAction.Block)
                {
                    buffer.Clear();
                    return new ComplianceCheckResult(true, string.Empty, matchedKeyword);
                }

                // ComplianceAction.Redact 替换处理
                StringBuilder sb = new StringBuilder(combinedLen);
                ReadOnlySpan<char> remaining = combinedSpan;

                while (remaining.Length > 0)
                {
                    int earliestIdx = -1;
                    string? earliestKw = null;

                    foreach (var kw in _keywords)
                    {
                        int idx = remaining.IndexOf(kw.AsSpan(), StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0 && (earliestIdx < 0 || idx < earliestIdx))
                        {
                            earliestIdx = idx;
                            earliestKw = kw;
                        }
                    }

                    if (earliestIdx < 0 || earliestKw == null)
                    {
                        sb.Append(remaining);
                        break;
                    }

                    sb.Append(remaining.Slice(0, earliestIdx));
                    sb.Append(_replacementMask);
                    remaining = remaining.Slice(earliestIdx + earliestKw.Length);
                }

                string redactedCombined = sb.ToString();
                // 暂存末尾 maxKeywordLength-1 字符到下一轮再下发：任何要跨 chunk 才能拼完整的敏感词，
                // 在完整出现前不会被部分下发（修复"敏感词前缀 + [REDACTED] 并存"的边界泄漏）。
                // Redact 模式下 Buffer 语义为"未下发的 pending 后缀"，流结束时经 FlushRemaining 补发。
                int holdbackLen = Math.Min(Math.Max(0, _maxKeywordLength - 1), redactedCombined.Length);
                int emitLen = redactedCombined.Length - holdbackLen;
                string sanitizedChunkText = redactedCombined[..emitLen];
                buffer.UpdateTail(redactedCombined.AsSpan(emitLen), holdbackLen);

                return new ComplianceCheckResult(true, sanitizedChunkText, matchedKeyword);
            }

            // 4. 未命中：Block 模式保持"已输出尾部窗口"语义供跨 chunk 检测；
            //    Redact 模式同样暂存末尾（本轮未命中不代表下一 chunk 不会补全敏感词前缀）。
            int tailLenToKeep = Math.Max(0, _maxKeywordLength - 1);
            if (_action == ComplianceAction.Redact)
            {
                string combinedText = new string(combinedSpan);
                int holdbackLen = Math.Min(tailLenToKeep, combinedText.Length);
                string emitText = combinedText[..(combinedText.Length - holdbackLen)];
                buffer.UpdateTail(combinedText.AsSpan(combinedText.Length - holdbackLen), holdbackLen);
                return new ComplianceCheckResult(false, emitText);
            }
            buffer.UpdateTail(combinedSpan, tailLenToKeep);

            return new ComplianceCheckResult(false, chunkText);
        }
        finally
        {
            if (rented != null)
            {
                System.Buffers.ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc />
    public string FlushRemaining(StreamingSlidingWindowBuffer buffer)
    {
        // Redact 模式：补发仍暂存的 pending 后缀（此后不再有增量，暂存前缀不可能补全为敏感词）。
        if (_action != ComplianceAction.Redact || buffer.TailSpan.Length == 0)
        {
            buffer.Clear();
            return string.Empty;
        }
        string pending = new string(buffer.TailSpan);
        buffer.Clear();
        return pending;
    }
}

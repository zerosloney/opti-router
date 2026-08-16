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
                // 从 redactedCombined 中剔除已被前一 Chunk 输出过的 tailSpan 前缀部分
                int skipLen = Math.Min(tailSpan.Length, matchedIndexInCombined);
                string sanitizedChunkText = redactedCombined.Length >= skipLen
                    ? redactedCombined.Substring(skipLen)
                    : redactedCombined;

                int maxTailToKeep = Math.Max(0, _maxKeywordLength - 1);
                buffer.UpdateTail(sanitizedChunkText.AsSpan(), maxTailToKeep);

                return new ComplianceCheckResult(true, sanitizedChunkText, matchedKeyword);
            }

            // 4. 未命中：更新滑动 Buffer 尾部并原样输出 Chunk
            int tailLenToKeep = Math.Max(0, _maxKeywordLength - 1);
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
}

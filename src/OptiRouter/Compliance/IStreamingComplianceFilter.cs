namespace OptiRouter.Compliance;

/// <summary>
/// 流式合规过滤动作模式。
/// </summary>
public enum ComplianceAction
{
    /// <summary>
    /// 触发敏感词时立即阻断并终止流式传输。
    /// </summary>
    Block = 0,

    /// <summary>
    /// 触发敏感词时对敏感内容进行掩码脱敏（如转化为 ***），流继续传输。
    /// </summary>
    Redact = 1
}

/// <summary>
/// 增量流式 Chunk 合规检测结果。
/// </summary>
public sealed record ComplianceCheckResult(
    bool IsViolation,
    string ProcessedText,
    string? MatchedKeyword = null);

/// <summary>
/// 流式滑动窗口状态上下文，维护跨 Chunk 边界敏感词检测的尾部字符。
/// </summary>
public sealed class StreamingSlidingWindowBuffer
{
    private readonly char[] _buffer;
    private int _length;

    public StreamingSlidingWindowBuffer(int maxWindowSize = 1024)
    {
        _buffer = new char[Math.Max(64, maxWindowSize)];
        _length = 0;
    }

    public ReadOnlySpan<char> TailSpan => _buffer.AsSpan(0, _length);

    public void UpdateTail(ReadOnlySpan<char> newTail, int maxTailLength)
    {
        int copyLen = Math.Min(newTail.Length, maxTailLength);
        if (copyLen <= 0)
        {
            _length = 0;
            return;
        }

        ReadOnlySpan<char> slice = newTail.Slice(newTail.Length - copyLen, copyLen);
        slice.CopyTo(_buffer);
        _length = copyLen;
    }

    public void Clear()
    {
        _length = 0;
    }
}

/// <summary>
/// 零拷贝 SSE 流式滑动窗口敏感词与合规在线拦截器接口。
/// </summary>
public interface IStreamingComplianceFilter
{
    /// <summary>
    /// 对增量 Chunk 文本进行滑动窗口在线合规检测。
    /// </summary>
    /// <param name="chunkText">当前 Chunk 增量文本。</param>
    /// <param name="buffer">流式会话绑定的滑动窗口 Buffer。</param>
    /// <returns>检测结果与处理后的文本。</returns>
    ComplianceCheckResult ProcessChunk(string chunkText, StreamingSlidingWindowBuffer buffer);

    /// <summary>
    /// 流结束时补发因跨 chunk 检测而暂存未下发的尾部文本（Redact 模式的 pending 后缀）。
    /// 此后不再有增量，跨 chunk 匹配窗口关闭；默认无暂存。
    /// </summary>
    string FlushRemaining(StreamingSlidingWindowBuffer buffer) => string.Empty;
}

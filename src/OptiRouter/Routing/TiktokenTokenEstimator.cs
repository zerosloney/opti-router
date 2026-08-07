using OptiRouter.Clients;
using SharpToken;

namespace OptiRouter.Routing;

/// <summary>
/// 基于 SharpToken（tiktoken 的 C# 移植，内置词表、离线可用）的精确 token 计数。
/// 按消息逐条 BPE 计数，另计每条消息的固定开销（role 标记 + 分隔符，约 3 token），
/// 与 <see cref="TokenEstimator"/> 的开销模型保持一致。
/// </summary>
/// <remarks>
/// 计数异常（词表未加载、病态输入等）时自动回退到 <see cref="BucketTokenEstimator"/>，
/// 保证路由决策永不被 tokenizer 阻塞。
/// <para>
/// SharpToken 的编码实例未承诺线程安全，内部对 CountTokens 加锁串行化；
/// 单请求计数为毫秒级，对路由吞吐影响可忽略。
/// </para>
/// </remarks>
public sealed class TiktokenTokenEstimator : ITokenEstimator
{
    /// <summary>
    /// 默认编码：<c>o200k_base</c>（GPT-4o 及更新一代模型的编码）。
    /// </summary>
    public const string DefaultEncodingName = "o200k_base";

    private const int TokensPerMessage = 3;

    private readonly Func<string, int> _countTokens;
    private readonly ITokenEstimator _fallback;

    /// <summary>
    /// 用指定编码初始化。编码词表在首次 <see cref="GptEncoding.GetEncoding(string)"/> 时解析并进程级缓存。
    /// </summary>
    /// <param name="encodingName">tiktoken 编码名，如 <c>o200k_base</c>、<c>cl100k_base</c>。</param>
    /// <param name="fallback">计数异常时的回退估算器，默认分桶粗估。</param>
    /// <exception cref="ArgumentException">编码名为空或不可用。</exception>
    public TiktokenTokenEstimator(string encodingName = DefaultEncodingName, ITokenEstimator? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodingName);

        // 未知编码在此处直接抛出，由启动校验（ValidateOnStart）提前暴露配置错误。
        var encoding = GptEncoding.GetEncoding(encodingName);
        var gate = new object();
        _countTokens = text =>
        {
            lock (gate)
            {
                return encoding.CountTokens(text);
            }
        };
        _fallback = fallback ?? new BucketTokenEstimator();
    }

    /// <summary>
    /// 用自定义计数委托初始化（测试或自定义词表场景）。
    /// </summary>
    /// <param name="countTokens">单段文本的 token 计数委托。</param>
    /// <param name="fallback">计数异常时的回退估算器，默认分桶粗估。</param>
    public TiktokenTokenEstimator(Func<string, int> countTokens, ITokenEstimator? fallback = null)
    {
        _countTokens = countTokens ?? throw new ArgumentNullException(nameof(countTokens));
        _fallback = fallback ?? new BucketTokenEstimator();
    }

    /// <inheritdoc />
    public int Estimate(ChatRequest request)
    {
        if (request.Messages is null || request.Messages.Count == 0)
            return 0;

        try
        {
            int contentTokens = 0;
            int messageCount = 0;

            foreach (var msg in request.Messages)
            {
                if (msg is null) continue;
                var text = msg.GetText();
                if (string.IsNullOrEmpty(text)) continue;
                messageCount++;
                contentTokens += _countTokens(text);
            }

            if (messageCount == 0) return 0;

            return contentTokens + messageCount * TokensPerMessage;
        }
        catch
        {
            // intentional-defensive: tokenizer 异常不得阻塞路由，回退分桶粗估。
            return _fallback.Estimate(request);
        }
    }

    /// <summary>
    /// 检测编码名是否可用（供启动校验）。可用时顺带预热进程级词表缓存。
    /// </summary>
    /// <param name="encodingName">tiktoken 编码名。</param>
    /// <returns>true 表示编码可加载。</returns>
    public static bool IsEncodingAvailable(string encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName)) return false;

        try
        {
            _ = GptEncoding.GetEncoding(encodingName);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

namespace OptiRouter.Compliance;

/// <summary>
/// 流式内容合规违规拦截异常。
/// </summary>
public class ComplianceViolationException : Exception
{
    public string? MatchedKeyword { get; }

    public ComplianceViolationException(string message, string? matchedKeyword = null)
        : base(message)
    {
        MatchedKeyword = matchedKeyword;
    }
}

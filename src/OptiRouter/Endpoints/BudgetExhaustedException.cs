namespace OptiRouter.Endpoints;

/// <summary>
/// 预算耗尽异常，Endpoint 层映射为 HTTP 429。
/// </summary>
public sealed class BudgetExhaustedException : Exception
{
    /// <summary>
    /// 初始化预算耗尽异常。
    /// </summary>
    /// <param name="message">错误描述。</param>
    public BudgetExhaustedException(string message) : base(message) { }
}

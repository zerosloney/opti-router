using System.Collections.ObjectModel;

namespace OptiRouter.Endpoints;

/// <summary>
/// 所有候选模型均失败的异常。
/// </summary>
public sealed class AllCandidatesFailedException : Exception
{
    /// <summary>
    /// 本次请求尝试过的模型名列表。
    /// </summary>
    public IReadOnlyList<string> AttemptedModels { get; }

    /// <summary>
    /// 最后尝试失败的模型名称。
    /// </summary>
    public string? LastModelName { get; }

    /// <summary>
    /// 最后尝试失败的状态码。
    /// </summary>
    public int? LastStatusCode { get; }

    /// <summary>
    /// 最后尝试失败的具体错误消息。
    /// </summary>
    public string? LastErrorMessage { get; }

    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="attemptedModels">尝试过的模型名列表。</param>
    public AllCandidatesFailedException(IReadOnlyList<string> attemptedModels)
    {
        ArgumentNullException.ThrowIfNull(attemptedModels);
        AttemptedModels = attemptedModels;
    }

    /// <summary>
    /// 初始化异常并指定错误消息。
    /// </summary>
    /// <param name="attemptedModels">尝试过的模型名列表。</param>
    /// <param name="message">错误消息。</param>
    public AllCandidatesFailedException(IReadOnlyList<string> attemptedModels, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(attemptedModels);
        AttemptedModels = attemptedModels;
    }

    /// <summary>
    /// 初始化异常并指定最后一次失败的具体细节。
    /// </summary>
    public AllCandidatesFailedException(
        IReadOnlyList<string> attemptedModels,
        string? lastModelName,
        int? lastStatusCode,
        string? lastErrorMessage,
        string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(attemptedModels);
        AttemptedModels = attemptedModels;
        LastModelName = lastModelName;
        LastStatusCode = lastStatusCode;
        LastErrorMessage = lastErrorMessage;
    }
}

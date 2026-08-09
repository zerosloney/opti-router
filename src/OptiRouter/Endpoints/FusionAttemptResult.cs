using OptiRouter.Clients;

namespace OptiRouter.Endpoints;

/// <summary>
/// 融合/并行尝试结果：采纳的响应（可能为 null 表示全部失败/取消）+ 失败诊断三元组。
/// 由 <see cref="FusionRouter"/> 与 <see cref="RaceOrchestrator"/> 共用，回写给调用方局部变量。
/// </summary>
public sealed record FusionAttemptResult(
    RawChatResponse? Response,
    string? LastModelName,
    int? LastStatusCode,
    string? LastErrorMessage);

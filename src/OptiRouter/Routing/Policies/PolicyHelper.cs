namespace OptiRouter.Routing;

/// <summary>
/// 策略链通用辅助：<see cref="RouterDecision"/> 的结构化事件 + 人类可读 Reason 字符串拼接。
/// <para>
/// 每个 <c>IRouterPolicy.Apply</c> 都需要在 <see cref="RouterDecision.Reason"/> 后追加一段
/// 分号分隔的说明，同时把同样的信息塞进 <see cref="RouterDecision.ReasonEvents"/> 供机器解析。
/// 重复这段样板是 24h 审查发现的"重复代码"异味（10 文件 / 36 处），统一收口到本类。
/// </para>
/// </summary>
public static class PolicyHelper
{
    /// <summary>
    /// 在 <paramref name="previous"/> 后追加一条策略事件：Reason 字符串追加 <c>"; {policy}: {detail}"</c>，
    /// ReasonEvents 追加 <c>new ReasonEvent(policy, detail)</c>。
    /// </summary>
    /// <param name="previous">上游策略链传下来的决策（可能为首个 <c>RouterEngine</c> 入口的初值）。</param>
    /// <param name="policy">策略名（kebab-case，如 <c>"failover"</c>、<c>"long-input"</c>），与代码中字符串字面量保持一致。</param>
    /// <param name="detail">本策略产生的详情文本（<b>不</b>含 <c>{policy}:</c> 前缀，由 helper 拼装）。</param>
    public static RouterDecision Append(this RouterDecision previous, string policy, string detail)
    {
        var events = new List<ReasonEvent>(previous.ReasonEvents.Count + 1);
        if (previous.ReasonEvents.Count > 0)
        {
            events.AddRange(previous.ReasonEvents);
        }
        events.Add(new ReasonEvent(policy, detail));

        return previous with
        {
            Reason = $"{previous.Reason}; {policy}: {detail}",
            ReasonEvents = events
        };
    }
}

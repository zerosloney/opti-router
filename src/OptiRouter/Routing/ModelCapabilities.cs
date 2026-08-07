namespace OptiRouter.Routing;

/// <summary>
/// 模型能力标签的语义约定与白名单。
/// </summary>
/// <remarks>
/// 单一真源：CapabilityFilterPolicy 的能力匹配与 RouterOptionsValidator 的 Tags 软校验都引用此处常量。
/// 新增能力时只改本文件，两个消费方自动同步。
/// </remarks>
public static class ModelCapabilities
{
    /// <summary>视觉输入能力（多模态 image_url）。</summary>
    public const string Vision = "vision";

    /// <summary>工具/函数调用能力（tools 数组）。</summary>
    public const string ToolUse = "tool-use";

    /// <summary>JSON 模式能力（response_format: json_object）。</summary>
    public const string JsonMode = "json-mode";

    /// <summary>
    /// 已知能力标签白名单。RouterOptionsValidator 据此对未识别 tag 发软警告（不阻断启动）。
    /// </summary>
    public static readonly IReadOnlySet<string> KnownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Vision,
        ToolUse,
        JsonMode
    };
}

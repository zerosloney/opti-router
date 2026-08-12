using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace OptiRouter.Routing;

/// <summary>
/// 提示词模版版本定义。
/// </summary>
public sealed record PromptTemplate(
    string Name,
    string Version,
    string TemplateText,
    DateTimeOffset CreatedAt);

/// <summary>
/// 提示词模版版本管理器：
/// 支持 Analyst / Outer / Verification 提示词模版的版本化注册、变量插值与动态切换。
/// </summary>
public sealed class PromptTemplateManager
{
    private readonly ConcurrentDictionary<string, PromptTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);

    public PromptTemplateManager()
    {
        // 注册内置默认版本
        Register("analyst", "v1", FusionSynthesis.DefaultAnalystPrompt);
        Register("outer", "v1", FusionSynthesis.DefaultOuterPrompt);
        Register("self_verify", "v1", ResponseConfidenceChecker.DefaultSelfVerifyPrompt);
    }

    /// <summary>
    /// 注册一个提示词模版版本。
    /// </summary>
    public void Register(string name, string version, string templateText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateText);

        string key = BuildKey(name, version);
        _templates[key] = new PromptTemplate(name, version, templateText, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 获取指定名称与版本的模版。
    /// </summary>
    public PromptTemplate? Get(string name, string version)
    {
        string key = BuildKey(name, version);
        return _templates.TryGetValue(key, out var template) ? template : null;
    }

    /// <summary>
    /// 对模版文本进行变量插值（例如 {{question}}, {{panel_answers}}）。
    /// </summary>
    public string Render(string name, string version, IDictionary<string, string> variables)
    {
        var template = Get(name, version);
        if (template is null)
            throw new KeyNotFoundException($"Prompt template '{name}:{version}' not found.");

        string text = template.TemplateText;
        if (variables is null || variables.Count == 0)
            return text;

        // 单次扫描原始模板占位符：避免逐变量顺序 Replace 把已替换值中形如 {{slot}} 的内容二次展开（跨槽注入）。
        // 缺失变量与 null 值都按空串处理。
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in variables)
            lookup[k] = v ?? string.Empty;

        return Regex.Replace(text, @"\{\{(\w+)\}\}",
            match => lookup.TryGetValue(match.Groups[1].Value, out var v) ? v : string.Empty);
    }

    private static string BuildKey(string name, string version) => $"{name}:{version}";
}

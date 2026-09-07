using System.Text.RegularExpressions;

namespace OptiRouter.Compression;

/// <summary>
/// Caveman 风格的文本压缩器。
///
/// 核心理念（参考 OmniRoute 继承自 viral caveman 项目）：
/// 「为什么用很多 token when 少 token 能 do trick？」
///
/// 策略：
/// - 客套话填充语剔除（hello, thank you, hope this helps）
/// - 英文冗余表达压缩（"in order to" → "to", "due to the fact that" → "because"）
/// - 中文冗余表达压缩（"好的好的" → "好"，"非常感谢" → "谢"）
/// - 技术文档冗余压缩（"This method is used to..." → "Used to..."）
/// - Markdown 结构保留（不破坏标题/列表/代码块格式）
/// - 最小 token 阈值：低于 50 token 不触发
///
/// 注意：与 <see cref="AdaptivePromptPruner"/> 的区别在于——Pruner 处理的是对话
/// 历史的结构压缩（折叠/去重），Caveman 处理的是单段文本的内容压缩（改写）。
/// </summary>
public sealed class CavemanCondenser : ITextCondenser
{
    // ── 英文填充语客套话（完全剔除）─────────────────────────
    private static readonly Dictionary<string, string> EnglishFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        // 开头客套
        ["hello!"] = "",
        ["hello."] = "",
        ["hi there!"] = "",
        ["hi!"] = "",
        ["hey!"] = "",
        ["good morning!"] = "",
        ["good afternoon!"] = "",
        ["good evening!"] = "",
        ["greetings!"] = "",
        ["howdy!"] = "",

        // 结尾客套
        ["hope this helps!"] = "",
        ["hope this helps."] = "",
        ["hope this helps. let me know if you have any questions!"] = "",
        ["hope this helps. let me know if you need anything else!"] = "",
        ["let me know if you have any questions!"] = "",
        ["let me know if you need anything else!"] = "",
        ["please let me know if you have any questions!"] = "",
        ["please let me know if you need anything else!"] = "",
        ["feel free to let me know if you have any questions!"] = "",
        ["please feel free to ask if you have any questions!"] = "",
        ["feel free to reach out if you have any questions!"] = "",
        ["if you have any questions, let me know!"] = "",
        ["if you need anything else, just let me know!"] = "",
        ["thanks for reading!"] = "",
        ["thank you for reading!"] = "",
        ["thanks for your time!"] = "",
        ["thank you for your time!"] = "",
        ["cheers!"] = "",
        ["best regards!"] = "",
        ["kind regards!"] = "",
        ["warm regards!"] = "",
        ["all the best!"] = "",
        ["sincerely,"] = "",

        // AI 标准前缀（无意义填充）
        ["as an ai language model, i"] = "i",
        ["as an ai assistant, i"] = "i",
        ["as a large language model, i"] = "i",
        ["as an ai, i"] = "i",
        ["as an artificial intelligence, i"] = "i",
        ["i, as an ai language model,"] = "i",
        ["i, as an ai assistant,"] = "i",
    };

    // ── 英文冗余表达压缩（词典顺序优先长匹配）──────────────
    private static readonly Dictionary<string, string> EnglishRedundancies = new(StringComparer.OrdinalIgnoreCase)
    {
        // 长度压缩
        ["in order to"] = "to",
        ["due to the fact that"] = "because",
        ["for the purpose of"] = "for",
        ["with the exception of"] = "except",
        ["at this point in time"] = "now",
        ["at that point in time"] = "then",
        ["in the event that"] = "if",
        ["on the occasion that"] = "if",
        ["in the case that"] = "if",
        ["in spite of the fact that"] = "although",
        ["despite the fact that"] = "although",
        ["owing to the fact that"] = "because",
        ["inasmuch as"] = "because",
        ["insofar as"] = "as",
        ["at the present time"] = "now",
        ["for the time being"] = "for now",
        ["until such time as"] = "until",
        ["by means of"] = "by",
        ["in the near future"] = "soon",
        ["at a later date"] = "later",
        ["in the future"] = "later",
        ["it is important to note that"] = "note:",
        ["it should be noted that"] = "note:",
        ["please note that"] = "note:",
        ["it is worth mentioning that"] = "",
        ["it is also worth noting that"] = "also:",
        ["in addition to this"] = "also",
        ["in addition to the above"] = "also",
        ["as a result of this"] = "so",
        ["as a consequence of this"] = "so",
        ["for this reason"] = "so",
        ["consequently"] = "so",
        ["on the other hand"] = "but",
        ["in contrast to this"] = "but",
        ["nevertheless"] = "but",
        ["notwithstanding"] = "but",
        ["on the contrary"] = "but",
        ["first and foremost"] = "first",
        ["each and every"] = "each",
        ["any and all"] = "any",
        ["each and all"] = "all",
        ["one and only"] = "only",
        ["true and accurate"] = "correct",
        ["final and conclusive"] = "final",
        ["null and void"] = "void",
        ["full and complete"] = "complete",
        ["new and innovative"] = "new",
        ["tried and tested"] = "tested",
        ["safe and secure"] = "secure",
        ["simple and easy"] = "easy",
        ["quick and fast"] = "fast",
        ["big and large"] = "large",
        ["small and compact"] = "compact",
        ["that is to say"] = "i.e.",
        ["in other words"] = "i.e.",
        ["to put it another way"] = "i.e.",
        ["with this in mind"] = "so",
        ["having said that"] = "but",
        ["all things considered"] = "overall",
        ["taking everything into account"] = "overall",
        ["when all is said and done"] = "overall",
        ["as a matter of fact"] = "in fact",
        ["at the end of the day"] = "overall",
        ["needless to say"] = "",
        ["it goes without saying"] = "",
        ["needless to say,"] = "",
        ["it should go without saying"] = "",
        ["as you can see"] = "",
        ["as you may know"] = "",
        ["as you are aware"] = "",
        ["as we all know"] = "",
        ["it is well known that"] = "",
        ["it is commonly known that"] = "",
        ["it is widely known that"] = "",
        ["it is generally accepted that"] = "",
        ["as stated previously"] = "as said",
        ["as mentioned earlier"] = "as mentioned",
        ["as mentioned above"] = "as mentioned",
        ["as discussed before"] = "as discussed",
        ["as outlined above"] = "as outlined",
        ["as shown above"] = "as shown",
        ["as illustrated above"] = "as shown",
        ["see the example below"] = "see below",
        ["see the example above"] = "see above",
        ["please refer to the"] = "see the",
        ["you may want to consider"] = "consider",
        ["you might want to try"] = "try",
        ["you could try"] = "try",
        ["you might also try"] = "also try",
        ["it is recommended that you"] = "you should",
        ["it is suggested that you"] = "you can",
        ["it is advisable that you"] = "you should",
        ["it is suggested to"] = "to",
        ["it is recommended to"] = "to",
        ["the following is"] = "",
        ["the aforementioned"] = "this",
        ["said and done"] = "done",
        ["first and second"] = "first, second",
        ["utilize"] = "use",
        ["utilizes"] = "uses",
        ["utilized"] = "used",
        ["utilization"] = "use",
        ["facilitate"] = "do",
        ["facilitates"] = "does",
        ["facilitated"] = "did",
        ["implement"] = "do",
        ["implementing"] = "doing",
        ["implementation"] = "setup",
        ["subsequent"] = "next",
        ["subsequently"] = "then",
        ["prior to"] = "before",
        ["in close proximity to"] = "near",
        ["in the event of"] = "on",
        ["for the purposes of"] = "for",
    };

    // ── 中文冗余表达压缩 ─────────────────────────────────
    private static readonly Dictionary<string, string> ChineseRedundancies = new()
    {
        // 开头客套
        ["好的"] = "好",
        ["好的好的"] = "好",
        ["好的，没问题"] = "好",
        ["好的，收到"] = "好",
        ["好的好的，收到"] = "好",
        ["好的，没问题的"] = "好",
        ["好的呀"] = "好",
        ["好的呢"] = "好",
        ["好嘞"] = "好",
        ["好嘞好嘞"] = "好",
        ["好哒"] = "好",
        ["好滴"] = "好",
        ["好的呀"] = "好",
        ["非常感谢"] = "谢",
        ["十分感谢"] = "谢",
        ["万分感谢"] = "谢",
        ["非常感谢你"] = "谢",
        ["非常感谢您的"] = "谢",
        ["谢谢你的"] = "谢",
        ["感谢你的"] = "谢",
        ["好的，让我"] = "让我",
        ["好的，我来"] = "我来",
        ["好的，我们先"] = "先",
        ["好的，下面"] = "下面",
        ["好的，首先"] = "首先",

        // 结尾客套
        ["希望对您有帮助"] = "",
        ["希望对您有所帮助"] = "",
        ["希望对您有帮助的话"] = "",
        ["希望对您有帮助！"] = "",
        ["希望对您有所帮助！"] = "",
        ["如果有疑问随时问我"] = "",
        ["如果有疑问请随时问我"] = "",
        ["有任何问题随时问我"] = "",
        ["有任何问题请随时问我"] = "",
        ["如有疑问请随时联系我"] = "",
        ["谢谢观看"] = "",
        ["感谢观看"] = "",
        ["感谢阅读"] = "",
        ["感谢你的阅读"] = "",
        ["感谢您抽出时间阅读"] = "",
        ["感谢您的耐心阅读"] = "",
        ["以上"] = "",
        ["以上是全部内容"] = "",
        ["以上就是全部内容"] = "",
        ["以上内容"] = "",
        ["以上就是"] = "",

        // 冗余表达
        ["首先第一步"] = "第一步",
        ["第一步首先"] = "第一步",
        ["首先先"] = "先",
        ["然后再"] = "再",
        ["接着然后"] = "再",
        ["那么下面"] = "下面",
        ["那么现在"] = "现在",
        ["那么就"] = "就",
        ["然后就"] = "就",
        ["主要是用于"] = "用于",
        ["主要的作用是"] = "作用是",
        ["主要的功能是"] = "功能是",
        ["主要特点包括"] = "特点：",
        ["主要特性包括"] = "特性：",
        ["主要优势包括"] = "优势：",
        ["也就是说"] = "即",
        ["换句话说"] = "即",
        ["也就是说说"] = "即",
        ["也就是说就是"] = "即",
        ["严格意义上来说"] = "严格来说",
        ["从本质上来说"] = "本质上",
        ["从技术角度来说"] = "技术上",
        ["从实际角度来看"] = "实际上",
        ["就实际情况而言"] = "实际上",
        ["考虑到这种情况"] = "因此",
        ["鉴于此种情况"] = "因此",
        ["由于这个原因"] = "因此",
        ["综合来看"] = "综上",
        ["总的来说"] = "综上",
        ["总体来说"] = "综上",
        ["总体来看"] = "综上",
        ["经过分析后"] = "分析后",
        ["经过测试后"] = "测试后",
        ["经过验证后"] = "验证后",
        ["经过仔细分析"] = "分析",
        ["经过详细说明"] = "说明",
        ["需要注意的是"] = "注意：",
        ["需要特别注意的是"] = "注意：",
        ["特别需要注意的是"] = "注意：",
        ["强烈建议"] = "建议",
        ["强烈推荐"] = "推荐",
        ["强烈推荐使用"] = "推荐用",
        ["非常推荐"] = "推荐",
        ["极力推荐"] = "推荐",
        ["不妨可以"] = "可以",
        ["可以选择"] = "选",
        ["可以使用"] = "用",
        ["可以考虑"] = "考虑",
        ["我们来看一下"] = "看",
        ["让我们来看一下"] = "看",
        ["下面我们来看一下"] = "看",
        ["让我们先来看"] = "先看",
        ["下面我们来看"] = "看",
        ["下面我们先来看"] = "先看",
    };

    // ── 技术文档精简模式 ─────────────────────────────────
    private static readonly Regex TechBoilerplateRegex = new(
        @"^this (method|function|class|interface|module|component|service)\s+is\s+used\s+to\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReturnsDescriptionRegex = new(
        @"^returns?[\s:]+the\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DocCommentParamRegex = new(
        @"^@param\s+\w+\s*[-:]?\s*",
        RegexOptions.Compiled);

    // ── 保护区域（不压缩内部）────────────────────────────
    private static readonly Regex CodeBlockRegex = new(
        @"```[\s\S]*?```|`[^`\n]+`",
        RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FilePathRegex = new(
        @"(?:^|[\s])(/[a-zA-Z0-9_./\\-]+|[a-zA-Z]:\\[^\s]+|\b[a-zA-Z]:/[^\s]+)",
        RegexOptions.Compiled);

    private readonly int _minTokenThreshold;
    private readonly bool _compressEnglish;
    private readonly bool _compressChinese;

    /// <summary>
    /// 初始化压缩器。
    /// </summary>
    /// <param name="minTokenThreshold">最小 token 数阈值，低于此值不触发压缩（0=不限制）</param>
    /// <param name="compressEnglish">是否压缩英文冗余表达（默认 true）</param>
    /// <param name="compressChinese">是否压缩中文冗余表达（默认 true）</param>
    public CavemanCondenser(
        int minTokenThreshold = 50,
        bool compressEnglish = true,
        bool compressChinese = true)
    {
        _minTokenThreshold = minTokenThreshold;
        _compressEnglish = compressEnglish;
        _compressChinese = compressChinese;
    }

    /// <inheritdoc />
    public TextCondensationResult Condense(string text, TextCondensationOptions? options = null)
    {
        options ??= new TextCondensationOptions();
        if (!options.Enabled || string.IsNullOrWhiteSpace(text))
        {
            return new TextCondensationResult(text, 0, 0, 0.0, false, "disabled_or_empty");
        }

        int origLen = text.Length;

        // 阈值保护
        if (_minTokenThreshold > 0 && origLen < _minTokenThreshold * 4) // ~4 chars/token 估算
        {
            return new TextCondensationResult(text, origLen, origLen, 0.0, false, $"below_min_threshold_{origLen}");
        }

        // 提取并保护代码块、URL、文件路径（记录原文，压缩后原样还原）
        var protectedRanges = new List<(string Placeholder, string Original)>();
        var working = text;

        working = ProtectRegions(working, CodeBlockRegex, protectedRanges, "__CODE_BLOCK_");
        working = ProtectRegions(working, UrlRegex, protectedRanges, "__URL_");
        working = ProtectRegions(working, FilePathRegex, protectedRanges, "__FPATH_");

        // ── 英文压缩 ─────────────────────────────────────
        if (_compressEnglish)
        {
            working = ApplyFillerAndRedundancyDictionaries(working, EnglishFillers);
            working = ApplyFillerAndRedundancyDictionaries(working, EnglishRedundancies);

            // 技术文档 boilerplate
            working = TechBoilerplateRegex.Replace(working, "used to ");
            working = ReturnsDescriptionRegex.Replace(working, "returns ");
            working = DocCommentParamRegex.Replace(working, "@param ");
        }

        // ── 中文压缩 ─────────────────────────────────────
        if (_compressChinese)
        {
            working = ApplyFillerAndRedundancyDictionaries(working, ChineseRedundancies);
        }

        // ── 通用清理 ─────────────────────────────────────
        // 连续标点压缩
        working = Regex.Replace(working, @"[.!?]{3,}", "…");
        // 多余空格
        working = Regex.Replace(working, @"[ \t]{2,}", " ");
        // 开头结尾空白
        working = working.Trim();
        // 清理孤立标点
        working = Regex.Replace(working, @"\(\s*\)", "");
        working = Regex.Replace(working, @"[\[\]]\s*[\[\]]", "");

        // 还原保护区域（替换回原始内容）
        foreach (var (placeholder, original) in protectedRanges)
        {
            working = working.Replace(placeholder, original);
        }

        int saved = origLen - working.Length;
        double reduction = origLen > 0 ? Math.Max(0.0, 1.0 - ((double)working.Length / origLen)) : 0.0;

        return new TextCondensationResult(
            Result: working,
            OriginalLength: origLen,
            CompressedLength: working.Length,
            ReductionRatio: reduction,
            WasCompressed: saved > 0,
            StrategySummary: $"caveman({saved}_chars_saved)");
    }

    private static string ProtectRegions(
        string text,
        Regex pattern,
        List<(string Placeholder, string Original)> ranges,
        string prefix)
    {
        int counter = 0;
        return pattern.Replace(text, match =>
        {
            string placeholder = $"{prefix}{counter++}__";
            ranges.Add((placeholder, match.Value));
            return placeholder;
        });
    }

    private static string ApplyFillerAndRedundancyDictionaries(
        string text,
        Dictionary<string, string> dict)
    {
        // 按 key 长度降序排列（优先匹配长表达）
        var ordered = dict.OrderByDescending(kv => kv.Key.Length);
        foreach (var (from, to) in ordered)
        {
            text = text.Contains(from, StringComparison.OrdinalIgnoreCase)
                ? Regex.Replace(text, Regex.Escape(from), to, RegexOptions.IgnoreCase)
                : text;
        }
        return text;
    }
}

/// <summary>
/// 文本压缩结果。
/// </summary>
public record TextCondensationResult(
    string Result,
    int OriginalLength,
    int CompressedLength,
    double ReductionRatio,
    bool WasCompressed,
    string StrategySummary);

/// <summary>
/// 文本压缩选项。
/// </summary>
public class TextCondensationOptions
{
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// 文本压缩器接口。用于对话内容和工具输出的内容级压缩。
/// </summary>
public interface ITextCondenser
{
    TextCondensationResult Condense(string text, TextCondensationOptions? options = null);
}

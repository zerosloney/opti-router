using System.Text;
using System.Text.RegularExpressions;

namespace OptiRouter.Compression;

/// <summary>
/// RTK 风格的 Shell/Bash 输出压缩器。
/// 专用于压缩工具执行结果（如 bash 输出、git diff、npm install 等），节省模型输入 token。
///
/// 策略（参考 OmniRoute RTK engine）：
/// - 剥离 ANSI 转义码
/// - 剔除进度条（`████░░░░ 40%` 风格）
/// - 压缩重复分隔线和日志表头
/// - 截断超长行但保留语义关键行
/// - 移除命令回显和空命令输出
/// - 保留代码块（不做破坏性修改）
/// </summary>
public sealed class RtkShellOutputCompressor : IOutputCompressor
{
    private static readonly Regex AnsiRegex = new(
        @"\x1b\[[0-9;]*[a-zA-Z]|\x1b\][^\x07]*\x07|\x1b[()][AB012]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressBarRegex = new(
        @"^.*(?:[▓░█◐◔●○#=~+*]|[━┃]|[━┃])\s*\d{1,3}(?:\.\d)?%\s*(?:\|.*\|)?.*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiColorCodeRegex = new(
        @"\x1b\[(?:[0-9;]+)?m",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RepeatedDashLineRegex = new(
        @"^[-━—]{10,}\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex GitStatTotalRegex = new(
        @"^\s*\d+\s+file(?:s)?\s+changed(?:,\s*\d+\s+insertion(?:s)?(?:,\s*\d+\s+deletion(?:s)?)?)?.*$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex NpmInstallSummaryRegex = new(
        @"^(added|removed|changed)\s+\d+\s+package",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly Regex EmptyOrBlankLineRegex = new(
        @"^(?:\s*[\r\n]*)+$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // 纯路径罗列行：整行仅路径字符且至少含一个路径分隔符（/ 或 \）——
    // lookahead 排除 "success"/"42" 这类普通单词行（无分隔符不是路径）。
    private static readonly Regex FilePathOnlyRegex = new(
        @"^(?=\S*[/\\])[a-zA-Z0-9_./\\-]+$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly int _maxLineLength;
    private readonly int _maxOutputLines;

    /// <summary>
    /// 初始化压缩器。
    /// </summary>
    /// <param name="maxLineLength">单行最大字符数，超过则截断（0=不截断）</param>
    /// <param name="maxOutputLines">总行数上限（0=不限制）</param>
    public RtkShellOutputCompressor(int maxLineLength = 512, int maxOutputLines = 200)
    {
        _maxLineLength = maxLineLength;
        _maxOutputLines = maxOutputLines;
    }

    /// <inheritdoc />
    public OutputCompressionResult Compress(string rawOutput, OutputCompressionOptions? options = null)
    {
        options ??= new OutputCompressionOptions();
        if (!options.Enabled || string.IsNullOrWhiteSpace(rawOutput))
        {
            return new OutputCompressionResult(rawOutput, 0, 0, 0.0, false, "disabled_or_empty");
        }

        int origLength = rawOutput.Length;
        var lines = rawOutput.Split('\n');

        var kept = new List<string>(lines.Length);
        int skipCount = 0;
        string? lastKept = null;
        bool inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            string line = rawLine;

            // 跟踪代码块（保护内部内容）
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inCodeBlock = !inCodeBlock;

            // ── 过滤阶段 ──────────────────────────────────────
            if (!inCodeBlock)
            {
                // 1. 剥离 ANSI
                line = AnsiColorCodeRegex.Replace(line, string.Empty);

                // 2. 剔除空行（保留结构空行：代码块内、列表项间）
                if (string.IsNullOrWhiteSpace(line) && kept.Count > 0)
                {
                    // 保留结构空行（两个非空行之间）
                    if (lastKept != null && lastKept != "\n")
                        kept.Add(line);
                    continue;
                }

                // 3. 剔除进度条
                if (ProgressBarRegex.IsMatch(line))
                {
                    skipCount++;
                    continue;
                }

                // 4. 合并重复分隔线（连续多条 `--`/`━━` 只留一条）
                if (RepeatedDashLineRegex.IsMatch(line))
                {
                    if (kept.Count > 0 && RepeatedDashLineRegex.IsMatch(kept[^1]))
                        continue;
                    line = "──"; // 标准化为通用分隔符
                }

                // 5. 压缩 git diff stat 摘要行（只保留汇总）
                if (GitStatTotalRegex.IsMatch(line))
                {
                    if (kept.Count > 0 && GitStatTotalRegex.IsMatch(kept[^1]))
                        continue; // 去重
                    line = "[git diff summary]";
                }

                // 6. 压缩 npm/pnpm install 摘要
                if (NpmInstallSummaryRegex.IsMatch(line))
                {
                    skipCount++;
                    continue; // npm install 详细日志全部跳过
                }

                // 7. 折叠纯路径罗列行（文件罗列太长，摘要即可）：保留首条内容，后续递增计数
                if (kept.Count > 0
                    && FilePathOnlyRegex.IsMatch(line.Trim())
                    && line.Length < 120
                    && !line.Contains(':')) // 排除 "path/to/file: content"
                {
                    if (!kept[^1].StartsWith("[+", StringComparison.Ordinal))
                        kept[^1] = $"[+1 path] {kept[^1]}";
                    else if (int.TryParse(kept[^1].AsSpan(1, kept[^1].IndexOf(' ') - 1), out int collapsed))
                        kept[^1] = $"[+{collapsed + 1} paths]";
                    continue;
                }
            }

            // ── 截断阶段 ──────────────────────────────────────
            if (_maxLineLength > 0 && line.Length > _maxLineLength)
            {
                // 保留前 _maxLineLength 字符，追加截断标记
                line = line.AsSpan(0, _maxLineLength).ToString() + "… [truncated]";
            }

            kept.Add(line);
            lastKept = line;

            // 行数上限保护
            if (_maxOutputLines > 0 && kept.Count >= _maxOutputLines)
            {
                kept.Add($"… [output truncated, {lines.Length - kept.Count} more lines]");
                break;
            }
        }

        string result = string.Join('\n', kept);
        int savedBytes = origLength - result.Length;
        double reduction = origLength > 0 ? Math.Max(0.0, 1.0 - ((double)result.Length / origLength)) : 0.0;

        return new OutputCompressionResult(
            Result: result,
            OriginalLength: origLength,
            CompressedLength: result.Length,
            ReductionRatio: reduction,
            WasCompressed: savedBytes > 0,
            StrategySummary: $"rtk_shell({skipCount}_skipped_lines)");
    }
}

/// <summary>
/// 输出压缩结果。
/// </summary>
/// <param name="Result">压缩后文本。</param>
/// <param name="OriginalLength">原始字节数。</param>
/// <param name="CompressedLength">压缩后字节数。</param>
/// <param name="ReductionRatio">压缩比（0.0-1.0）。</param>
/// <param name="WasCompressed">是否实际发生了压缩。</param>
/// <param name="StrategySummary">使用的策略摘要。</param>
public record OutputCompressionResult(
    string Result,
    int OriginalLength,
    int CompressedLength,
    double ReductionRatio,
    bool WasCompressed,
    string StrategySummary);

/// <summary>
/// 输出压缩选项。
/// </summary>
public class OutputCompressionOptions
{
    /// <summary>
    /// 是否启用压缩。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 最小压缩收益阈值（字节）。低于此值不做压缩，避免无谓处理。
    /// </summary>
    public int MinBytesToTrigger { get; set; } = 100;
}

/// <summary>
/// 输出压缩器接口。用于工具执行结果和模型响应输出的压缩。
/// </summary>
public interface IOutputCompressor
{
    OutputCompressionResult Compress(string rawOutput, OutputCompressionOptions? options = null);
}

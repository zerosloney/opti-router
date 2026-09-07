using System.Text;
using OptiRouter.Compression;
using Xunit;

namespace OptiRouter.Tests.Compression;

public sealed class RtkShellOutputCompressorTests
{
    private readonly RtkShellOutputCompressor _compressor = new();

    [Fact]
    public void Compress_PlainWordLines_AreKept()
    {
        // 修复前：FilePathOnlyRegex 无分隔符要求，"success"/"42"/"true"/"error" 等普通
        // 单词行被当作路径折叠，整段输出只剩 "[+N paths]" 字面量。
        var output = "build finished\nsuccess\n42\ntrue\nerror";

        var result = _compressor.Compress(output);

        Assert.Contains("success", result.Result);
        Assert.Contains("42", result.Result);
        Assert.Contains("error", result.Result);
    }

    [Fact]
    public void Compress_PathListing_CollapsedWithRealCount()
    {
        var output = "src/OptiRouter/Program.cs\nsrc/OptiRouter/Endpoints/FusionRouter.cs\nsrc/OptiRouter/Models/Foo.cs";

        var result = _compressor.Compress(output);

        // 折叠语义：整段路径罗列收敛为一个计数标记（2 条被折叠），后续路径不再逐条占用 token
        Assert.Contains("[+2 paths]", result.Result);
        Assert.DoesNotContain("FusionRouter.cs", result.Result);
        Assert.True(result.Result.Length < output.Length);
    }

    [Fact]
    public void Compress_StripsAnsiCodes()
    {
        var output = "\x1b[32mOK\x1b[0m all tests passed";

        var result = _compressor.Compress(output);

        Assert.DoesNotContain("\x1b[", result.Result);
        Assert.Contains("all tests passed", result.Result);
    }

    [Fact]
    public void Compress_RemovesProgressBarLines()
    {
        var output = "Downloading...\n████████░░░░ 80% |████ | 40/50\nDone";

        var result = _compressor.Compress(output);

        Assert.Contains("Done", result.Result);
        Assert.DoesNotContain("80%", result.Result);
    }

    [Fact]
    public void Compress_TruncatesLongLines()
    {
        var compressor = new RtkShellOutputCompressor(maxLineLength: 100);
        var longLine = "x" + new string('a', 500);

        var result = compressor.Compress(longLine);

        Assert.Contains("[truncated]", result.Result);
        Assert.True(result.Result.Length < 200);
    }

    [Fact]
    public void Compress_DisabledOrEmpty_ReturnsOriginal()
    {
        Assert.Equal("", _compressor.Compress("", new OutputCompressionOptions { Enabled = true }).Result);

        var output = "some output";
        var result = _compressor.Compress(output, new OutputCompressionOptions { Enabled = false });
        Assert.Equal(output, result.Result);
        Assert.False(result.WasCompressed);
    }
}

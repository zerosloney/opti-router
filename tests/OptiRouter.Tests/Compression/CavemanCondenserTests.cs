using OptiRouter.Compression;
using Xunit;

namespace OptiRouter.Tests.Compression;

public sealed class CavemanCondenserTests
{
    private readonly CavemanCondenser _condenser = new(minTokenThreshold: 0);

    [Fact]
    public void Condense_ProtectsUrlAndCodeBlock_RestoresOriginalContent()
    {
        // 修复前：保护区域还原时占位符被替换成 "URL_0"/"CODE_BLOCK_0" 字面标记，原文永久丢失。
        var text = "Please see https://example.com/docs/page in order to proceed. " +
                   "Code: `Console.WriteLine(\"hello\")` done.";

        var result = _condenser.Condense(text);

        Assert.Contains("https://example.com/docs/page", result.Result);
        Assert.Contains("Console.WriteLine(\"hello\")", result.Result);
        // 冗余表达仍被压缩（保护区域之外的部分正常工作）
        Assert.DoesNotContain("in order to", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to proceed", result.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Condense_EnglishFillersAndRedundancy_Compressed()
    {
        var text = "Hello! It is important to note that this is utilized in order to work. Hope this helps!";

        var result = _condenser.Condense(text);

        Assert.True(result.WasCompressed);
        Assert.DoesNotContain("Hello!", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hope this helps", result.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("utilized", result.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Condense_ChineseRedundancy_Compressed()
    {
        var text = "好的好的，首先第一步我们来看一下这个方案，希望对您有帮助。";

        var result = _condenser.Condense(text);

        Assert.True(result.WasCompressed);
        Assert.DoesNotContain("好的好的", result.Result);
        Assert.DoesNotContain("首先第一步", result.Result);
        Assert.DoesNotContain("希望对您有帮助", result.Result);
    }

    [Fact]
    public void Condense_BelowMinTokenThreshold_ReturnsOriginal()
    {
        var condenser = new CavemanCondenser(minTokenThreshold: 50);
        var text = "short text with in order to";

        var result = condenser.Condense(text);

        Assert.False(result.WasCompressed);
        Assert.Equal(text, result.Result);
    }

    [Fact]
    public void Condense_Disabled_ReturnsOriginal()
    {
        var text = "Hello! This is a rather long text full of filler words to be compressed. Hope this helps!";

        var result = _condenser.Condense(text, new TextCondensationOptions { Enabled = false });

        Assert.False(result.WasCompressed);
        Assert.Equal(text, result.Result);
    }
}

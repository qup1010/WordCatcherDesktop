using WordCatcher.Core.Services;

namespace WordCatcher.Core.Tests;

public class OcrTextFormatterTests
{
    [Fact]
    public void JoinLinesPreservesHyphenatedWordsAndParagraphBreaks()
    {
        var result = OcrTextFormatter.JoinLines([
            "  A well-",
            "known interface ",
            "",
            "Next paragraph. "
        ]);

        Assert.Equal("A well-known interface\nNext paragraph.", result);
    }

    [Fact]
    public void JoinLinesAvoidsExtraSpacesBetweenCjkIncludingSupplementaryPlane()
    {
        var result = OcrTextFormatter.JoinLines([
            "这是一段跨行𠀀",
            "中文识别文本"
        ]);

        Assert.Equal("这是一段跨行𠀀中文识别文本", result);
    }

    [Fact]
    public void JoinLinesKeepsSpaceBetweenCjkAndLatinBoundary()
    {
        var result = OcrTextFormatter.JoinLines([
            "使用",
            "WordCatcher",
            "取词"
        ]);

        Assert.Equal("使用 WordCatcher 取词", result);
    }
}

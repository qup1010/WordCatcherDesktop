using WordCatcher.Core.Helpers;

namespace WordCatcher.Core.Tests;

public class TextLanguageDetectorTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("  ", false, false)]
    [InlineData("42?!", false, false)]
    [InlineData("你好，世界！", true, false)]
    [InlineData("㐀", true, false)]
    [InlineData("Hello 中文", true, false)]
    [InlineData("漢字かな", false, false)]
    [InlineData("漢字カナ", false, false)]
    [InlineData("漢字한글", false, false)]
    [InlineData("Hello, world! 42", false, true)]
    [InlineData("https://example.com", false, false)]
    [InlineData("Aᄒ", false, false)]
    [InlineData("A𠀀", false, false)]
    public void DetectsChineseAndEnglish(string? text, bool chinese, bool english)
    {
        Assert.Equal(chinese, TextLanguageDetector.IsChineseText(text));
        Assert.Equal(english, TextLanguageDetector.IsEnglishText(text));
    }

    [Theory]
    [InlineData("你好", " French ", true, "English")]
    [InlineData("你好", " French ", false, "French")]
    [InlineData("hello", " Japanese ", true, "Japanese")]
    [InlineData("漢字かな", "", true, "简体中文")]
    [InlineData(null, null, true, "简体中文")]
    [InlineData("hello", "  ", false, "简体中文")]
    public void ResolvesTargetLanguage(string? text, string? configured, bool auto, string expected)
    {
        Assert.Equal(expected, TextLanguageDetector.ResolveEffectiveTargetLanguage(text, configured, auto));
    }
}

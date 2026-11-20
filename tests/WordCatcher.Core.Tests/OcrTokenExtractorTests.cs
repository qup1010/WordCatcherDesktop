using WordCatcher.Core.Services;

namespace WordCatcher.Core.Tests;

public class OcrTokenExtractorTests
{
    [Fact]
    public void ExtractsEnglishWordsWithTheirOriginalSpans()
    {
        Assert.Equal(
            [new OcrWordToken("Read", 0, 4), new("this", 5, 4), new("sentence", 10, 8)],
            OcrTokenExtractor.ExtractWords("Read this sentence."));
    }

    [Fact]
    public void PreservesInternalPunctuationAndLatinAccents()
    {
        Assert.Equal(
            ["don't", "well-known", "l’été", "Cafe\u0301"],
            OcrTokenExtractor.ExtractWords("'don't', (well-known); 123 — l’été! Cafe\u0301.").Select(token => token.Text));
    }

    [Fact]
    public void RepeatedWordsKeepDistinctUtf16Offsets()
    {
        Assert.Equal(
            [new OcrWordToken("go", 3, 2), new("go", 7, 2)],
            OcrTokenExtractor.ExtractWords("😀 go, go!"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("中文，标点和 123！")]
    public void ReturnsNoTokensWithoutLatinLetters(string text)
    {
        Assert.Empty(OcrTokenExtractor.ExtractWords(text));
    }

    [Fact]
    public void LimitsRepeatedWordsToTheFirstFortyOccurrences()
    {
        var tokens = OcrTokenExtractor.ExtractWords(string.Concat(Enumerable.Repeat("go ", 100)));

        Assert.Equal(40, tokens.Count);
        Assert.Equal(new OcrWordToken("go", 117, 2), tokens[^1]);
    }
}

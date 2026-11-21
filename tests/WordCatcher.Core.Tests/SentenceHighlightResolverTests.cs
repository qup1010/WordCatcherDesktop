using WordCatcher.Core.Algorithms;
using Xunit;

namespace WordCatcher.Core.Tests;

public class SentenceHighlightResolverTests
{
    [Fact]
    public void UsesPreferredOffsetWhenMultipleOccurrencesExist()
    {
        const string sentence = "A representation of X at the present moment.";
        var offset = sentence.LastIndexOf("present", System.StringComparison.Ordinal);

        var parts = SentenceHighlightResolver.Split(sentence, ["present"], offset);

        Assert.True(parts.HasHighlight);
        Assert.Equal("A representation of X at the ", parts.Prefix);
        Assert.Equal("present", parts.Highlight);
        Assert.Equal(" moment.", parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void PrefersWholeWordBoundaryOverSubstringWhenNoOffsetGiven()
    {
        const string sentence = "A representation of X at the present moment.";

        var parts = SentenceHighlightResolver.Split(sentence, ["present"]);

        Assert.True(parts.HasHighlight);
        Assert.Equal("A representation of X at the ", parts.Prefix);
        Assert.Equal("present", parts.Highlight);
        Assert.Equal(" moment.", parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void MatchesCaseInsensitivelyAndPreservesOriginalCase()
    {
        const string sentence = "Semantic aspect (mAP@10)";

        var parts = SentenceHighlightResolver.Split(sentence, ["semantic"]);

        Assert.True(parts.HasHighlight);
        Assert.Equal(string.Empty, parts.Prefix);
        Assert.Equal("Semantic", parts.Highlight);
        Assert.Equal(" aspect (mAP@10)", parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void CollapsesWhitespaceForMultiWordPhrases()
    {
        const string sentence = "We can make   up the difference.";

        var parts = SentenceHighlightResolver.Split(sentence, ["make up"]);

        Assert.True(parts.HasHighlight);
        Assert.Equal("We can ", parts.Prefix);
        Assert.Equal("make   up", parts.Highlight);
        Assert.Equal(" the difference.", parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void MatchesInflectedAndIrregularEnglishForms()
    {
        const string inflectedSentence = "The team is developing a new protocol.";
        var inflected = SentenceHighlightResolver.Split(inflectedSentence, ["develop"]);

        Assert.True(inflected.HasHighlight);
        Assert.Equal("The team is ", inflected.Prefix);
        Assert.Equal("developing", inflected.Highlight);
        Assert.Equal(" a new protocol.", inflected.Suffix);
        Assert.Equal(inflectedSentence, inflected.Prefix + inflected.Highlight + inflected.Suffix);

        const string irregularSentence = "They went home early.";
        var irregular = SentenceHighlightResolver.Split(irregularSentence, ["go"]);

        Assert.True(irregular.HasHighlight);
        Assert.Equal("They ", irregular.Prefix);
        Assert.Equal("went", irregular.Highlight);
        Assert.Equal(" home early.", irregular.Suffix);
        Assert.Equal(irregularSentence, irregular.Prefix + irregular.Highlight + irregular.Suffix);
    }

    [Fact]
    public void FallsBackThroughOrderedCandidates()
    {
        const string sentence = "The resilient team recovered quickly.";

        var parts = SentenceHighlightResolver.Split(sentence, [null, "   ", "nonexistent", "resilient"]);

        Assert.True(parts.HasHighlight);
        Assert.Equal("The ", parts.Prefix);
        Assert.Equal("resilient", parts.Highlight);
        Assert.Equal(" team recovered quickly.", parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void MatchesNonAsciiTargetWords()
    {
        const string sentence = "这个语境记录非常清晰。";

        var parts = SentenceHighlightResolver.Split(sentence, ["语境"]);

        Assert.True(parts.HasHighlight);
        Assert.Equal("这个", parts.Prefix);
        Assert.Equal("语境", parts.Highlight);
        Assert.Equal("记录非常清晰。", parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void ReturnsWholeSentenceAsPrefixWhenTargetNotFound()
    {
        const string sentence = "An unrelated example sentence.";

        var parts = SentenceHighlightResolver.Split(sentence, ["semantic"]);

        Assert.False(parts.HasHighlight);
        Assert.Equal(sentence, parts.Prefix);
        Assert.Equal(string.Empty, parts.Highlight);
        Assert.Equal(string.Empty, parts.Suffix);
        Assert.Equal(sentence, parts.Prefix + parts.Highlight + parts.Suffix);
    }

    [Fact]
    public void HandlesEmptySentenceAndEmptyCandidates()
    {
        var emptySentence = SentenceHighlightResolver.Split(string.Empty, ["semantic"]);
        Assert.False(emptySentence.HasHighlight);
        Assert.Equal(string.Empty, emptySentence.Prefix);
        Assert.Equal(string.Empty, emptySentence.Highlight);
        Assert.Equal(string.Empty, emptySentence.Suffix);

        const string sentence = "Semantic aspect (mAP@10)";
        var emptyCandidates = SentenceHighlightResolver.Split(sentence, [null, "  "]);
        Assert.False(emptyCandidates.HasHighlight);
        Assert.Equal(sentence, emptyCandidates.Prefix);
        Assert.Equal(string.Empty, emptyCandidates.Highlight);
        Assert.Equal(string.Empty, emptyCandidates.Suffix);
        Assert.Equal(sentence, emptyCandidates.Prefix + emptyCandidates.Highlight + emptyCandidates.Suffix);
    }
}

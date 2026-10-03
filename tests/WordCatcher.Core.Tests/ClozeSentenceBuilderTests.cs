using WordCatcher.Core.Algorithms;
using Xunit;

namespace WordCatcher.Core.Tests;

public class ClozeSentenceBuilderTests
{
    [Fact]
    public void ReplacesTargetWithClozeBlank()
    {
        var outStr = ClozeSentenceBuilder.BuildClozeSentence("He was devastated by the news.", "devastated", 7);
        Assert.Equal("He was <span class=\"wc-blank\">[&nbsp;?&nbsp;]</span> by the news.", outStr);
    }

    [Fact]
    public void RespectsOffsetWhenMultipleOccurrencesExist()
    {
        const string sentence = "A representation of X at the present moment.";
        var offset = sentence.LastIndexOf("present", System.StringComparison.Ordinal);

        var outStr = ClozeSentenceBuilder.BuildClozeSentence(sentence, "present", offset);
        Assert.Equal("A representation of X at the <span class=\"wc-blank\">[&nbsp;?&nbsp;]</span> moment.", outStr);
        Assert.Contains("representation", outStr);
    }

    [Fact]
    public void FallsBackToSearchWhenNoOffsetGiven()
    {
        var outStr = ClozeSentenceBuilder.BuildClozeSentence("He was devastated by the news.", "devastated");
        Assert.Contains("wc-blank", outStr);
    }

    [Fact]
    public void FallsBackToSearchWhenOffsetIsIncorrect()
    {
        var outStr = ClozeSentenceBuilder.BuildClozeSentence("He was devastated by the news.", "devastated", 999);
        Assert.Equal("He was <span class=\"wc-blank\">[&nbsp;?&nbsp;]</span> by the news.", outStr);
    }

    [Fact]
    public void HandlesCaseInsensitivity()
    {
        var outStr = ClozeSentenceBuilder.BuildClozeSentence("Devastated, he left the room.", "devastated", -1);
        Assert.Contains("wc-blank", outStr);
        Assert.Contains(", he left the room.", outStr);
    }

    [Fact]
    public void ReturnsOriginalSentenceWhenNotFound()
    {
        const string sentence = "A completely unrelated sentence.";
        Assert.Equal(sentence, ClozeSentenceBuilder.BuildClozeSentence(sentence, "devastated", -1));
    }

    [Fact]
    public void EscapesHtmlCharacters()
    {
        var outStr = ClozeSentenceBuilder.EscapeHtml("Use <script>alert(1)</script> & carefully.");
        Assert.Equal("Use &lt;script&gt;alert(1)&lt;/script&gt; &amp; carefully.", outStr);
    }
}

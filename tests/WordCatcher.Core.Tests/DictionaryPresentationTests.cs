using WordCatcher.Core.Models;

namespace WordCatcher.Core.Tests;

public class DictionaryPresentationTests
{
    [Fact]
    public void DetailsAvoidRepeatedExplanationAndKeepExtraExamplesAvailable()
    {
        var meaning = new DictMeaning("id", "core", "", "解释", [new("First", "第一句"), new("Second", "第二句")]) { DisplayNumber = 2 };
        Assert.Null(meaning.DetailExplanation);
        Assert.Equal("2. 解释", meaning.DisplayHeading);
        Assert.Single(meaning.FirstExamples);
        Assert.Equal("Second", Assert.Single(meaning.AdditionalExamples).Text);
        Assert.True(meaning.HasAdditionalExamples);
        Assert.Equal("更多例句（1）", meaning.AdditionalExamplesLabel);
        Assert.Equal("进一步解释", (meaning with { ShortGloss = "简义", LearnerExplanation = "进一步解释" }).DetailExplanation);
        Assert.Equal("n. · 专名", new DictPosGroup("n.", null, [], true).DisplayPos);
    }
}

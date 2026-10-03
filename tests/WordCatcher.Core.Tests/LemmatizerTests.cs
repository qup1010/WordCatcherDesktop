using System.Linq;
using WordCatcher.Core.Algorithms;
using Xunit;

namespace WordCatcher.Core.Tests;

public class LemmatizerTests
{
    [Fact]
    public void HandlesExactWords()
    {
        var candidates1 = Lemmatizer.GetCandidates("cat");
        Assert.Contains("cat", candidates1);

        var candidates2 = Lemmatizer.GetCandidates("Book");
        Assert.Contains("book", candidates2);
    }

    [Fact]
    public void HandlesPluralsAndThirdPersonSingulars()
    {
        Assert.Contains("cat", Lemmatizer.GetCandidates("cats"));
        Assert.Contains("city", Lemmatizer.GetCandidates("cities"));
        Assert.Contains("box", Lemmatizer.GetCandidates("boxes"));
        Assert.Contains("wolf", Lemmatizer.GetCandidates("wolves"));
        Assert.Contains("knife", Lemmatizer.GetCandidates("knives"));
    }

    [Fact]
    public void HandlesPastTenseAndParticiples()
    {
        Assert.Contains("devastate", Lemmatizer.GetCandidates("devastated"));
        Assert.Contains("walk", Lemmatizer.GetCandidates("walked"));
        Assert.Contains("study", Lemmatizer.GetCandidates("studied"));
        Assert.Contains("stop", Lemmatizer.GetCandidates("stopped"));
    }

    [Fact]
    public void HandlesContinuousTenses()
    {
        Assert.Contains("run", Lemmatizer.GetCandidates("running"));
        Assert.Contains("make", Lemmatizer.GetCandidates("making"));
        Assert.Contains("read", Lemmatizer.GetCandidates("reading"));
        Assert.Contains("die", Lemmatizer.GetCandidates("dying"));
    }

    [Fact]
    public void HandlesIrregularVerbsAndNouns()
    {
        Assert.Contains("go", Lemmatizer.GetCandidates("went"));
        Assert.Contains("child", Lemmatizer.GetCandidates("children"));
        Assert.Contains("good", Lemmatizer.GetCandidates("better"));
    }
}

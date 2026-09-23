using WordCatcher.App.ViewModels;

namespace WordCatcher.App.Tests;

public sealed class WordLookupViewModelTests
{
    [Fact]
    public async Task SearchTrimsQueryAndReusesLookupResultFlow()
    {
        var (lookupVm, _, _, _) = LookupViewModelTests.Create();
        var pageVm = new WordLookupViewModel(lookupVm);

        pageVm.Query = "  digital  ";
        await pageVm.SearchCommand.ExecuteAsync(null);

        Assert.True(pageVm.HasSearched);
        Assert.Equal("digital", pageVm.Query);
        Assert.Equal("digital", lookupVm.SelectedText);
        Assert.True(lookupVm.HasResult);
    }

    [Fact]
    public async Task EmptyQueryShowsValidationAndDoesNotStartLookup()
    {
        var (lookupVm, _, _, _) = LookupViewModelTests.Create();
        var pageVm = new WordLookupViewModel(lookupVm);

        await pageVm.SearchCommand.ExecuteAsync(null);

        Assert.False(pageVm.HasSearched);
        Assert.Equal("请输入要查询的单词或短语", pageVm.QueryError);
        Assert.False(lookupVm.HasResult);
    }

    [Fact]
    public async Task ClearingQueryHidesPreviousResult()
    {
        var (lookupVm, _, _, _) = LookupViewModelTests.Create();
        var pageVm = new WordLookupViewModel(lookupVm) { Query = "digital" };
        await pageVm.SearchCommand.ExecuteAsync(null);

        pageVm.ClearQueryCommand.Execute(null);

        Assert.False(pageVm.HasSearched);
        Assert.False(lookupVm.HasResult);
        Assert.False(lookupVm.CanSave);
        Assert.Equal(string.Empty, pageVm.Query);
    }
}

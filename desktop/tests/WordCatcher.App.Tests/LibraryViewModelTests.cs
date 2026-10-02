using System.Reflection;
using System.IO;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Core.Enums;

namespace WordCatcher.App.Tests;

public class LibraryViewModelTests
{
    public class RepositoryProxy : DispatchProxy
    {
        public Func<string?, int, int, Task<IReadOnlyList<Word>>> ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        public Func<string, Task<IReadOnlyList<Occurrence>>> ReadOccurrences = _ => Task.FromResult<IReadOnlyList<Occurrence>>([]);
        public Func<Word, Task> Update = _ => Task.CompletedTask;
        public Func<string?, WordFilter, int, int, Task<IReadOnlyList<Word>>> ReadFiltered = (_, _, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        public Func<string, Task> Trash = _ => Task.CompletedTask;
        public Func<string, Task> Restore = _ => Task.CompletedTask;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IWordRepository.GetFilteredWordsAsync) => ReadFiltered((string?)args![0], (WordFilter)args[1]!, (int)args[2]!, (int)args[3]!),
            nameof(IWordRepository.MoveWordToTrashAsync) => Trash((string)args![0]!),
            nameof(IWordRepository.RestoreWordAsync) => Restore((string)args![0]!),
            nameof(IWordRepository.GetWordsAsync) => ReadWords((string?)args![0], (int)args[1]!, (int)args[2]!),
            nameof(IWordRepository.GetOccurrencesByWordIdAsync) => ReadOccurrences((string)args![0]!),
            nameof(IWordRepository.UpdateWordAsync) => Update((Word)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        };
    }

    private static (LibraryViewModel Vm, RepositoryProxy Repo) Create(WordCollectionEvents? events = null)
    {
        var repo = DispatchProxy.Create<IWordRepository, RepositoryProxy>();
        return (new LibraryViewModel(repo, events ?? new WordCollectionEvents()), (RepositoryProxy)repo);
    }

    [Fact]
    public async Task FailedLoadShowsRetryStateAndSuccessfulRetryClearsIt()
    {
        var (vm, repo) = Create();
        repo.ReadWords = (_, _, _) => throw new IOException();
        await vm.InitializeAsync();
        Assert.True(vm.HasLoadError);
        Assert.False(vm.IsLoading);
        Assert.Contains("无法加载", vm.EmptyMessage);

        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([new() { Id = "one" }]);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.False(vm.HasLoadError);
        Assert.Single(vm.Words);
    }

    [Fact]
    public async Task SavingLocksCancellationUntilWriteFinishes()
    {
        var (vm, repo) = Create();
        var write = new TaskCompletionSource();
        var word = new Word { Id = "one", DisplayWord = "word", Definition = "definition" };
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([word]);
        repo.Update = _ => write.Task;
        await vm.InitializeAsync();
        vm.StartEditCommand.Execute(null);
        var saving = vm.SaveEditCommand.ExecuteAsync(null);
        Assert.True(vm.IsSavingEdit);
        Assert.False(vm.CancelEditCommand.CanExecute(null));
        vm.CancelEditCommand.Execute(null);
        Assert.True(vm.IsEditing);
        write.SetResult();
        await saving;
        Assert.False(vm.IsSavingEdit);
        Assert.False(vm.IsEditing);
        Assert.True(vm.CancelEditCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshReplacesSelectedInstanceAndClearsMissingSelection()
    {
        var (vm, repo) = Create();
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([new() { Id = "one", Definition = "old" }]);
        await vm.InitializeAsync();
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([new() { Id = "one", Definition = "new" }]);
        await vm.SearchAsync();
        Assert.Equal("new", vm.SelectedWord!.Definition);
        Assert.Same(vm.Words[0], vm.SelectedWord);
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        await vm.SearchAsync();
        Assert.Null(vm.SelectedWord);
        Assert.Empty(vm.Occurrences);
    }

    [Fact]
    public async Task ExternalSaveWaitsUntilEditingEndsBeforeRefreshing()
    {
        var events = new WordCollectionEvents();
        var (vm, repo) = Create(events);
        var stored = new List<Word> { new() { Id = "one", DisplayWord = "one" } };
        repo.ReadWords = (_, limit, offset) => Task.FromResult<IReadOnlyList<Word>>(stored.Skip(offset).Take(limit).ToArray());
        await vm.InitializeAsync();
        vm.StartEditCommand.Execute(null);
        vm.EditWordText = "unsaved draft";
        stored.Insert(0, new Word { Id = "two", DisplayWord = "two" });

        events.NotifyWordSaved();

        Assert.Single(vm.Words);
        Assert.Equal("unsaved draft", vm.EditWordText);
        vm.CancelEditCommand.Execute(null);
        Assert.Equal(2, vm.Words.Count);
    }

    [Fact]
    public async Task PagingUsesLookAheadWithoutSkippingWord()
    {
        var (vm, repo) = Create();
        var all = Enumerable.Range(0, 205).Select(i => new Word { Id = i.ToString() }).ToArray();
        repo.ReadWords = (_, limit, offset) => Task.FromResult<IReadOnlyList<Word>>(all.Skip(offset).Take(limit).ToArray());
        await vm.InitializeAsync();
        Assert.Equal(100, vm.Words.Count);
        Assert.True(vm.HasMore);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(205, vm.Words.Count);
        Assert.Equal(205, vm.Words.Select(w => w.Id).Distinct().Count());
        Assert.False(vm.HasMore);
    }

    [Fact]
    public async Task OldSearchCannotOverwriteNewResults()
    {
        var (vm, repo) = Create();
        var first = new TaskCompletionSource<IReadOnlyList<Word>>();
        repo.ReadWords = (_, _, _) => first.Task;
        var pending = vm.InitializeAsync();
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([new() { Id = "new" }]);
        await vm.SearchAsync();
        first.SetResult([new() { Id = "old" }]);
        await pending;
        Assert.Equal("new", vm.SelectedWord!.Id);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task StaleOccurrencesAreIgnored()
    {
        var (vm, repo) = Create();
        var old = new TaskCompletionSource<IReadOnlyList<Occurrence>>();
        repo.ReadOccurrences = id => id == "old" ? old.Task : Task.FromResult<IReadOnlyList<Occurrence>>([new() { Sentence = "new context" }]);
        vm.SelectedWord = new Word { Id = "old" };
        vm.SelectedWord = new Word { Id = "new" };
        old.SetResult([new() { Sentence = "old context" }]);
        await old.Task;
        Assert.Equal("new context", Assert.Single(vm.Occurrences).Sentence);
    }

    [Fact]
    public async Task FailedSaveKeepsOriginalAndDraft()
    {
        var (vm, repo) = Create();
        vm.SelectedWord = new Word { DisplayWord = "original", Definition = "definition" };
        vm.StartEditCommand.Execute(null);
        vm.EditWordText = "changed";
        repo.Update = _ => throw new IOException();
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Equal("original", vm.SelectedWord.DisplayWord);
        Assert.Equal("changed", vm.EditWordText);
        Assert.True(vm.IsEditing);
        Assert.Contains("保存失败", vm.StatusMessage);
    }

    [Fact]
    public async Task BlankEditIsRejected()
    {
        var (vm, repo) = Create();
        vm.SelectedWord = new Word { DisplayWord = "word", Definition = "definition" };
        vm.StartEditCommand.Execute(null);
        vm.EditWordText = " ";
        repo.Update = _ => throw new InvalidOperationException("Must not save");
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Contains("不能为空", vm.StatusMessage);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public async Task DeleteLastWordCanBeUndoneAndFailedRestoreKeepsUndoAvailable()
    {
        var (vm, repo) = Create();
        var word = new Word { Id = "one", DisplayWord = "one" };
        var visible = true;
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>(visible ? [word] : []);
        repo.Trash = _ => { visible = false; return Task.CompletedTask; };
        repo.Restore = _ => throw new IOException();
        await vm.InitializeAsync();
        await vm.DeleteWordCommand.ExecuteAsync(null);
        Assert.Empty(vm.Words);
        Assert.True(vm.CanUndoDelete);
        await vm.UndoDeleteCommand.ExecuteAsync(null);
        Assert.True(vm.CanUndoDelete);
        Assert.Contains("恢复失败", vm.DeleteStatusText);
        repo.Restore = _ => { visible = true; return Task.CompletedTask; };
        await vm.UndoDeleteCommand.ExecuteAsync(null);
        Assert.Same(word, vm.SelectedWord);
        Assert.False(vm.CanUndoDelete);
    }

    [Fact]
    public async Task ChangingFilterResetsPaginationAndKeepsQuery()
    {
        var (vm, repo) = Create();
        var offsets = new List<int>();
        repo.ReadFiltered = (query, filter, limit, offset) =>
        {
            Assert.Equal("needle", query);
            Assert.Equal(WordFilter.Failed, filter);
            offsets.Add(offset);
            return Task.FromResult<IReadOnlyList<Word>>(Enumerable.Range(offset, Math.Min(limit, 120 - offset))
                .Select(i => new Word { Id = i.ToString() }).ToArray());
        };
        vm.SearchQuery = "needle";
        vm.SelectedFilterIndex = 2;
        Assert.Equal(100, vm.Words.Count);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(120, vm.Words.Count);
        Assert.Equal(new[] { 0, 100 }, offsets);
    }
}

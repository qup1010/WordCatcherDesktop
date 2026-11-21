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
    [Theory]
    [InlineData("context")]
    [InlineData("word")]
    [InlineData("savingContext")]
    [InlineData("savingWord")]
    public async Task FocusWordPreservesActiveEditor(string state)
    {
        var (vm, _) = Create();
        var original = new Word { Id = "a", DisplayWord = "alpha" };
        var other = new Word { Id = "b", DisplayWord = "beta" };
        vm.Words.Add(original);
        vm.Words.Add(other);
        vm.SelectedWord = original;
        var occurrence = new Occurrence { Id = "context", WordId = "a" };
        if (state == "context") vm.EditContextCommand.Execute(occurrence);
        vm.ContextNote = "unsaved note";
        vm.ContextTranslation = "unsaved translation";
        vm.EditDefinition = "unsaved definition";
        if (state == "word") vm.IsEditing = true;
        if (state == "savingContext") vm.IsSavingContext = true;
        if (state == "savingWord") vm.IsSavingEdit = true;

        await vm.FocusWordAsync("beta");

        Assert.Same(original, vm.SelectedWord);
        if (state == "context") Assert.Same(occurrence, vm.EditingOccurrence);
        if (state == "word") Assert.True(vm.IsEditing);
        if (state == "savingContext") Assert.True(vm.IsSavingContext);
        if (state == "savingWord") Assert.True(vm.IsSavingEdit);
        Assert.Equal("unsaved note", vm.ContextNote);
        Assert.Equal("unsaved translation", vm.ContextTranslation);
        Assert.Equal("unsaved definition", vm.EditDefinition);
        Assert.Empty(vm.SearchQuery);
    }

    [Fact]
    public async Task FocusWordClearsExcludingFiltersAndFindsTargetBeyondFirstPage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"focus-view-{Guid.NewGuid():N}.json");
        try
        {
            new LibraryViewPreferences(4, 1, "chrome").Save(path);
            var repository = DispatchProxy.Create<IWordRepository, RepositoryProxy>();
            var repo = (RepositoryProxy)repository;
            var target = new Word { Id = "target", DisplayWord = "bank" };
            var words = Enumerable.Range(0, 105).Select(i => new Word { Id = $"w{i}", DisplayWord = $"bank-{i}" }).Append(target).ToArray();
            repo.Find = _ => Task.FromResult<Word?>(target);
            repo.ReadFiltered = (_, filter, limit, offset) => Task.FromResult<IReadOnlyList<Word>>(
                filter == WordFilter.All ? words.Skip(offset).Take(limit).ToArray() : []);
            var vm = new LibraryViewModel(repository, new WordCollectionEvents(), preferencesPath: path);
            await vm.InitializeAsync();
            Assert.Empty(vm.Words);

            await vm.FocusWordAsync("bank");

            Assert.Equal(0, vm.SelectedFilterIndex);
            Assert.Empty(vm.SourceFilter);
            Assert.Equal("bank", vm.SearchQuery);
            Assert.Same(target, vm.SelectedWord);
            Assert.Equal(words.Length, vm.Words.Count);
            Assert.False(vm.HasMore);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task PartialBatchDeletionAndUndoRemainRetryable()
    {
        var (vm, repo) = Create();
        var deleted = new List<string>();
        repo.Trash = id => { if (id == "three") throw new IOException(); deleted.Add(id); return Task.CompletedTask; };
        vm.SetSelectedWords(new[] { new Word { Id = "one" }, new Word { Id = "two" }, new Word { Id = "three" } });
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "one", "two" }, deleted);
        Assert.True(vm.CanUndoDelete);
        Assert.Contains("2 / 3", vm.DeleteStatusText);
        var restored = new List<string>();
        repo.Restore = id => { if (id == "one") throw new IOException(); restored.Add(id); return Task.CompletedTask; };
        await vm.UndoDeleteCommand.ExecuteAsync(null);
        Assert.True(vm.CanUndoDelete);
        Assert.Equal(new[] { "two" }, restored);
        repo.Restore = id => { restored.Add(id); return Task.CompletedTask; };
        await vm.UndoDeleteCommand.ExecuteAsync(null);
        Assert.False(vm.CanUndoDelete);
        Assert.Equal(new[] { "two", "one" }, restored);
    }

    [Fact]
    public async Task LibraryViewRemembersFiltersAndSortWithoutPersistingSearchText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"library-view-{Guid.NewGuid():N}.json");
        try
        {
            new LibraryViewPreferences(4, 1, "chrome").Save(path);
            var repo = DispatchProxy.Create<IWordRepository, RepositoryProxy>();
            var vm = new LibraryViewModel(repo, new WordCollectionEvents(), preferencesPath: path);
            Assert.Equal(4, vm.SelectedFilterIndex);
            Assert.Equal(1, vm.SelectedSortIndex);
            Assert.Equal("chrome", vm.SourceFilter);
            Assert.Equal("筛选 · 2", vm.FilterButtonText);
            Assert.Equal("有备注 ×", vm.FilterTagText);
            Assert.Equal("chrome ×", vm.SourceTagText);
            Assert.Empty(vm.SearchQuery);
            await vm.InitializeAsync();
            Assert.False(vm.HasLoadError);
            Assert.Equal(new LibraryViewPreferences(4, 1, "chrome"), LibraryViewPreferences.Load(path));
            vm.ClearStatusFilterCommand.Execute(null);
            Assert.Equal("筛选 · 1", vm.FilterButtonText);
            Assert.Empty(vm.FilterTagText);
            vm.ClearSourceFilterCommand.Execute(null);
            Assert.Equal("筛选", vm.FilterButtonText);
            Assert.Empty(vm.SourceTagText);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DelayedSentenceTranslationDoesNotOverwriteANewerDraft()
    {
        var repository = DispatchProxy.Create<IWordRepository, RepositoryProxy>();
        var lookup = DispatchProxy.Create<ILookupService, LookupViewModelTests.ServiceProxy>();
        var result = new TaskCompletionSource<string>();
        ((LookupViewModelTests.ServiceProxy)lookup).TranslateSentence = (_, _) => result.Task;
        var vm = new LibraryViewModel(repository, new WordCollectionEvents(), lookupService: lookup);
        vm.EditContextCommand.Execute(new Occurrence { Sentence = "We sat on the bank.", SelectedText = "bank" });
        var translating = vm.TranslateOccurrenceSentenceCommand.ExecuteAsync(null);
        vm.ContextTranslation = "用户刚刚输入的译文";
        result.SetResult("较晚返回的自动译文");
        await translating;
        Assert.Equal("用户刚刚输入的译文", vm.ContextTranslation);
        Assert.False(vm.IsTranslatingContextSentence);
    }

    [Fact]
    public async Task SentenceTranslationFailureOffersExplicitAiAction()
    {
        var repository = DispatchProxy.Create<IWordRepository, RepositoryProxy>();
        var lookup = DispatchProxy.Create<ILookupService, LookupViewModelTests.ServiceProxy>();
        var service = (LookupViewModelTests.ServiceProxy)lookup;
        service.TranslateSentence = (_, _) => throw new InvalidOperationException("provider unavailable");
        var vm = new LibraryViewModel(repository, new WordCollectionEvents(), lookupService: lookup);
        vm.EditContextCommand.Execute(new Occurrence { Sentence = "We sat on the bank.", SelectedText = "bank" });

        await vm.TranslateOccurrenceSentenceCommand.ExecuteAsync(null);

        Assert.True(vm.ContextSentenceTranslationFailed);
        Assert.True(vm.CanUseAiContextSentence);
        Assert.Contains("在线快译失败", vm.StatusMessage);

        await vm.UseAiOccurrenceSentenceCommand.ExecuteAsync(null);

        Assert.Equal("AI 译句", vm.ContextTranslation);
        Assert.False(vm.ContextSentenceTranslationFailed);
        Assert.False(vm.CanUseAiContextSentence);
    }

    [Fact]
    public void SyncRefreshDoesNotEmitAnotherLocalSave()
    {
        var events = new WordCollectionEvents();
        var saves = 0;
        var refreshes = 0;
        events.WordSaved += () => saves++;
        events.CollectionChanged += () => refreshes++;
        events.NotifyWordSaved();
        events.NotifyCollectionChanged();
        Assert.Equal(1, saves);
        Assert.Equal(2, refreshes);
    }

    public class RepositoryProxy : DispatchProxy
    {
        public Func<string, Task<Word?>> Find = _ => Task.FromResult<Word?>(null);
        public Func<string, Task<Word?>> FindById = _ => Task.FromResult<Word?>(null);
        public Func<string?, int, int, Task<IReadOnlyList<Word>>> ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        public Func<string, Task<IReadOnlyList<Occurrence>>> ReadOccurrences = _ => Task.FromResult<IReadOnlyList<Occurrence>>([]);
        public Func<Word, Task> Update = _ => Task.CompletedTask;
        public Func<string?, WordFilter, int, int, Task<IReadOnlyList<Word>>> ReadFiltered = (_, _, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        public Func<string, Task> Trash = _ => Task.CompletedTask;
        public Func<string, Task> Restore = _ => Task.CompletedTask;
        public Func<string, Task<int>> ImportJson = _ => Task.FromResult(0);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IWordRepository.FindWordAsync) => Find((string)args![0]!),
            nameof(IWordRepository.GetWordByIdAsync) => FindById((string)args![0]!),
            nameof(IWordRepository.GetFilteredWordsAsync) => ReadFiltered((string?)args![0], (WordFilter)args[1]!, (int)args[2]!, (int)args[3]!),
            nameof(IWordRepository.MoveWordToTrashAsync) => Trash((string)args![0]!),
            nameof(IWordRepository.RestoreWordAsync) => Restore((string)args![0]!),
            nameof(IWordRepository.GetWordsAsync) => ReadWords((string?)args![0], (int)args[1]!, (int)args[2]!),
            nameof(IWordRepository.GetOccurrencesByWordIdAsync) => ReadOccurrences((string)args![0]!),
            nameof(IWordRepository.UpdateWordAsync) => Update((Word)args![0]!),
            nameof(IWordRepository.ImportWordsJsonAsync) => ImportJson((string)args![0]!),
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
    public async Task SyncRefreshWaitsUntilContextEditingEnds()
    {
        var events = new WordCollectionEvents();
        var (vm, repo) = Create(events);
        var stored = new List<Word> { new() { Id = "one", DisplayWord = "one" } };
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>(stored.ToArray());
        await vm.InitializeAsync();
        vm.EditContextCommand.Execute(new Occurrence { WordId = "one", SelectedText = "one" });
        vm.ContextNote = "未保存的备注";
        stored.Add(new Word { Id = "two", DisplayWord = "two" });

        events.NotifyCollectionChanged();
        Assert.Single(vm.Words);
        Assert.Equal("未保存的备注", vm.ContextNote);
        Assert.True(vm.IsEditingContext);
        vm.CancelContextEditCommand.Execute(null);
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

    [Fact]
    public async Task FocusWordWithPreferredOccurrenceSelectsTargetOccurrence()
    {
        var (vm, repo) = Create();
        var word = new Word { Id = "w1", DisplayWord = "alpha" };
        var occ1 = new Occurrence { Id = "o1", WordId = "w1", Sentence = "First sentence." };
        var occ2 = new Occurrence { Id = "o2", WordId = "w1", Sentence = "Second sentence." };
        repo.FindById = id => Task.FromResult<Word?>(id == "w1" ? word : null);
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([word]);
        repo.ReadOccurrences = _ => Task.FromResult<IReadOnlyList<Occurrence>>([occ1, occ2]);

        await vm.InitializeAsync();
        await vm.FocusWordAsync("alpha", "w1", "o2");

        Assert.Same(word, vm.SelectedWord);
        Assert.Equal("o2", vm.SelectedOccurrence?.Id);
    }

    [Fact]
    public async Task StartReviewLoadsAllMatchingPagesCompletesAndSupportsMarkedRepractice()
    {
        var (vm, repo) = Create();
        var words = Enumerable.Range(0, 105)
            .Select(i => new Word { Id = $"w{i}", DisplayWord = $"word-{i}", Definition = $"def-{i}" })
            .ToArray();
        repo.ReadWords = (_, limit, offset) => Task.FromResult<IReadOnlyList<Word>>(words.Skip(offset).Take(limit).ToArray());
        repo.ReadOccurrences = _ => Task.FromResult<IReadOnlyList<Occurrence>>([]);

        await vm.InitializeAsync();
        Assert.Equal(100, vm.Words.Count);

        await vm.StartReviewCommand.ExecuteAsync(null);
        Assert.True(vm.IsReviewMode);
        Assert.False(vm.IsReviewCompleted);
        Assert.Equal(105, vm.ReviewTotal);

        // When multiple words are selected, review scopes explicitly to those selected words.
        vm.ExitReviewCommand.Execute(null);
        vm.SetSelectedWords([words[0], words[2]]);
        await vm.StartReviewCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.ReviewTotal);
        Assert.Contains("已选 2 词", vm.ReviewScopeLabel);

        vm.ToggleMarkReviewAgainCommand.Execute(null);
        Assert.True(vm.IsCurrentReviewMarkedAgain);
        Assert.Equal(1, vm.MarkedAgainCount);

        vm.NextReviewCardCommand.Execute(null);
        Assert.Equal(1, vm.ReviewIndex);
        Assert.True(vm.IsLastReviewCard);

        vm.NextReviewCardCommand.Execute(null);
        Assert.True(vm.IsReviewCompleted);
        Assert.True(vm.HasMarkedAgainItems);

        vm.PracticeMarkedAgainCommand.Execute(null);
        Assert.False(vm.IsReviewCompleted);
        Assert.Equal(1, vm.ReviewTotal);
        Assert.Equal("w0", vm.CurrentReviewWord?.Id);
    }

    [Fact]
    public async Task ReviewExposesHighlightedSentencePartsAndFallbackTargetWord()
    {
        var (vm, repo) = Create();
        var wordWithContext = new Word { Id = "w1", DisplayWord = "semantic", NormalizedWord = "semantic", Definition = "adj. 语义的" };
        var wordWithoutHit = new Word { Id = "w2", DisplayWord = "resilient", NormalizedWord = "resilient", Definition = "adj. 有韧性的" };
        var wordOnly = new Word { Id = "w3", DisplayWord = "leverage", NormalizedWord = "leverage", Definition = "n. 杠杆作用" };
        repo.ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([wordWithContext, wordWithoutHit, wordOnly]);
        repo.ReadOccurrences = id => id switch
        {
            "w1" => Task.FromResult<IReadOnlyList<Occurrence>>([new Occurrence { Id = "o1", WordId = "w1", SelectedText = "semantic", Sentence = "Semantic aspect (mAP@10)" }]),
            "w2" => Task.FromResult<IReadOnlyList<Occurrence>>([new Occurrence { Id = "o2", WordId = "w2", SelectedText = "resilient", Sentence = "The team recovered quickly." }]),
            _ => Task.FromResult<IReadOnlyList<Occurrence>>([])
        };

        await vm.InitializeAsync();
        await vm.StartReviewCommand.ExecuteAsync(null);

        Assert.True(vm.HasReviewPromptHighlight);
        Assert.Equal(string.Empty, vm.ReviewPromptPrefix);
        Assert.Equal("Semantic", vm.ReviewPromptHighlight);
        Assert.Equal(" aspect (mAP@10)", vm.ReviewPromptSuffix);
        Assert.Equal(string.Empty, vm.ReviewFallbackTargetWord);

        vm.NextReviewCardCommand.Execute(null);
        Assert.False(vm.HasReviewPromptHighlight);
        Assert.Equal("The team recovered quickly.", vm.ReviewPromptPrefix);
        Assert.Equal(string.Empty, vm.ReviewPromptHighlight);
        Assert.Equal(string.Empty, vm.ReviewPromptSuffix);
        Assert.Equal("resilient", vm.ReviewFallbackTargetWord);

        vm.NextReviewCardCommand.Execute(null);
        Assert.True(vm.HasReviewPromptHighlight);
        Assert.Equal(string.Empty, vm.ReviewPromptPrefix);
        Assert.Equal("leverage", vm.ReviewPromptHighlight);
        Assert.Equal(string.Empty, vm.ReviewPromptSuffix);
        Assert.Equal(string.Empty, vm.ReviewFallbackTargetWord);
    }
}

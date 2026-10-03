using System.IO;
using System.Reflection;
using System.Text.Json;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Dictionary;
using WordCatcher.Infrastructure.Dictionary.Models;

namespace WordCatcher.App.Tests;

public class LookupViewModelTests
{
    public class ServiceProxy : DispatchProxy
    {
        public Func<CaptureResult, Task<TranslationResult>> Lookup = _ => Task.FromResult(Digital());
        public Func<SaveCardCommand, Task<(Word, Occurrence, SyncJob)>> Save = _ => Task.FromResult((new Word(), new Occurrence(), new SyncJob()));
        public Func<string?, int, int, Task<IReadOnlyList<Word>>> ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        public AppSettings Settings = new() { Ui = new() { ClosePopupAfterSave = false } };
        public List<string> Queued = [];
        public Word? ExistingWord;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "FindWordAsync" => Task.FromResult(ExistingWord),
            "LookupAsync" => Lookup((CaptureResult)args![0]!),
            "SaveAsync" => Save((SaveCardCommand)args![0]!),
            "GetWordsAsync" => ReadWords((string?)args![0], (int)args[1]!, (int)args[2]!),
            "GetOccurrencesByWordIdAsync" => Task.FromResult<IReadOnlyList<Occurrence>>([]),
            "get_Current" => Settings,
            "Enqueue" => Enqueue((string)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        };
        private object? Enqueue(string id) { Queued.Add(id); return null; }
    }

    private sealed class FakeSpeechService : ISpeechService
    {
        public string? LastText { get; private set; }
        public Exception? Error { get; init; }

        public void Speak(string text)
        {
            LastText = text;
            if (Error is not null) throw Error;
        }
    }

    public static TranslationResult Digital() => OfflineDictionaryService.FormatEntry(
        JsonSerializer.Deserialize<DistributionEntryV5>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "digital.v5.json")))!);

    public static CaptureResult Capture(string word = "digital") => new(word, "test", "test", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);

    public static (LookupViewModel Vm, ServiceProxy Lookup, ServiceProxy Repository, ServiceProxy Queue) Create(
        WordCollectionEvents? wordCollectionEvents = null,
        ISpeechService? speechService = null)
    {
        var lookup = DispatchProxy.Create<ILookupService, ServiceProxy>();
        var repo = DispatchProxy.Create<IWordRepository, ServiceProxy>();
        var queue = DispatchProxy.Create<IAnkiSyncQueue, ServiceProxy>();
        var settings = DispatchProxy.Create<ISettingsService, ServiceProxy>();
        return (new LookupViewModel(lookup, repo, queue, settings, speechService ?? new SpeechService(), wordCollectionEvents ?? new WordCollectionEvents()),
            (ServiceProxy)lookup, (ServiceProxy)repo, (ServiceProxy)queue);
    }

    [Fact]
    public async Task SpeakingDoesNotReplaceCollectionFeedback()
    {
        var speech = new FakeSpeechService();
        var (vm, _, _, _) = Create(speechService: speech);
        await vm.StartLookupAsync(Capture());
        vm.SavedStatusText = "已收藏";

        vm.SpeakCommand.Execute(null);

        Assert.Equal("digital", speech.LastText);
        Assert.Equal("已收藏", vm.SavedStatusText);
    }

    [Fact]
    public async Task SpeechFailureRemainsVisibleInFooter()
    {
        var speech = new FakeSpeechService { Error = new InvalidOperationException("speech unavailable") };
        var (vm, _, _, _) = Create(speechService: speech);
        await vm.StartLookupAsync(Capture());

        vm.SpeakCommand.Execute(null);

        Assert.Contains("朗读不可用", vm.ActionStatusText);
        Assert.Empty(vm.SavedStatusText);
    }

    [Fact]
    public async Task CompactViewCoversBothPartsOfSpeechAndExpansionKeepsAllNineSenses()
    {
        var (vm, _, _, _) = Create();
        await vm.StartLookupAsync(Capture());
        Assert.True(vm.HasStructuredDefinitions);
        Assert.False(vm.IsDetailsExpanded);
        Assert.Equal(2, vm.DisplayPosGroups!.Count);
        Assert.Contains("数字设备或数字技术", vm.DisplayPosGroups[1].QuickSummary);
        Assert.Equal(9, vm.DisplayPosGroups.Sum(g => g.Meanings.Count));
        Assert.Contains("9", vm.DetailsToggleText);
        vm.ToggleDetailsCommand.Execute(null);
        Assert.True(vm.IsDetailsExpanded);
        vm.IsMemoryHookExpanded = true;
        await vm.StartLookupAsync(Capture());
        Assert.False(vm.IsDetailsExpanded);
        Assert.False(vm.IsMemoryHookExpanded);
    }

    [Fact]
    public async Task SingleSenseStillAllowsDetailedExplanation()
    {
        var (vm, lookup, _, _) = Create();
        lookup.Lookup = _ => Task.FromResult(Digital() with { PosGroups = [new("n.", null, [new("s1", "rare", "简义", "完整解释", [])])] });
        await vm.StartLookupAsync(Capture());
        Assert.True(vm.CanExpandDetails);
        Assert.Equal("简义", vm.DisplayPosGroups![0].QuickSummary);
        vm.ToggleDetailsCommand.Execute(null);
        Assert.True(vm.IsDetailsExpanded);
    }

    [Fact]
    public async Task DictionaryWithoutStructuredGroupsStillShowsDefinition()
    {
        var (vm, lookup, _, _) = Create();
        lookup.Lookup = _ => Task.FromResult(Digital() with { PosGroups = [], Definition = "fallback definition" });
        await vm.StartLookupAsync(Capture());
        Assert.False(vm.HasStructuredDefinitions);
        Assert.False(vm.CanExpandDetails);
        Assert.Equal("fallback definition", vm.Definition);
    }

    [Fact]
    public async Task SaveUsesAllPartsOfSpeechRegardlessOfExpansion()
    {
        var (vm, _, repo, _) = Create();
        SaveCardCommand? saved = null;
        repo.Save = command => { saved = command; return Task.FromResult((new Word(), new Occurrence(), new SyncJob())); };
        await vm.StartLookupAsync(Capture());
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(saved);
        Assert.Contains("adj.", saved.Translation.Definition);
        Assert.Contains("n. 数字设备或数字技术", saved.Translation.Definition);
        Assert.Equal(9, saved.Translation.PosGroups!.Sum(g => g.Meanings.Count));
    }

    [Fact]
    public async Task SavingFromLookupImmediatelyRefreshesLibrary()
    {
        var events = new WordCollectionEvents();
        var (lookupVm, _, repository, _) = Create(events);
        var stored = new List<Word>();
        repository.ReadWords = (_, limit, offset) => Task.FromResult<IReadOnlyList<Word>>(stored.Skip(offset).Take(limit).ToArray());
        repository.Save = command =>
        {
            var word = new Word { Id = "saved-word", DisplayWord = command.Translation.Word };
            stored.Add(word);
            return Task.FromResult((word, new Occurrence(), new SyncJob { Id = "job" }));
        };
        var libraryVm = new LibraryViewModel((IWordRepository)repository, events);
        await libraryVm.InitializeAsync();
        Assert.Empty(libraryVm.Words);

        await lookupVm.StartLookupAsync(Capture());
        await lookupVm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("digital", Assert.Single(libraryVm.Words).DisplayWord);
        Assert.Equal("saved-word", libraryVm.SelectedWord?.Id);
    }

    [Fact]
    public async Task FailedSaveDoesNotNotifyLibrary()
    {
        var events = new WordCollectionEvents();
        var notifications = 0;
        events.WordSaved += () => notifications++;
        var (vm, _, repository, _) = Create(events);
        repository.Save = _ => throw new IOException("disk unavailable");

        await vm.StartLookupAsync(Capture());
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, notifications);
        Assert.Contains("保存失败", vm.SavedStatusText);
    }

    [Fact]
    public async Task PreviousSaveCannotMarkNewLookupAsSaved()
    {
        var events = new WordCollectionEvents();
        var notifications = 0;
        events.WordSaved += () => notifications++;
        var (vm, lookup, repo, queue) = Create(events);
        var pending = new TaskCompletionSource<(Word, Occurrence, SyncJob)>();
        repo.Save = _ => pending.Task;
        await vm.StartLookupAsync(Capture());
        var saveTask = vm.SaveCommand.ExecuteAsync(null);
        lookup.Lookup = _ => Task.FromResult(Digital() with { Word = "other" });
        await vm.StartLookupAsync(Capture("other"));
        pending.SetResult((new Word(), new Occurrence(), new SyncJob { Id = "old-job" }));
        await saveTask;
        Assert.Equal("other", vm.Word);
        Assert.False(vm.IsSaved);
        Assert.True(vm.CanSave);
        Assert.Empty(vm.SavedStatusText);
        Assert.Contains("old-job", queue.Queued);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ExistingCollectionCanAddContextAndSpeechFailureDoesNotReplaceSavedState()
    {
        var speech = new FakeSpeechService { Error = new InvalidOperationException("unavailable") };
        var (vm, _, repo, _) = Create(speechService: speech);
        repo.ExistingWord = new Word { DisplayWord = "digital" };
        await vm.StartLookupAsync(Capture());
        Assert.True(vm.IsSaved);
        Assert.Equal("保存新语境", vm.SaveButtonText);
        var pending = new TaskCompletionSource<(Word, Occurrence, SyncJob)>();
        repo.Save = _ => pending.Task;
        var save = vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("保存中…", vm.SaveButtonText);
        pending.SetResult((repo.ExistingWord, new Occurrence(), new SyncJob()));
        await save;
        Assert.Equal("已收藏 ✓", vm.SaveButtonText);
        Assert.False(vm.CanSave);
        var savedStatus = vm.SavedStatusText;
        vm.SpeakCommand.Execute(null);
        Assert.Equal(savedStatus, vm.SavedStatusText);
        Assert.Contains("新增语境", savedStatus);
        Assert.Contains("朗读不可用", vm.ActionStatusText);
    }
}

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
    [Fact]
    public async Task OcrSentenceShowsWordPickerAndNotifiesBindings()
    {
        const string sentence = "Read this sentence.";
        var (vm, _, _, _) = Create();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await vm.StartLookupAsync(Capture(sentence) with { SourceProcess = "screenocr", Sentence = sentence });

        Assert.True(vm.ShowOcrWordPicker);
        Assert.Equal(["Read", "this", "sentence"], vm.OcrSentenceTokens.Select(token => token.Text));
        Assert.Contains(nameof(vm.ShowOcrWordPicker), changedProperties);
        Assert.Contains(nameof(vm.OcrSentenceTokens), changedProperties);
    }

    [Fact]
    public async Task SelectingSecondOcrWordOccurrenceKeepsFullSentenceAndOffsetThroughSave()
    {
        const string sentence = "digital means digital here.";
        var (vm, lookup, repo, _) = Create();
        var lookups = new List<CaptureResult>();
        lookup.Lookup = capture =>
        {
            lookups.Add(capture);
            return Task.FromResult(Digital());
        };
        SaveCardCommand? saved = null;
        repo.Save = command =>
        {
            saved = command;
            return Task.FromResult((new Word(), new Occurrence(), new SyncJob()));
        };
        var ocrCapture = Capture(sentence) with
        {
            SourceProcess = "ScreenOcr",
            SourceWindowTitle = "OCR test",
            CursorPosition = new ScreenPoint(12, 34),
            Sentence = sentence
        };

        await vm.StartLookupAsync(ocrCapture);
        await vm.SelectOcrTokenCommand.ExecuteAsync(vm.OcrSentenceTokens[0]);
        await vm.SelectOcrTokenCommand.ExecuteAsync(vm.OcrSentenceTokens[2]);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(3, lookups.Count);
        var selected = lookups[2];
        Assert.Equal("digital", selected.SelectedText);
        Assert.Equal("ScreenOcr", selected.SourceProcess);
        Assert.Equal(ocrCapture.SourceWindowTitle, selected.SourceWindowTitle);
        Assert.Equal(ocrCapture.CursorPosition, selected.CursorPosition);
        Assert.Equal(sentence, selected.Sentence);
        Assert.Equal(14, selected.SentenceOffset);
        Assert.Equal(sentence, vm.OriginalSentence);
        Assert.Equal(selected, saved?.Capture);
        Assert.True(vm.ShowOcrWordPicker);
        Assert.Equal(4, vm.OcrSentenceTokens.Count);
    }

    [Theory]
    [InlineData("digital", "test", "")]
    [InlineData("Read this sentence.", "test", "Read this sentence.")]
    [InlineData("digital", "ScreenOcr", "digital")]
    public async Task NewLookupWithoutMultipleOcrWordsClearsThePicker(string selection, string source, string sentence)
    {
        var (vm, _, _, _) = Create();
        await vm.StartLookupAsync(Capture("Two words") with { SourceProcess = "ScreenOcr", Sentence = "Two words" });

        await vm.StartLookupAsync(Capture(selection) with { SourceProcess = source, Sentence = sentence });

        Assert.False(vm.ShowOcrWordPicker);
        Assert.Empty(vm.OcrSentenceTokens);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  THE NEW CLIENT EXPOSES A PROTOCOL GAP.  ", false)]
    [InlineData("The new client exposes a protocol gap. We should fix it.", false)]
    [InlineData("protocol gap", true)]
    public async Task SentenceAnalysisOnlyDisplaysADistinctExpression(string expression, bool expected)
    {
        const string selection = "The new client exposes a protocol gap.";
        var (vm, lookup, _, _) = Create();
        lookup.Lookup = _ => Task.FromResult(Digital() with
        {
            Word = selection,
            Source = LookupSource.Ai,
            Analysis = new LearningAnalysis("sentence", expression, "", "", "", "")
        });

        await vm.StartLookupAsync(Capture(selection) with { Sentence = selection + " We should fix it." });

        Assert.True(vm.IsLongText);
        Assert.True(vm.HasAnalysis);
        Assert.Equal(expected, vm.ShowLearningExpression);
    }

    [Fact]
    public void HeadwordAnalysisRefreshesExpressionVisibilityWhenWordChanges()
    {
        var (vm, _, _, _) = Create();
        vm.Word = "digital";
        vm.Analysis = new LearningAnalysis("word", " DIGITAL ", "", "", "", "");
        Assert.False(vm.ShowLearningExpression);
        var visibilityChanged = false;
        vm.PropertyChanged += (_, args) =>
            visibilityChanged |= args.PropertyName == nameof(LookupViewModel.ShowLearningExpression);

        vm.Word = "digit";

        Assert.True(visibilityChanged);
        Assert.True(vm.ShowLearningExpression);
    }

    [Fact]
    public async Task SavedContextCanBeEditedTranslatedAndSavedAgainInTheDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lookup-edit-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new WordCatcher.Infrastructure.Database.SqliteConnectionFactory(path);
            await new WordCatcher.Infrastructure.Database.SqliteMigrationRunner(factory).MigrateAsync();
            var repository = new WordCatcher.Infrastructure.Database.WordRepository(factory);
            var lookup = DispatchProxy.Create<ILookupService, ServiceProxy>();
            var queue = DispatchProxy.Create<IAnkiSyncQueue, ServiceProxy>();
            var settings = DispatchProxy.Create<ISettingsService, ServiceProxy>();
            var vm = new LookupViewModel(lookup, repository, queue, settings, new FakeSpeechService(), new WordCollectionEvents());
            await vm.StartLookupAsync(Capture() with { Sentence = "We use digital systems.", SentenceOffset = 7 });
            await vm.SaveCommand.ExecuteAsync(null);
            var word = Assert.Single(await repository.GetWordsAsync());
            var original = Assert.Single(await repository.GetOccurrencesByWordIdAsync(word.Id));
            Assert.True(vm.HasSavedContext);
            Assert.False(vm.CanSave);
            vm.EditDefinition = "自定义释义";
            vm.LearningNote = "新笔记";
            Assert.True(vm.CanSave);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal("已更新当前语境", vm.SavedStatusText);
            Assert.False(vm.CanSave);
            await vm.TranslateSentenceCommand.ExecuteAsync(null);
            Assert.True(vm.CanSave);
            await vm.SaveCommand.ExecuteAsync(null);
            var actual = Assert.Single(await repository.GetOccurrencesByWordIdAsync(word.Id));
            Assert.Equal(original.Id, actual.Id);
            Assert.Equal("自定义释义", actual.Definition);
            Assert.Equal("新笔记", actual.Note);
            Assert.Equal("手动翻译的原句意思", actual.Translation);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
    [Fact]
    public void SavedMeaningPresentationUsesSourceAndSuppressesDuplicates()
    {
        var occurrence = new Occurrence { LearningResult = Digital(), WordDefinition = Digital().Definition };
        Assert.Equal("词典释义", occurrence.DefinitionLabel);
        Assert.Empty(occurrence.AdditionalDefinition);
        occurrence.Edits = new LearningEdits("我的补充解释", null, string.Empty);
        Assert.Equal("我的释义", occurrence.DefinitionLabel);
        Assert.Equal("我的补充解释", occurrence.AdditionalDefinition);
        var empty = new Occurrence();
        Assert.False(empty.HasExplanation);
        occurrence.LearningResult = Digital() with { Source = LookupSource.Ai };
        Assert.Equal("AI 原句解析", occurrence.ExplanationLabel);
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<CaptureResult, Task<TranslationResult>> Lookup = _ => Task.FromResult(Digital());
        public Func<string, string?, Task<string>> TranslateSentence = (_, _) => Task.FromResult("手动翻译的原句意思");
        public Func<string, string?, Task<string>> TranslateSentenceWithAi = (_, _) => Task.FromResult("AI 译句");
        public CancellationToken SentenceToken;
        public Func<SaveCardCommand, Task<(Word, Occurrence, SyncJob)>> Save = _ => Task.FromResult((new Word(), new Occurrence(), new SyncJob()));
        public Func<string?, int, int, Task<IReadOnlyList<Word>>> ReadWords = (_, _, _) => Task.FromResult<IReadOnlyList<Word>>([]);
        public AppSettings Settings = new() { Ui = new() { ClosePopupAfterSave = false } };
        public List<string> Queued = [];
        public Word? ExistingWord;
        public string? UndoneId;
        private Task Undo(string id) { UndoneId = id; return Task.CompletedTask; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "FindWordAsync" => Task.FromResult(ExistingWord),
            "LookupAsync" => Lookup((CaptureResult)args![0]!),
            "TranslateSentenceAsync" => TranslateSentenceRequest(args!, false),
            "TranslateSentenceWithAiAsync" => TranslateSentenceRequest(args!, true),
            "SaveAsync" => Save((SaveCardCommand)args![0]!),
            "UndoNewOccurrenceAsync" => Undo((string)args![0]!),
            "GetWordsAsync" => ReadWords((string?)args![0], (int)args[1]!, (int)args[2]!),
            "GetOccurrencesByWordIdAsync" => Task.FromResult<IReadOnlyList<Occurrence>>([]),
            "get_Current" => Settings,
            "Enqueue" => Enqueue((string)args![0]!),
            _ => throw new NotSupportedException(method.Name)
        };
        private object? Enqueue(string id) { Queued.Add(id); return null; }
        private Task<string> TranslateSentenceRequest(object?[] args, bool useAi)
        {
            SentenceToken = (CancellationToken)args[2]!;
            return useAi ? TranslateSentenceWithAi((string)args[0]!, (string?)args[1])
                : TranslateSentence((string)args[0]!, (string?)args[1]);
        }
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
    public async Task UndoNewCollectionRestoresSaveAction()
    {
        var (vm, _, repo, _) = Create();
        repo.Save = _ => Task.FromResult((new Word(), new Occurrence { Id = "new-context", WasCreated = true }, new SyncJob()));
        await vm.StartLookupAsync(Capture());
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.CanUndoSave);
        await vm.UndoSaveCommand.ExecuteAsync(null);
        Assert.Equal("new-context", repo.UndoneId);
        Assert.False(vm.CanUndoSave);
        Assert.True(vm.CanSave);
        Assert.False(vm.IsSaved);
    }

    [Fact]
    public async Task FailedRegenerationPreservesCompletedResultAndEdits()
    {
        var (vm, lookup, _, _) = Create();
        await vm.StartLookupAsync(Capture());
        var original = vm.Definition;
        vm.EditDefinition = "我的释义";
        lookup.Lookup = _ => Task.FromException<TranslationResult>(new IOException("网络中断"));
        await vm.ForceAiCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.True(vm.HasResult);
        Assert.Equal(original, vm.Definition);
        Assert.Equal("我的释义", vm.EditDefinition);
        Assert.True(vm.CanUseQuickFallback);
    }

    [Fact]
    public async Task SaveFailureOffersRetryWithoutLosingResult()
    {
        var (vm, _, repo, _) = Create();
        await vm.StartLookupAsync(Capture());
        repo.Save = _ => Task.FromException<(Word, Occurrence, SyncJob)>(new IOException());
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("重试保存", vm.SaveButtonText);
        Assert.True(vm.CanSave);
        Assert.True(vm.HasResult);
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

    [Fact]
    public async Task RetryAfterAiSentenceFailureKeepsUsingAi()
    {
        var (vm, lookup, _, _) = Create();
        var calls = 0;
        lookup.TranslateSentenceWithAi = (_, _) => ++calls == 1
            ? Task.FromException<string>(new IOException("temporary error"))
            : Task.FromResult("AI retry result");
        lookup.TranslateSentence = (_, _) => throw new InvalidOperationException("Unexpected quick translation");
        await vm.StartLookupAsync(Capture() with { Sentence = "The digital camera." });
        await vm.UseAiSentenceTranslationCommand.ExecuteAsync(null);
        Assert.True(vm.SentenceTranslationFailed);
        Assert.Equal("重试 AI", vm.TranslateSentenceButtonText);
        await vm.TranslateSentenceCommand.ExecuteAsync(null);
        Assert.Equal(2, calls);
        Assert.Equal("AI retry result", vm.ContextTranslation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeavingLookupCancelsSentenceRequestAndIgnoresLateResult(bool close)
    {
        var (vm, lookup, _, _) = Create();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lookup.TranslateSentence = (_, _) => pending.Task;
        await vm.StartLookupAsync(Capture() with { Sentence = "The digital camera." });
        var translate = vm.TranslateSentenceCommand.ExecuteAsync(null);
        var token = lookup.SentenceToken;
        if (close) vm.CancelLookup();
        else await vm.StartLookupAsync(Capture("other"));
        Assert.True(token.IsCancellationRequested);
        pending.SetResult("late result");
        await translate;
        Assert.NotEqual("late result", vm.ContextTranslation);
        Assert.False(vm.IsTranslatingSentence);
        Assert.False(vm.SentenceTranslationFailed);
    }

    [Fact]
    public async Task ManualSentenceTranslationPopulatesContextTranslationAndPersistsOnSave()
    {
        var (vm, lookup, repo, _) = Create();
        lookup.TranslateSentence = (sentence, selection) =>
        {
            Assert.Equal("The digital camera is on the desk.", sentence);
            Assert.Equal("digital", selection);
            return Task.FromResult("数码相机在桌子上。");
        };
        SaveCardCommand? saved = null;
        repo.Save = cmd => { saved = cmd; return Task.FromResult((new Word(), new Occurrence(), new SyncJob())); };

        var captureWithSentence = new CaptureResult(
            "digital",
            "reader.exe",
            "Book",
            new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow,
            Sentence: "The digital camera is on the desk.",
            SentenceOffset: 4);

        await vm.StartLookupAsync(captureWithSentence);
        Assert.Equal(string.Empty, vm.ContextTranslation);
        Assert.True(vm.CanTranslateSentence);

        await vm.TranslateSentenceCommand.ExecuteAsync(null);
        Assert.Equal("数码相机在桌子上。", vm.ContextTranslation);
        Assert.False(vm.CanTranslateSentence);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(saved);
        Assert.Equal("数码相机在桌子上。", saved!.Translation.ContextTranslation);
    }

    [Fact]
    public async Task ManualSentenceTranslationDoesNotOverwriteDraftChangedWhileRequestIsPending()
    {
        var (vm, lookup, _, _) = Create();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lookup.TranslateSentence = (_, _) => pending.Task;
        var captureWithSentence = new CaptureResult(
            "digital",
            "reader.exe",
            "Book",
            new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow,
            Sentence: "The digital camera is on the desk.",
            SentenceOffset: 4);

        await vm.StartLookupAsync(captureWithSentence);
        var translate = vm.TranslateSentenceCommand.ExecuteAsync(null);
        vm.EditTranslation = "用户正在输入的译文";
        pending.SetResult("自动翻译结果");
        await translate;

        Assert.Equal("用户正在输入的译文", vm.EditTranslation);
        Assert.Contains("未覆盖", vm.ActionStatusText);
    }

    [Fact]
    public async Task OpenInMainHandoffPreservesUnsavedContextAndDraftWithoutRequerying()
    {
        var (popupVm, lookup, repo, queue) = Create();
        var capture = new CaptureResult(
            "digital",
            "chrome.exe",
            "AI Docs - Google Chrome",
            new ScreenPoint(10, 20),
            DateTimeOffset.UtcNow,
            Sentence: "Digital tools speed up learning.",
            SentenceOffset: 0);
        await popupVm.StartLookupAsync(capture);
        popupVm.EditDefinition = "数字的（自定义草稿）";
        popupVm.LearningNote = "高频搭配";

        MainWindowLookupHandoff? handoff = null;
        popupVm.OpenInMainHandoffRequested += h => handoff = h;
        popupVm.OpenInMainCommand.Execute(null);

        Assert.NotNull(handoff);
        Assert.False(handoff!.ShouldOpenInLibrary);

        var (mainPopupVm, mainLookup, _, _) = Create();
        var lookupCalls = 0;
        mainLookup.Lookup = _ => { lookupCalls++; return Task.FromResult(Digital()); };
        var mainVm = new WordLookupViewModel(mainPopupVm);
        await mainVm.OpenHandoffAsync(handoff);

        Assert.Equal(0, lookupCalls);
        Assert.Equal("Digital tools speed up learning.", mainVm.Lookup.OriginalSentence);
        Assert.Contains("chrome.exe", mainVm.Lookup.CaptureSourceSummary);
        Assert.Equal("数字的（自定义草稿）", mainVm.Lookup.EditDefinition);
        Assert.Equal("高频搭配", mainVm.Lookup.LearningNote);
        Assert.True(mainVm.Lookup.HasResult);
        Assert.True(mainVm.Lookup.CanSave);
    }

    [Fact]
    public async Task OpenInMainHandoffRoutesSavedContextToLibraryOrLookupBasedOnDraft()
    {
        var path = Path.Combine(Path.GetTempPath(), $"handoff-saved-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new WordCatcher.Infrastructure.Database.SqliteConnectionFactory(path);
            await new WordCatcher.Infrastructure.Database.SqliteMigrationRunner(factory).MigrateAsync();
            var repository = new WordCatcher.Infrastructure.Database.WordRepository(factory);
            var lookup = DispatchProxy.Create<ILookupService, ServiceProxy>();
            var queue = DispatchProxy.Create<IAnkiSyncQueue, ServiceProxy>();
            var settings = DispatchProxy.Create<ISettingsService, ServiceProxy>();
            var vm = new LookupViewModel(lookup, repository, queue, settings, new FakeSpeechService(), new WordCollectionEvents());
            var capture = Capture() with { Sentence = "We use digital systems.", SentenceOffset = 7 };

            await vm.StartLookupAsync(capture);
            await vm.SaveCommand.ExecuteAsync(null);

            MainWindowLookupHandoff? savedHandoff = null;
            vm.OpenInMainHandoffRequested += h => savedHandoff = h;
            vm.OpenInMainCommand.Execute(null);

            Assert.NotNull(savedHandoff);
            Assert.True(savedHandoff!.ShouldOpenInLibrary);
            Assert.False(string.IsNullOrWhiteSpace(savedHandoff.SavedWordId));
            Assert.False(string.IsNullOrWhiteSpace(savedHandoff.SavedOccurrenceId));

            vm.LearningNote = "刚补充但未保存的笔记";
            vm.OpenInMainCommand.Execute(null);
            Assert.False(savedHandoff!.ShouldOpenInLibrary);
            Assert.True(savedHandoff.HasUnsavedEdits);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Core.Services;
using WordCatcher.App.Services;

namespace WordCatcher.App.ViewModels;

public sealed partial class LookupViewModel : ObservableObject
{
    private readonly ILookupService _lookupService;
    private readonly IWordRepository _wordRepository;
    private readonly IAnkiSyncQueue _syncQueue;
    private readonly ISettingsService _settingsService;
    private readonly ISpeechService _speechService;
    private readonly WordCollectionEvents _wordCollectionEvents;
    private readonly IQueryHistory? _history;
    public ObservableCollection<QueryHistoryItem> History { get; } = new();
    public ObservableCollection<OcrWordToken> OcrSentenceTokens { get; } = new();
    public bool ShowOcrWordPicker => OcrSentenceTokens.Count > 1;
    public async Task RefreshHistoryAsync()
    {
        if (_history == null) return;
        try { var items = await _history.ReadAsync(); History.Clear(); foreach (var item in items) History.Add(item); }
        catch (Exception) { ActionStatusText = "查询历史暂时无法读取。"; }
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearningExpression))]
    private LearningAnalysis? _analysis;
    [ObservableProperty] private string _editDefinition = string.Empty;
    [ObservableProperty] private string _editTranslation = string.Empty;
    [ObservableProperty] private string _learningNote = string.Empty;
    [ObservableProperty] private bool _hasSavedContext;
    private LearningEdits? _savedDraft;
    partial void OnEditDefinitionChanged(string value) => RefreshSaveAvailability();
    partial void OnEditTranslationChanged(string value) => RefreshSaveAvailability();
    partial void OnLearningNoteChanged(string value)
    {
        RefreshSaveAvailability();
        OnPropertyChanged(nameof(HasSecondaryContent));
    }
    private void RefreshSaveAvailability()
    {
        if (_savedDraft != null && !IsSaving && HasResult && _currentTranslation != null)
            CanSave = _savedDraft != new LearningEdits(EditDefinition, EditTranslation, LearningNote);
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslateSentenceButtonText))]
    [NotifyPropertyChangedFor(nameof(CanUseAiSentenceTranslation))]
    private bool _isTranslatingSentence;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslateSentenceButtonText))]
    [NotifyPropertyChangedFor(nameof(CanUseAiSentenceTranslation))]
    private bool _sentenceTranslationFailed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranslateSentenceButtonText))]
    [NotifyPropertyChangedFor(nameof(CanUseAiSentenceTranslation))]
    private bool _lastSentenceTranslationUsedAi;
    [ObservableProperty]
    private string _sentenceTranslationError = string.Empty;
    private bool _lastForceAi;
    private string? _undoOccurrenceId;
    private string? _savedWordId;
    private string? _savedOccurrenceId;
    private int _collectionVersion;
    private bool _wasSavedBeforeCollection;
    [ObservableProperty] private bool _canUndoSave;
    [ObservableProperty] private bool _isUndoingSave;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _saveFailed;
    public bool CanUseQuickFallback => HasError && _lastForceAi && _currentCapture != null;
    public string LearningExpression => string.IsNullOrWhiteSpace(Analysis?.Expression) ? Word : Analysis.Expression;
    public bool ShowLearningExpression
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Analysis?.Expression))
                return false;

            var expression = LearningExpression.Trim();
            return IsLongText
                ? !string.Equals(expression, SelectedText.Trim(), StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(expression, OriginalSentence.Trim(), StringComparison.OrdinalIgnoreCase)
                : !string.Equals(expression, Word.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
    public bool HasAnalysis => Analysis != null;
    public bool HasLearningDetails => !string.IsNullOrWhiteSpace(Analysis?.KeyExplanation) || !string.IsNullOrWhiteSpace(Analysis?.UsageNote) || !string.IsNullOrWhiteSpace(Analysis?.Example);
    public string AiButtonText => Source == LookupSource.Ai ? "重新生成" : "AI 详解";
    public bool CanTranslateSentence => !string.IsNullOrWhiteSpace(OriginalSentence) && !IsLongText && string.IsNullOrWhiteSpace(ContextTranslation);
    public bool CanUseAiFallback => HasError && !_lastForceAi && _currentCapture != null;
    public bool CanUseAiSentenceTranslation => SentenceTranslationFailed && !LastSentenceTranslationUsedAi && !IsTranslatingSentence;
    public string TranslateSentenceButtonText => IsTranslatingSentence ? "翻译中…" : SentenceTranslationFailed ? (LastSentenceTranslationUsedAi ? "重试 AI" : "重试快译") : "翻译原句";
    partial void OnSourceChanged(LookupSource value) => OnPropertyChanged(nameof(AiButtonText));
    partial void OnHasErrorChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseAiFallback));
        OnPropertyChanged(nameof(CanUseQuickFallback));
        OnPropertyChanged(nameof(MinimalDisplayText));
    }
    partial void OnAnalysisChanged(LearningAnalysis? value) { OnPropertyChanged(nameof(LearningExpression)); OnPropertyChanged(nameof(HasAnalysis)); OnPropertyChanged(nameof(HasLearningDetails)); }
    partial void OnWordChanged(string value) { OnPropertyChanged(nameof(LearningExpression)); OnPropertyChanged(nameof(IsMediumHeadword)); OnPropertyChanged(nameof(IsExtraLongHeadword)); }
    partial void OnHasSavedContextChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(EncounterBadgeText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimalDisplayText))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "正在查询…";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimalDisplayText))]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearningExpression))]
    private string _selectedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearningExpression))]
    private string _word = string.Empty;

    [ObservableProperty]
    private string _reading = string.Empty;

    [ObservableProperty]
    private string _partOfSpeech = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimalDisplayText))]
    private string _definition = string.Empty;

    [ObservableProperty]
    private string _contextTranslation = string.Empty;

    [ObservableProperty]
    private string? _memoryHook;

    [ObservableProperty]
    private LookupSource _source = LookupSource.OpenDictionary;

    [ObservableProperty]
    private IReadOnlyList<DictPosGroup>? _posGroups;

    [ObservableProperty]
    private IReadOnlyList<DictPosGroup>? _displayPosGroups;

    [ObservableProperty]
    private bool _canExpandDetails;

    [ObservableProperty]
    private string _detailsToggleText = "展开全部词性与义项";

    [ObservableProperty]
    private bool _isDetailsExpanded;

    [ObservableProperty]
    private bool _isMemoryHookExpanded;

    [ObservableProperty]
    private bool _isEditingDefinition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMiniMode))]
    [NotifyPropertyChangedFor(nameof(IsMinimalMode))]
    [NotifyPropertyChangedFor(nameof(IsStandardCard))]
    [NotifyPropertyChangedFor(nameof(IsCompactWordCard))]
    [NotifyPropertyChangedFor(nameof(IsCompactSentenceCard))]
    [NotifyPropertyChangedFor(nameof(IsFullCard))]
    [NotifyPropertyChangedFor(nameof(IsMediumHeadword))]
    [NotifyPropertyChangedFor(nameof(IsExtraLongHeadword))]
    [NotifyPropertyChangedFor(nameof(ShortcutHintText))]
    [NotifyPropertyChangedFor(nameof(MinimalDisplayText))]
    private string _popupMode = UiSettings.PopupModeFull;

    public bool IsMiniMode
    {
        get => PopupMode == UiSettings.PopupModeMini;
        set => PopupMode = value ? UiSettings.PopupModeMini : UiSettings.PopupModeFull;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EncounterBadgeText))]
    private int _encounterCount;

    public bool IsMinimalMode => PopupMode == UiSettings.PopupModeMinimal;
    public bool IsStandardCard => !IsMinimalMode;
    public bool IsCompactWordCard => PopupMode == UiSettings.PopupModeMini && !IsLongText;
    public bool IsCompactSentenceCard => PopupMode == UiSettings.PopupModeMini && IsLongText;
    public bool IsFullCard => PopupMode == UiSettings.PopupModeFull;
    public string ShortcutHintText => IsMinimalMode
        ? string.Empty
        : IsMiniMode
            ? "Tab 展开 · Enter 保存 · 右键关闭"
            : "Enter 保存 · Ctrl+Enter AI";
    public string MinimalDisplayText
    {
        get
        {
            if (HasError) return ErrorMessage;
            if (IsLoading) return "正在翻译…";
            if (!IsLongText && MiniPosGroups is { Count: > 0 } groups)
            {
                var lines = groups
                    .Select(g => string.IsNullOrWhiteSpace(g.DisplayPos)
                        ? g.QuickSummary.Trim()
                        : $"{g.DisplayPos.Trim()} {g.QuickSummary.Trim()}".Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (lines.Count > 0)
                    return string.Join("\n", lines);
            }
            return Definition;
        }
    }
    public string EncounterBadgeText => !IsSaved
        ? string.Empty
        : HasSavedContext
            ? (EncounterCount > 1 ? $"已收藏 · 第 {EncounterCount} 次遇到" : "已收藏")
            : $"已收藏 · 第 {Math.Max(1, EncounterCount) + 1} 次遇到";

    public IReadOnlyList<DictPosGroup>? MiniPosGroups =>
        DisplayPosGroups is { Count: > 0 } ? DisplayPosGroups.Take(2).ToList() : null;

    [RelayCommand]
    private void ExpandToFull()
    {
        PopupMode = UiSettings.PopupModeFull;
    }

    public bool HasStructuredDefinitions => DisplayPosGroups?.Count > 0;

    partial void OnDisplayPosGroupsChanged(IReadOnlyList<DictPosGroup>? value)
    {
        OnPropertyChanged(nameof(HasStructuredDefinitions));
        OnPropertyChanged(nameof(MiniPosGroups));
        OnPropertyChanged(nameof(MinimalDisplayText));
    }

    [ObservableProperty]
    private bool _isSaved;

    [ObservableProperty]
    private bool _canSave;

    [ObservableProperty]
    private string _savedStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveButtonText))]
    private bool _isSaving;

    [ObservableProperty]
    private string _actionStatusText = string.Empty;

    [ObservableProperty]
    private string _syncStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLearningExpression))]
    private string _originalSentence = string.Empty;

    public bool IsLongText => SelectedText.Length > 45 || SelectedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5;
    public bool IsMediumHeadword => !IsLongText && (IsMiniMode ? Word.Length is >= 9 and < 15 : Word.Length is >= 13 and < 22);
    public bool IsExtraLongHeadword => !IsLongText && (IsMiniMode ? Word.Length >= 15 : Word.Length >= 22);
    public bool HasSecondaryContent => !string.IsNullOrWhiteSpace(ContextTranslation)
        || !string.IsNullOrWhiteSpace(MemoryHook)
        || !string.IsNullOrWhiteSpace(OriginalSentence)
        || !string.IsNullOrWhiteSpace(LearningNote)
        || !string.IsNullOrWhiteSpace(CaptureSourceSummary);
    public string CaptureSourceSummary
    {
        get
        {
            if (_currentCapture == null) return string.Empty;
            var process = _currentCapture.SourceProcess?.Trim() ?? string.Empty;
            var title = _currentCapture.SourceWindowTitle?.Trim() ?? string.Empty;
            if (string.Equals(process, "WordCatcher", StringComparison.OrdinalIgnoreCase) && title == "查词页面")
                return string.Empty;
            return string.Join(" · ", new[] { process, title }.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }
    public string SaveButtonText => IsSaving ? "保存中…" : SaveFailed ? "重试保存" : !CanSave && IsSaved ? "已收藏" : HasSavedContext ? "更新当前语境" : IsSaved ? "保存新语境" : "存入生词库";
    partial void OnIsSavedChanged(bool value)
    {
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(EncounterBadgeText));
    }
    partial void OnCanSaveChanged(bool value) => OnPropertyChanged(nameof(SaveButtonText));
    partial void OnSelectedTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsLongText));
        OnPropertyChanged(nameof(IsCompactWordCard));
        OnPropertyChanged(nameof(IsCompactSentenceCard));
        OnPropertyChanged(nameof(IsMediumHeadword));
        OnPropertyChanged(nameof(IsExtraLongHeadword));
        OnPropertyChanged(nameof(CanTranslateSentence));
        OnPropertyChanged(nameof(MinimalDisplayText));
    }
    partial void OnContextTranslationChanged(string value) { OnPropertyChanged(nameof(HasSecondaryContent)); OnPropertyChanged(nameof(CanTranslateSentence)); }
    partial void OnMemoryHookChanged(string? value) => OnPropertyChanged(nameof(HasSecondaryContent));
    partial void OnOriginalSentenceChanged(string value) { OnPropertyChanged(nameof(HasSecondaryContent)); OnPropertyChanged(nameof(CanTranslateSentence)); }
    private long _actionVersion;

    private async Task ShowActionStatusAsync(string message)
    {
        var version = ++_actionVersion;
        ActionStatusText = message;
        await Task.Delay(2500).ConfigureAwait(true);
        if (version == _actionVersion) ActionStatusText = string.Empty;
    }

    public event Action? RequestClose;
    public event Action<string, bool>? OpenInMainRequested;
    public event Action<MainWindowLookupHandoff>? OpenInMainHandoffRequested;

    public bool KeepOpenAfterSave { get; set; }

    private CaptureResult? _currentCapture;
    private TranslationResult? _currentTranslation;
    private CancellationTokenSource? _lookupCancellation;
    private CancellationTokenSource? _sentenceCancellation;
    private long _lookupVersion;

    public LookupViewModel(
        ILookupService lookupService,
        IWordRepository wordRepository,
        IAnkiSyncQueue syncQueue,
        ISettingsService settingsService,
        ISpeechService speechService,
        WordCollectionEvents wordCollectionEvents, IQueryHistory? history = null)
    {
        _lookupService = lookupService;
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
        _settingsService = settingsService;
        _speechService = speechService;
        _wordCollectionEvents = wordCollectionEvents;
        _history = history;
    }

    public Task StartLookupAsync(CaptureResult capture, bool forceAi = false, bool bypassCache = false, TranslationResult? historyResult = null)
        => StartLookupCoreAsync(capture, forceAi, bypassCache, historyResult, restoredEdits: null, recordHistory: true);

    [RelayCommand]
    private Task SelectOcrTokenAsync(OcrWordToken? token)
    {
        if (token is null)
            return Task.CompletedTask;

        return StartLookupAsync(new CaptureResult(
            token.Text,
            "ScreenOcr",
            _currentCapture?.SourceWindowTitle ?? "截屏 OCR 取词",
            _currentCapture?.CursorPosition ?? new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow,
            Sentence: _currentCapture?.Sentence ?? token.Text,
            SentenceOffset: token.Offset));
    }

    private void UpdateOcrWordPicker(CaptureResult capture)
    {
        var isOcr = string.Equals(capture.SourceProcess, "ScreenOcr", StringComparison.OrdinalIgnoreCase);
        if (isOcr && capture.SelectedText != capture.Sentence)
            return;

        OcrSentenceTokens.Clear();
        if (isOcr)
        {
            var tokens = OcrTokenExtractor.ExtractWords(capture.Sentence);
            if (tokens.Count > 1)
            {
                foreach (var token in tokens)
                    OcrSentenceTokens.Add(token);
            }
        }

        OnPropertyChanged(nameof(OcrSentenceTokens));
        OnPropertyChanged(nameof(ShowOcrWordPicker));
    }

    public async Task RestoreHandoffAsync(MainWindowLookupHandoff handoff)
    {
        if (handoff.Result == null)
        {
            await StartLookupCoreAsync(handoff.Capture, handoff.WasForceAi, bypassCache: false, historyResult: null, restoredEdits: handoff.Edits, recordHistory: false).ConfigureAwait(true);
            return;
        }

        await StartLookupCoreAsync(handoff.Capture, handoff.WasForceAi, bypassCache: false, historyResult: handoff.Result, restoredEdits: handoff.Edits, recordHistory: false).ConfigureAwait(true);
        if (HasResult)
        {
            UpdateStructuredDetails(handoff.IsDetailsExpanded);
            IsMemoryHookExpanded = handoff.IsMemoryHookExpanded && !string.IsNullOrWhiteSpace(MemoryHook);
        }
    }

    private async Task StartLookupCoreAsync(
        CaptureResult capture,
        bool forceAi,
        bool bypassCache,
        TranslationResult? historyResult,
        LearningEdits? restoredEdits,
        bool recordHistory)
    {
        _sentenceCancellation?.Cancel();
        var keepEdits = restoredEdits != null || (_currentTranslation != null && _currentCapture?.SelectedText == capture.SelectedText
            && _currentCapture?.Sentence == capture.Sentence && _currentCapture?.SentenceOffset == capture.SentenceOffset
            && _currentCapture?.SourceUri == capture.SourceUri);
        var previousEdits = restoredEdits ?? (keepEdits ? new LearningEdits(
            EditDefinition != _currentTranslation?.Definition ? EditDefinition : null,
            EditTranslation != _currentTranslation?.ContextTranslation ? EditTranslation : null, LearningNote) : null);
        var previousSavedContext = HasSavedContext;
        var previousSaved = IsSaved;
        _lastForceAi = forceAi;
        OnPropertyChanged(nameof(CanUseAiFallback));
        Analysis = null;
        HasSavedContext = false;
        _savedDraft = null;
        _savedWordId = null;
        _savedOccurrenceId = null;
        IsTranslatingSentence = false;
        SentenceTranslationFailed = false;
        LastSentenceTranslationUsedAi = false;
        SentenceTranslationError = string.Empty;
        ++_lookupVersion;
        var previousLookup = _lookupCancellation;
        var currentLookup = new CancellationTokenSource();
        _lookupCancellation = currentLookup;
        previousLookup?.Cancel();

        var previousTranslation = forceAi && _currentCapture != null &&
                                  string.Equals(_currentCapture.SelectedText, capture.SelectedText, StringComparison.OrdinalIgnoreCase)
            ? _currentTranslation
            : null;

        _currentCapture = capture;
        OnPropertyChanged(nameof(CanUseAiFallback));
        OnPropertyChanged(nameof(CaptureSourceSummary));
        SelectedText = capture.SelectedText;
        Word = capture.SelectedText;
        Reading = string.Empty;
        PartOfSpeech = string.Empty;
        PosGroups = null;
        DisplayPosGroups = null;
        CanExpandDetails = false;
        IsDetailsExpanded = false;
        IsMemoryHookExpanded = false;
        IsEditingDefinition = false;
        Definition = string.Empty;
        ContextTranslation = string.Empty;
        MemoryHook = null;
        _currentTranslation = null;
        IsLoading = true;
        HasResult = false;
        HasError = false;
        IsSaved = false;
        EncounterCount = 0;
        SaveFailed = false;
        CanUndoSave = false;
        _undoOccurrenceId = null;
        IsSaving = false;
        ++_actionVersion;
        ActionStatusText = string.Empty;
        SyncStatusText = string.Empty;
        OriginalSentence = capture.Sentence == capture.SelectedText ? string.Empty : capture.Sentence;
        UpdateOcrWordPicker(capture);
        CanSave = false;
        SavedStatusText = string.Empty;
        StatusText = forceAi ? "正在结合语境生成 AI 详解…" : "正在查询词典与翻译…";

        try
        {
            var progress = new Progress<TranslationResult>(partial =>
            {
                if (!ReferenceEquals(_lookupCancellation, currentLookup) || currentLookup.IsCancellationRequested || _currentTranslation != null) return;
                Word = partial.Word;
                Reading = partial.Reading;
                PartOfSpeech = partial.PartOfSpeech;
                Definition = partial.Definition;
                ContextTranslation = partial.ContextTranslation;
                Analysis = partial.Analysis;
                Source = partial.Source;
                HasResult = true;
            });
            var result = historyResult ?? (_lookupService is IStreamingLookupService streaming
                ? await streaming.LookupStreamingAsync(capture, forceAi, progress, bypassCache, currentLookup.Token).ConfigureAwait(true)
                : await _lookupService.LookupAsync(capture, forceAi, currentLookup.Token).ConfigureAwait(true));
            if (!ReferenceEquals(_lookupCancellation, currentLookup))
                return;

            if (previousTranslation != null)
            {
                result = result with
                {
                    Reading = !string.IsNullOrWhiteSpace(result.Reading) ? result.Reading : previousTranslation.Reading,
                    MemoryHook = !string.IsNullOrWhiteSpace(result.MemoryHook) ? result.MemoryHook : previousTranslation.MemoryHook,
                    PosGroups = result.PosGroups is { Count: > 0 } ? result.PosGroups : previousTranslation.PosGroups
                };
            }

            _currentTranslation = result;

            Word = result.Word;
            Reading = result.Reading;
            PartOfSpeech = result.PartOfSpeech;
            Definition = result.Definition;
            ContextTranslation = result.ContextTranslation;
            MemoryHook = result.MemoryHook;
            Analysis = result.Analysis;
            EditDefinition = previousEdits?.Definition ?? result.Definition;
            EditTranslation = previousEdits?.ContextTranslation ?? result.ContextTranslation;
            LearningNote = previousEdits?.Note ?? string.Empty;
            Source = result.Source;
            PosGroups = result.PosGroups;
            UpdateStructuredDetails(expanded: false);

            HasResult = true;
            CanSave = true;
            if (recordHistory && _history != null)
            {
                try { await _history.RecordAsync(capture, result, currentLookup.Token); await RefreshHistoryAsync(); }
                catch (Exception) when (!currentLookup.IsCancellationRequested) { }
                if (!ReferenceEquals(_lookupCancellation, currentLookup)) return;
            }
            // 收藏检查失败不影响已经取得的查询结果。
            try
            {
                var existing = await _wordRepository.FindWordAsync(result.Word, currentLookup.Token).ConfigureAwait(true);
                if (!ReferenceEquals(_lookupCancellation, currentLookup)) return;
                IsSaved = existing != null;
                _savedWordId = existing?.Id;
                EncounterCount = 0;
                if (existing != null)
                {
                    var contexts = await _wordRepository.GetOccurrencesByWordIdAsync(existing.Id, currentLookup.Token).ConfigureAwait(true);
                    if (!ReferenceEquals(_lookupCancellation, currentLookup)) return;
                    EncounterCount = contexts.Count;
                    var saved = contexts.FirstOrDefault(o => o.SelectedText == capture.SelectedText
                        && o.Sentence == (string.IsNullOrWhiteSpace(capture.Sentence) ? capture.SelectedText : capture.Sentence)
                        && o.SelectionOffset == capture.SentenceOffset && o.SourceUri == capture.SourceUri);
                    HasSavedContext = saved != null;
                    _savedOccurrenceId = saved?.Id;
                    if (saved != null)
                    {
                        var baseline = new LearningEdits(
                            saved.Edits?.Definition ?? result.Definition,
                            saved.Edits?.ContextTranslation ?? result.ContextTranslation,
                            saved.Edits?.Note ?? string.Empty);
                        _savedDraft = baseline;
                        if (previousEdits == null)
                        {
                            EditDefinition = baseline.Definition ?? result.Definition;
                            EditTranslation = baseline.ContextTranslation ?? result.ContextTranslation;
                            LearningNote = baseline.Note;
                        }
                        RefreshSaveAvailability();
                    }
                }
                if (IsSaved && !HasSavedContext) SavedStatusText = "该词已在生词库中，可补充保存当前语境。";
                else if (HasSavedContext) SavedStatusText = CanSave ? "已修改草稿，可点击更新当前语境。" : "该语境已收藏在生词库。";
            }
            catch (Exception) when (!currentLookup.IsCancellationRequested) { }
        }
        catch (OperationCanceledException) when (currentLookup.IsCancellationRequested)
        {
            // A newer lookup replaced this one, or the window was closed.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_lookupCancellation, currentLookup))
            {
                HasError = true;
                HasResult = false;
                CanSave = false;
                ErrorMessage = ex.Message;
                if (previousTranslation != null && keepEdits)
                {
                    _currentTranslation = previousTranslation;
                    Word = previousTranslation.Word;
                    Reading = previousTranslation.Reading;
                    PartOfSpeech = previousTranslation.PartOfSpeech;
                    Definition = previousTranslation.Definition;
                    ContextTranslation = previousTranslation.ContextTranslation;
                    MemoryHook = previousTranslation.MemoryHook;
                    PosGroups = previousTranslation.PosGroups;
                    Analysis = previousTranslation.Analysis;
                    Source = previousTranslation.Source;
                    EditDefinition = previousEdits?.Definition ?? previousTranslation.Definition;
                    EditTranslation = previousEdits?.ContextTranslation ?? previousTranslation.ContextTranslation;
                    LearningNote = previousEdits?.Note ?? string.Empty;
                    HasSavedContext = previousSavedContext;
                    IsSaved = previousSaved;
                    HasResult = true;
                    CanSave = true;
                    UpdateStructuredDetails(false);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_lookupCancellation, currentLookup))
            {
                _lookupCancellation = null;
                IsLoading = false;
            }
            currentLookup.Dispose();
        }
    }

    public void CancelLookup()
    {
        ++_lookupVersion;
        _sentenceCancellation?.Cancel();
        IsTranslatingSentence = false;
        var lookup = _lookupCancellation;
        _lookupCancellation = null;
        lookup?.Cancel();
    }

    [RelayCommand]
    private void ToggleEditDefinition()
    {
        if (IsMiniMode) IsMiniMode = false;
        IsEditingDefinition = !IsEditingDefinition;
    }

    [RelayCommand]
    private Task TranslateSentenceAsync() => TranslateSentenceCoreAsync(useAi: SentenceTranslationFailed && LastSentenceTranslationUsedAi);

    [RelayCommand]
    private Task UseAiSentenceTranslationAsync() => TranslateSentenceCoreAsync(useAi: true);

    private async Task TranslateSentenceCoreAsync(bool useAi)
    {
        if (IsTranslatingSentence || string.IsNullOrWhiteSpace(OriginalSentence))
            return;

        var version = _lookupVersion;
        var targetSentence = OriginalSentence;
        var targetSelection = SelectedText;
        var translationAtStart = EditTranslation;
        using var cancellation = new CancellationTokenSource();
        _sentenceCancellation = cancellation;
        IsTranslatingSentence = true;
        SentenceTranslationFailed = false;
        LastSentenceTranslationUsedAi = useAi;
        SentenceTranslationError = string.Empty;
        try
        {
            var translated = useAi
                ? await _lookupService.TranslateSentenceWithAiAsync(targetSentence, targetSelection, cancellation.Token).ConfigureAwait(true)
                : await _lookupService.TranslateSentenceAsync(targetSentence, targetSelection, cancellation.Token).ConfigureAwait(true);
            if (version != _lookupVersion)
                return;
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException(useAi ? "AI 翻译未返回结果。" : "在线快译未返回结果。请重试，或改用 AI。");

            if (EditTranslation != translationAtStart)
            {
                ActionStatusText = "译文草稿已修改，未覆盖你的输入。";
                return;
            }

            ContextTranslation = translated.Trim();
            EditTranslation = translated.Trim();
            if (_currentTranslation != null)
            {
                _currentTranslation = _currentTranslation with { ContextTranslation = translated.Trim() };
            }
            if (IsSaved && !IsSaving)
            {
                CanSave = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (version == _lookupVersion)
            {
                SentenceTranslationFailed = true;
                SentenceTranslationError = $"{(useAi ? "AI 翻译" : "在线快译")}失败：{ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_sentenceCancellation, cancellation)) _sentenceCancellation = null;
            if (version == _lookupVersion)
            {
                IsTranslatingSentence = false;
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || _currentCapture == null || _currentTranslation == null)
            return;

        CanSave = false;
        IsSaving = true;
        SaveFailed = false;
        IsEditingDefinition = false;
        var wasSaved = IsSaved;
        var hadSavedContext = HasSavedContext;
        var savedDraft = new LearningEdits(EditDefinition, EditTranslation, LearningNote);
        var collectionVersion = ++_collectionVersion;
        SavedStatusText = "正在保存到生词库…";
        var version = _lookupVersion;

        try
        {
            if (string.IsNullOrWhiteSpace(EditDefinition)) throw new InvalidOperationException("释义不能为空。");
            var edits = new LearningEdits(
                EditDefinition != _currentTranslation.Definition ? EditDefinition.Trim() : null,
                EditTranslation != _currentTranslation.ContextTranslation ? EditTranslation.Trim() : null,
                LearningNote.Trim());
            var cmd = new SaveCardCommand(_currentCapture, _currentTranslation, edits, HasSavedContext);
            var (savedWord, occ, syncJob) = await _wordRepository.SaveAsync(cmd).ConfigureAwait(true);
            _wordCollectionEvents.NotifyWordSaved();
            _syncQueue.Enqueue(syncJob.Id);
            if (version != _lookupVersion) return;

            IsSaved = true;
            HasSavedContext = true;
            if (occ.WasCreated) EncounterCount = Math.Max(1, EncounterCount + 1);
            else if (EncounterCount == 0) EncounterCount = 1;
            _savedWordId = savedWord.Id;
            _savedOccurrenceId = occ.Id;
            _savedDraft = savedDraft;
            _wasSavedBeforeCollection = wasSaved;
            _undoOccurrenceId = occ.WasCreated ? occ.Id : null;
            CanUndoSave = _undoOccurrenceId != null;
            IsSaving = false;
            RefreshSaveAvailability();
            SavedStatusText = hadSavedContext ? "已更新当前语境" : wasSaved ? "已新增语境记录" : "已存入生词库";
            SyncStatusText = _settingsService.Current.Anki.Enabled ? "已保存在本地，将在后台同步到 Anki" : string.Empty;

            if (_settingsService.Current.Ui.ClosePopupAfterSave)
            {
                await Task.Delay(CanUndoSave ? 5000 : 800).ConfigureAwait(true);
                if (version == _lookupVersion && collectionVersion == _collectionVersion && !KeepOpenAfterSave && !CanSave && !IsUndoingSave)
                    RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            if (version != _lookupVersion) return;
            IsSaving = false;
            CanSave = true;
            SaveFailed = true;
            SavedStatusText = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UndoSaveAsync()
    {
        if (!CanUndoSave || _undoOccurrenceId == null || IsUndoingSave) return;
        var id = _undoOccurrenceId;
        ++_collectionVersion;
        var version = _lookupVersion;
        IsUndoingSave = true;
        try
        {
            await _wordRepository.UndoNewOccurrenceAsync(id).ConfigureAwait(true);
            _wordCollectionEvents.NotifyWordSaved();
            if (version != _lookupVersion) return;
            CanUndoSave = false;
            _undoOccurrenceId = null;
            _savedOccurrenceId = null;
            if (!_wasSavedBeforeCollection) _savedWordId = null;
            IsSaved = _wasSavedBeforeCollection;
            HasSavedContext = false;
            EncounterCount = _wasSavedBeforeCollection ? Math.Max(1, EncounterCount - 1) : 0;
            _savedDraft = null;
            CanSave = true;
            SavedStatusText = "已撤销本次本地收藏";
            SyncStatusText = _settingsService.Current.Anki.Enabled ? "已发送到 Anki 的卡片可在 Anki 中删除。" : string.Empty;
        }
        catch (Exception ex)
        {
            if (version == _lookupVersion) SavedStatusText = $"撤销失败：{ex.Message}";
        }
        finally { IsUndoingSave = false; }
    }

    [RelayCommand]
    private async Task QuickFallbackAsync()
    {
        if (_currentCapture != null)
            await StartLookupAsync(_currentCapture, forceAi: false, bypassCache: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ForceAiAsync()
    {
        if (_currentCapture != null)
        {
            IsMiniMode = false;
            await StartLookupAsync(_currentCapture, forceAi: true, bypassCache: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_currentCapture != null)
        {
            await StartLookupAsync(_currentCapture, forceAi: _lastForceAi, bypassCache: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        UpdateStructuredDetails(!IsDetailsExpanded);
    }

    private void UpdateStructuredDetails(bool expanded)
    {
        var groups = PosGroups?.Where(group => group.Meanings.Count > 0 || !string.IsNullOrWhiteSpace(group.Summary))
            .Select(group => group with { Meanings = group.Meanings.OrderBy(m => m.PriorityRank).ToList() }).ToList();
        DisplayPosGroups = groups;
        var count = groups?.Sum(group => group.Meanings.Count) ?? 0;
        CanExpandDetails = count > 0;
        IsDetailsExpanded = expanded && CanExpandDetails;
        DetailsToggleText = IsDetailsExpanded ? "收起完整释义" : $"完整释义（{count}）";
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void OpenInMain()
    {
        var query = IsSaved && !string.IsNullOrWhiteSpace(Word) ? Word : SelectedText;
        if (string.IsNullOrWhiteSpace(query)) return;
        if (_currentCapture != null)
        {
            var hasCompletedResult = HasResult && _currentTranslation != null;
            LearningEdits? edits = hasCompletedResult
                ? new LearningEdits(
                    EditDefinition != _currentTranslation!.Definition ? EditDefinition : null,
                    EditTranslation != _currentTranslation.ContextTranslation ? EditTranslation : null,
                    LearningNote)
                : !string.IsNullOrWhiteSpace(LearningNote)
                    ? new LearningEdits(null, null, LearningNote)
                    : null;
            var hasUnsavedEdits = hasCompletedResult && CanSave && (
                HasSavedContext
                || edits?.Definition != null
                || edits?.ContextTranslation != null
                || !string.IsNullOrWhiteSpace(edits?.Note));
            var handoff = new MainWindowLookupHandoff(
                _currentCapture,
                _currentTranslation,
                edits,
                IsSaved,
                HasSavedContext,
                hasUnsavedEdits,
                _savedWordId,
                _savedOccurrenceId,
                IsDetailsExpanded,
                IsMemoryHookExpanded,
                _lastForceAi);
            OpenInMainHandoffRequested?.Invoke(handoff);
        }
        OpenInMainRequested?.Invoke(query, IsSaved);
    }

    [RelayCommand]
    private void Speak()
    {
        ++_actionVersion;
        ActionStatusText = string.Empty;

        try
        {
            var text = !string.IsNullOrWhiteSpace(Word) ? Word : SelectedText;
            _speechService.Speak(text);
        }
        catch (Exception ex)
        {
            ActionStatusText = $"无法朗读：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyResult()
    {
        if (!HasResult)
            return;

        var heading = string.Join(" ", new[] { Word, Reading, PartOfSpeech }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var lines = new List<string> { heading, Definition };
        if (!string.IsNullOrWhiteSpace(ContextTranslation)
            && !string.Equals(ContextTranslation.Trim(), Definition.Trim(), StringComparison.Ordinal))
        {
            lines.Add(ContextTranslation);
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line))));
            _ = ShowActionStatusAsync("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            ++_actionVersion;
            ActionStatusText = $"复制失败：{ex.Message}";
        }
    }
}

public sealed record MainWindowLookupHandoff(
    CaptureResult Capture,
    TranslationResult? Result,
    LearningEdits? Edits,
    bool IsSaved,
    bool HasSavedContext,
    bool HasUnsavedEdits,
    string? SavedWordId,
    string? SavedOccurrenceId,
    bool IsDetailsExpanded,
    bool IsMemoryHookExpanded,
    bool WasForceAi)
{
    public bool ShouldOpenInLibrary => IsSaved && HasSavedContext && !HasUnsavedEdits;
}

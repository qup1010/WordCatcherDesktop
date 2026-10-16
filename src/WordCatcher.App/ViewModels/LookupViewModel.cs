using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
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
    public System.Collections.ObjectModel.ObservableCollection<QueryHistoryItem> History { get; } = new();
    public async Task RefreshHistoryAsync()
    {
        if (_history == null) return;
        try { var items = await _history.ReadAsync(); History.Clear(); foreach (var item in items) History.Add(item); }
        catch (Exception) { ActionStatusText = "查询历史暂时无法读取。"; }
    }
    [ObservableProperty] private LearningAnalysis? _analysis;
    [ObservableProperty] private string _editDefinition = string.Empty;
    [ObservableProperty] private string _editTranslation = string.Empty;
    [ObservableProperty] private string _learningNote = string.Empty;
    [ObservableProperty] private bool _hasSavedContext;
    private bool _lastForceAi;
    public string LearningExpression => string.IsNullOrWhiteSpace(Analysis?.Expression) ? Word : Analysis.Expression;
    public bool HasAnalysis => Analysis != null;
    public bool HasLearningDetails => !string.IsNullOrWhiteSpace(Analysis?.KeyExplanation) || !string.IsNullOrWhiteSpace(Analysis?.UsageNote) || !string.IsNullOrWhiteSpace(Analysis?.Example);
    public string AiButtonText => Source == LookupSource.Ai ? "重新生成" : "AI 详解";
    partial void OnSourceChanged(LookupSource value) => OnPropertyChanged(nameof(AiButtonText));
    partial void OnAnalysisChanged(LearningAnalysis? value) { OnPropertyChanged(nameof(LearningExpression)); OnPropertyChanged(nameof(HasAnalysis)); OnPropertyChanged(nameof(HasLearningDetails)); }
    partial void OnWordChanged(string value) => OnPropertyChanged(nameof(LearningExpression));
    partial void OnHasSavedContextChanged(bool value) => OnPropertyChanged(nameof(SaveButtonText));

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "正在解析...";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _selectedText = string.Empty;

    [ObservableProperty]
    private string _word = string.Empty;

    [ObservableProperty]
    private string _reading = string.Empty;

    [ObservableProperty]
    private string _partOfSpeech = string.Empty;

    [ObservableProperty]
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

    public bool HasStructuredDefinitions => DisplayPosGroups?.Count > 0;

    partial void OnDisplayPosGroupsChanged(IReadOnlyList<DictPosGroup>? value)
        => OnPropertyChanged(nameof(HasStructuredDefinitions));

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
    private string _originalSentence = string.Empty;

    public bool IsLongText => SelectedText.Length > 45 || SelectedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5;
    public bool HasSecondaryContent => !string.IsNullOrWhiteSpace(ContextTranslation) || !string.IsNullOrWhiteSpace(MemoryHook) || !string.IsNullOrWhiteSpace(OriginalSentence);
    public string SaveButtonText => IsSaving ? "保存中…" : !CanSave && IsSaved ? "已收藏 ✓" : HasSavedContext ? "更新这条语境" : IsSaved ? "保存新语境" : "存入生词库";
    partial void OnIsSavedChanged(bool value) => OnPropertyChanged(nameof(SaveButtonText));
    partial void OnCanSaveChanged(bool value) => OnPropertyChanged(nameof(SaveButtonText));
    partial void OnSelectedTextChanged(string value) => OnPropertyChanged(nameof(IsLongText));
    partial void OnContextTranslationChanged(string value) => OnPropertyChanged(nameof(HasSecondaryContent));
    partial void OnMemoryHookChanged(string? value) => OnPropertyChanged(nameof(HasSecondaryContent));
    partial void OnOriginalSentenceChanged(string value) => OnPropertyChanged(nameof(HasSecondaryContent));
    private long _actionVersion;

    private async Task ShowActionStatusAsync(string message)
    {
        var version = ++_actionVersion;
        ActionStatusText = message;
        await Task.Delay(2500).ConfigureAwait(true);
        if (version == _actionVersion) ActionStatusText = string.Empty;
    }

    public event Action? RequestClose;

    public bool KeepOpenAfterSave { get; set; }

    private CaptureResult? _currentCapture;
    private TranslationResult? _currentTranslation;
    private CancellationTokenSource? _lookupCancellation;
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

    public async Task StartLookupAsync(CaptureResult capture, bool forceAi = false, bool bypassCache = false, TranslationResult? historyResult = null)
    {
        var keepEdits = _currentTranslation != null && _currentCapture?.SelectedText == capture.SelectedText
            && _currentCapture?.Sentence == capture.Sentence && _currentCapture?.SentenceOffset == capture.SentenceOffset
            && _currentCapture?.SourceUri == capture.SourceUri;
        var previousEdits = keepEdits ? new LearningEdits(
            EditDefinition != _currentTranslation?.Definition ? EditDefinition : null,
            EditTranslation != _currentTranslation?.ContextTranslation ? EditTranslation : null, LearningNote) : null;
        _lastForceAi = forceAi;
        Analysis = null;
        HasSavedContext = false;
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
        IsSaving = false;
        ++_actionVersion;
        ActionStatusText = string.Empty;
        SyncStatusText = string.Empty;
        OriginalSentence = capture.Sentence == capture.SelectedText ? string.Empty : capture.Sentence;
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
            if (_history != null)
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
                if (existing != null)
                {
                    var contexts = await _wordRepository.GetOccurrencesByWordIdAsync(existing.Id, currentLookup.Token).ConfigureAwait(true);
                    if (!ReferenceEquals(_lookupCancellation, currentLookup)) return;
                    var saved = contexts.FirstOrDefault(o => o.SelectedText == capture.SelectedText
                        && o.Sentence == (string.IsNullOrWhiteSpace(capture.Sentence) ? capture.SelectedText : capture.Sentence)
                        && o.SelectionOffset == capture.SentenceOffset && o.SourceUri == capture.SourceUri);
                    HasSavedContext = saved != null;
                    if (saved?.Edits != null && previousEdits == null)
                    {
                        EditDefinition = saved.Edits.Definition ?? result.Definition;
                        EditTranslation = saved.Edits.ContextTranslation ?? result.ContextTranslation;
                        LearningNote = saved.Edits.Note;
                    }
                }
                if (IsSaved) SavedStatusText = "该词已在生词库中，可补充保存当前语境。";
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
        var lookup = _lookupCancellation;
        _lookupCancellation = null;
        lookup?.Cancel();
    }

    [RelayCommand]
    private void ToggleEditDefinition()
    {
        IsEditingDefinition = !IsEditingDefinition;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || _currentCapture == null || _currentTranslation == null)
            return;

        CanSave = false;
        IsSaving = true;
        IsEditingDefinition = false;
        var wasSaved = IsSaved;
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
            IsSaving = false;
            SavedStatusText = HasSavedContext ? "已更新当前语境" : wasSaved ? "已新增语境记录" : "已存入生词库";
            SyncStatusText = _settingsService.Current.Anki.Enabled ? "已保存在本地，将在后台同步到 Anki" : string.Empty;

            if (_settingsService.Current.Ui.ClosePopupAfterSave)
            {
                await Task.Delay(800).ConfigureAwait(true);
                if (version == _lookupVersion && !KeepOpenAfterSave)
                    RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            if (version != _lookupVersion) return;
            IsSaving = false;
            CanSave = true;
            SavedStatusText = $"保存失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ForceAiAsync()
    {
        if (_currentCapture != null)
        {
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
            ActionStatusText = $"朗读不可用: {ex.Message}";
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
            ActionStatusText = $"复制失败: {ex.Message}";
        }
    }
}

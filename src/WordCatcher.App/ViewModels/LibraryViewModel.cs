using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using WordCatcher.Core.Enums;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IWordRepository _wordRepository;
    private readonly ISpeechService? _speechService;
    private readonly IWebDavSyncService? _webDavSyncService;
    private readonly ILookupService? _lookupService;
    private readonly string? _preferencesPath;
    public ObservableCollection<Word> SelectedWords { get; } = new();
    [ObservableProperty] private int _matchingWordCount;
    public string ResultCountText => HasMore ? $"{MatchingWordCount} 个词条 · 已显示 {Words.Count}" : $"{MatchingWordCount} 个词条";
    public int ActiveFilterCount => (SelectedFilterIndex > 0 ? 1 : 0) + (!string.IsNullOrWhiteSpace(SourceFilter) ? 1 : 0);
    public string FilterButtonText => ActiveFilterCount > 0 ? $"筛选 · {ActiveFilterCount}" : "筛选";
    public string FilterTagText => SelectedFilterIndex is > 0 and < 6 ? new[] { "", "待同步", "同步失败", "最近 7 天收藏", "有备注", "无备注" }[SelectedFilterIndex] + " ×" : string.Empty;
    public string SourceTagText => string.IsNullOrWhiteSpace(SourceFilter) ? string.Empty : SourceFilter.Trim() + " ×";
    private void RefreshFilterLabels()
    {
        OnPropertyChanged(nameof(FilterButtonText));
        OnPropertyChanged(nameof(FilterTagText));
        OnPropertyChanged(nameof(SourceTagText));
    }
    [RelayCommand] private void ClearStatusFilter() => SelectedFilterIndex = 0;
    [RelayCommand] private void ClearSourceFilter() => SourceFilter = string.Empty;
    partial void OnMatchingWordCountChanged(int value) => OnPropertyChanged(nameof(ResultCountText));
    [ObservableProperty] private int _selectedSortIndex;
    [ObservableProperty] private string _sourceFilter = string.Empty;
    partial void OnSelectedSortIndexChanged(int value) => _ = SearchAsync();
    partial void OnSourceFilterChanged(string value) { RefreshFilterLabels(); OnSearchQueryChanged(SearchQuery); }
    public string BatchSelectionText => $"已选 {SelectedWords.Count} 个 · Ctrl / Shift 多选";
    public void SetSelectedWords(IEnumerable<Word> words)
    {
        SelectedWords.Clear();
        foreach (var word in words) SelectedWords.Add(word);
        OnPropertyChanged(nameof(BatchSelectionText));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }
    private const int PageSize = 100;
    private int _loadVersion;
    private int _occurrenceVersion;
    private CancellationTokenSource? _searchDelay;
    private bool _refreshPending;

    [ObservableProperty] private Occurrence? _editingOccurrence;
    [ObservableProperty] private string _contextDefinition = string.Empty;
    [ObservableProperty] private string _contextTranslation = string.Empty;
    [ObservableProperty] private string _contextNote = string.Empty;
    [ObservableProperty] private bool _isSavingContext;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextSentenceButtonText))]
    [NotifyPropertyChangedFor(nameof(CanUseAiContextSentence))]
    private bool _isTranslatingContextSentence;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextSentenceButtonText))]
    [NotifyPropertyChangedFor(nameof(CanUseAiContextSentence))]
    private bool _contextSentenceTranslationFailed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextSentenceButtonText))]
    [NotifyPropertyChangedFor(nameof(CanUseAiContextSentence))]
    private bool _lastContextSentenceUsedAi;
    private Occurrence? _lastContextSentenceOccurrence;
    private bool _lastContextSentenceFromEditor;
    public string ContextSentenceButtonText => IsTranslatingContextSentence
        ? "翻译中…"
        : ContextSentenceTranslationFailed
            ? LastContextSentenceUsedAi ? "重试 AI" : "重试快译"
            : "翻译原句";
    public bool CanUseAiContextSentence => ContextSentenceTranslationFailed && !LastContextSentenceUsedAi && !IsTranslatingContextSentence;
    public bool ShowContextSentenceActions => ContextSentenceTranslationFailed && !IsEditingContext;
    public bool IsEditingContext => EditingOccurrence != null;
    partial void OnEditingOccurrenceChanged(Occurrence? value)
    {
        OnPropertyChanged(nameof(IsEditingContext));
        OnPropertyChanged(nameof(ShowContextSentenceActions));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DeleteWordCommand.NotifyCanExecuteChanged();
        UndoDeleteCommand.NotifyCanExecuteChanged();
        RefreshAfterEditing();
    }
    partial void OnIsSavingContextChanged(bool value)
    {
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DeleteWordCommand.NotifyCanExecuteChanged();
        UndoDeleteCommand.NotifyCanExecuteChanged();
        RefreshAfterEditing();
    }

    private void RefreshAfterEditing()
    {
        if (!IsEditing && !IsEditingContext && !IsSavingContext && !IsChangingCollection && _refreshPending)
        {
            _refreshPending = false;
            _ = LoadWordsAsync();
        }
    }

    // ── 遮挡释义 & 卡片自测复习模式 ──────────────────────────────────────────
    private readonly List<(Word Word, Occurrence? Occurrence)> _reviewItems = new();
    [ObservableProperty] private bool _isDefinitionMasked;
    [ObservableProperty] private bool _isReviewMode;
    [ObservableProperty] private bool _isAnswerRevealed;
    [ObservableProperty] private int _reviewIndex;

    public int ReviewTotal => _reviewItems.Count;
    public Word? CurrentReviewWord => _reviewItems.Count > 0 && ReviewIndex >= 0 && ReviewIndex < _reviewItems.Count ? _reviewItems[ReviewIndex].Word : null;
    public Occurrence? CurrentReviewOccurrence => _reviewItems.Count > 0 && ReviewIndex >= 0 && ReviewIndex < _reviewItems.Count ? _reviewItems[ReviewIndex].Occurrence : null;
    public string ReviewProgressText => ReviewTotal > 0 ? $"{ReviewIndex + 1} / {ReviewTotal}" : "0 / 0";
    public string ReviewPromptSentence => !string.IsNullOrWhiteSpace(CurrentReviewOccurrence?.Sentence)
        ? CurrentReviewOccurrence!.Sentence
        : CurrentReviewWord?.DisplayWord ?? string.Empty;
    public string ReviewEffectiveExpression => !string.IsNullOrWhiteSpace(CurrentReviewOccurrence?.Expression)
        ? CurrentReviewOccurrence!.Expression
        : CurrentReviewWord?.DisplayWord ?? string.Empty;
    public string ReviewEffectiveDefinition => !string.IsNullOrWhiteSpace(CurrentReviewOccurrence?.Definition)
        ? CurrentReviewOccurrence!.Definition
        : CurrentReviewWord?.Definition ?? string.Empty;
    public string ReviewEffectiveTranslation => CurrentReviewOccurrence?.Translation ?? string.Empty;
    public string ReviewEffectiveNote => CurrentReviewOccurrence?.Note ?? string.Empty;
    public string ReviewEffectiveKeyExplanation => CurrentReviewOccurrence?.KeyExplanation ?? string.Empty;
    public string ReviewEffectiveUsageNote => CurrentReviewOccurrence?.UsageNote ?? string.Empty;
    public string ReviewEffectiveExample => CurrentReviewOccurrence?.Example ?? string.Empty;
    public string ReviewEffectiveExampleTranslation => CurrentReviewOccurrence?.ExampleTranslation ?? string.Empty;
    public string ReviewSourceSummary
    {
        get
        {
            if (CurrentReviewOccurrence == null) return string.Empty;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(CurrentReviewOccurrence.SourceProcess)) parts.Add(CurrentReviewOccurrence.SourceProcess);
            if (!string.IsNullOrWhiteSpace(CurrentReviewOccurrence.SourceWindowTitle)) parts.Add(CurrentReviewOccurrence.SourceWindowTitle);
            return string.Join(" · ", parts);
        }
    }

    private void NotifyReviewPropertiesChanged()
    {
        OnPropertyChanged(nameof(ReviewTotal));
        OnPropertyChanged(nameof(CurrentReviewWord));
        OnPropertyChanged(nameof(CurrentReviewOccurrence));
        OnPropertyChanged(nameof(ReviewProgressText));
        OnPropertyChanged(nameof(ReviewPromptSentence));
        OnPropertyChanged(nameof(ReviewEffectiveExpression));
        OnPropertyChanged(nameof(ReviewEffectiveDefinition));
        OnPropertyChanged(nameof(ReviewEffectiveTranslation));
        OnPropertyChanged(nameof(ReviewEffectiveNote));
        OnPropertyChanged(nameof(ReviewEffectiveKeyExplanation));
        OnPropertyChanged(nameof(ReviewEffectiveUsageNote));
        OnPropertyChanged(nameof(ReviewEffectiveExample));
        OnPropertyChanged(nameof(ReviewEffectiveExampleTranslation));
        OnPropertyChanged(nameof(ReviewSourceSummary));
    }

    partial void OnReviewIndexChanged(int value) => NotifyReviewPropertiesChanged();

    [RelayCommand]
    private async Task StartReviewAsync()
    {
        if (Words.Count == 0)
        {
            StatusMessage = "当前列表没有可复习的词条。";
            return;
        }

        _reviewItems.Clear();
        foreach (var word in Words)
        {
            IReadOnlyList<Occurrence> contexts = Array.Empty<Occurrence>();
            try
            {
                contexts = await _wordRepository.GetOccurrencesByWordIdAsync(word.Id).ConfigureAwait(true);
            }
            catch
            {
                // Ignore individual occurrence read errors and fall back to word summary
            }

            if (contexts.Count > 0)
            {
                foreach (var occ in contexts)
                {
                    _reviewItems.Add((word, occ));
                }
            }
            else
            {
                _reviewItems.Add((word, null));
            }
        }

        if (_reviewItems.Count == 0) return;

        var startIndex = SelectedWord != null
            ? Math.Max(0, _reviewItems.FindIndex(item => item.Word.Id == SelectedWord.Id))
            : 0;
        ReviewIndex = startIndex;
        IsAnswerRevealed = false;
        IsReviewMode = true;
        NotifyReviewPropertiesChanged();
        StatusMessage = $"已进入卡片复习（共 {ReviewTotal} 张），按 Space 显示答案，← / → 切换卡片。";
    }

    [RelayCommand]
    private void ExitReview()
    {
        if (!IsReviewMode) return;
        IsReviewMode = false;
        IsAnswerRevealed = false;
        StatusMessage = $"{Words.Count} 个词条 · 按最近更新排序";
    }

    [RelayCommand]
    private void ToggleAnswer()
    {
        if (!IsReviewMode) return;
        IsAnswerRevealed = !IsAnswerRevealed;
    }

    [RelayCommand]
    private void NextReviewCard()
    {
        if (!IsReviewMode || _reviewItems.Count == 0) return;
        IsAnswerRevealed = false;
        ReviewIndex = (ReviewIndex + 1) % _reviewItems.Count;
        SelectedWord = CurrentReviewWord;
    }

    [RelayCommand]
    private void PrevReviewCard()
    {
        if (!IsReviewMode || _reviewItems.Count == 0) return;
        IsAnswerRevealed = false;
        ReviewIndex = (ReviewIndex - 1 + _reviewItems.Count) % _reviewItems.Count;
        SelectedWord = CurrentReviewWord;
    }

    [RelayCommand]
    private void ShuffleReview()
    {
        if (!IsReviewMode || _reviewItems.Count <= 1) return;
        var rng = Random.Shared;
        for (var i = _reviewItems.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (_reviewItems[i], _reviewItems[j]) = (_reviewItems[j], _reviewItems[i]);
        }
        IsAnswerRevealed = false;
        ReviewIndex = 0;
        NotifyReviewPropertiesChanged();
        SelectedWord = CurrentReviewWord;
        StatusMessage = "已打乱复习卡片顺序。";
    }

    [RelayCommand]
    private void SpeakWord(string? text = null)
    {
        var target = !string.IsNullOrWhiteSpace(text)
            ? text
            : IsReviewMode
                ? (CurrentReviewWord?.DisplayWord ?? string.Empty)
                : (SelectedWord?.DisplayWord ?? string.Empty);
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            (_speechService ?? new SpeechService()).Speak(target);
        }
        catch (Exception ex)
        {
            StatusMessage = $"朗读未完成：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSourceUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            StatusMessage = "无法在默认浏览器中打开来源链接。";
        }
    }

    [RelayCommand]
    private void EditContext(Occurrence occurrence)
    {
        EditingOccurrence = occurrence;
        ContextDefinition = string.IsNullOrWhiteSpace(occurrence.Definition) ? SelectedWord?.Definition ?? "" : occurrence.Definition;
        ContextTranslation = occurrence.Translation;
        ContextNote = occurrence.Note;
    }

    [RelayCommand]
    private void CancelContextEdit() { if (!IsSavingContext) EditingOccurrence = null; }

    [RelayCommand]
    private Task TranslateOccurrenceSentenceAsync(Occurrence? target = null)
        => TranslateOccurrenceSentenceCoreAsync(target, useAi: false);

    [RelayCommand]
    private Task UseAiOccurrenceSentenceAsync(Occurrence? target = null)
        => TranslateOccurrenceSentenceCoreAsync(target, useAi: true);

    [RelayCommand]
    private Task RetryContextSentenceAsync(Occurrence? target = null)
    {
        var retryTarget = target ?? (_lastContextSentenceFromEditor ? null : _lastContextSentenceOccurrence);
        var retryWithAi = ContextSentenceTranslationFailed && LastContextSentenceUsedAi;
        return TranslateOccurrenceSentenceCoreAsync(retryTarget, retryWithAi);
    }

    [RelayCommand]
    private Task UseAiFailedContextSentenceAsync()
        => TranslateOccurrenceSentenceCoreAsync(
            _lastContextSentenceFromEditor ? null : _lastContextSentenceOccurrence,
            useAi: true);

    private async Task TranslateOccurrenceSentenceCoreAsync(Occurrence? target, bool useAi)
    {
        if (IsTranslatingContextSentence) return;
        if (_lookupService == null)
        {
            StatusMessage = "翻译服务未初始化。";
            return;
        }

        var occ = target ?? EditingOccurrence;
        if (occ == null || string.IsNullOrWhiteSpace(occ.Sentence))
        {
            StatusMessage = "当前语境没有可翻译的原句。";
            return;
        }

        IsTranslatingContextSentence = true;
        ContextSentenceTranslationFailed = false;
        LastContextSentenceUsedAi = useAi;
        _lastContextSentenceOccurrence = occ;
        _lastContextSentenceFromEditor = target == null;
        var editingAtStart = EditingOccurrence;
        var translationAtStart = ContextTranslation;
        StatusMessage = useAi ? "正在使用 AI 翻译原句…" : "正在进行在线快译…";
        try
        {
            var translated = useAi
                ? await _lookupService.TranslateSentenceWithAiAsync(occ.Sentence, occ.SelectedText).ConfigureAwait(true)
                : await _lookupService.TranslateSentenceAsync(occ.Sentence, occ.SelectedText).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException(useAi ? "AI 翻译未返回结果。" : "在线快译未返回结果。");
            if (target == null && (!ReferenceEquals(EditingOccurrence, editingAtStart)
                || ContextTranslation != translationAtStart)) return;

            if (ReferenceEquals(EditingOccurrence, editingAtStart) && EditingOccurrence?.Id == occ.Id
                && ContextTranslation == translationAtStart)
            {
                ContextTranslation = translated.Trim();
            }

            if (target != null)
            {
                var def = !string.IsNullOrWhiteSpace(occ.Definition)
                    ? occ.Definition
                    : SelectedWord?.Definition ?? occ.SelectedText;
                var edits = new LearningEdits(def, translated.Trim(), occ.Note);
                await _wordRepository.UpdateOccurrenceEditsAsync(occ.Id, edits).ConfigureAwait(true);
                if (SelectedWord?.Id == occ.WordId)
                    await LoadOccurrencesForWordAsync(occ.WordId).ConfigureAwait(true);
                _webDavSyncService?.ScheduleAutoSync();
                StatusMessage = "原句翻译已生成并保存。";
            }
            else
            {
                StatusMessage = "原句翻译已填入，点击「保存语境」即可生效。";
            }
        }
        catch (Exception ex)
        {
            ContextSentenceTranslationFailed = true;
            StatusMessage = $"{(useAi ? "AI 翻译" : "在线快译")}失败：{ex.Message}";
        }
        finally
        {
            IsTranslatingContextSentence = false;
        }
    }

    [RelayCommand]
    private async Task SaveContextEditAsync()
    {
        if (EditingOccurrence == null || IsSavingContext) return;
        if (string.IsNullOrWhiteSpace(ContextDefinition)) { StatusMessage = "语境释义不能为空，请填写后保存。"; return; }
        IsSavingContext = true;
        var occurrence = EditingOccurrence;
        try
        {
            var edits = new LearningEdits(ContextDefinition.Trim(), ContextTranslation.Trim(), ContextNote.Trim());
            await _wordRepository.UpdateOccurrenceEditsAsync(occurrence.Id, edits);
            if (EditingOccurrence != occurrence) return;
            EditingOccurrence = null;
            if (SelectedWord?.Id == occurrence.WordId) await LoadOccurrencesForWordAsync(occurrence.WordId);
            StatusMessage = "语境修改已保存。";
            _webDavSyncService?.ScheduleAutoSync();
        }
        catch (Exception ex) { StatusMessage = "保存语境失败：" + ex.Message; }
        finally { IsSavingContext = false; }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteWordCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoDeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isChangingCollection;
    partial void OnIsChangingCollectionChanged(bool value) { if (!value) RefreshAfterEditing(); }
    private bool CanChangeCollection => !IsChangingCollection && !IsEditing && !IsEditingContext && !IsSavingContext;

    private readonly Stack<List<(string Id, string Name)>> _deletedWords = new();
    [ObservableProperty] private bool _canUndoDelete;
    [ObservableProperty] private string _deleteStatusText = string.Empty;
    [ObservableProperty] private int _selectedFilterIndex;
    partial void OnSelectedFilterIndexChanged(int value) { RefreshFilterLabels(); _ = SearchAsync(); }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    private bool _isLoading;
    [ObservableProperty] private bool _hasLoadError;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelEditCommand))]
    private bool _isSavingEdit;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _isSyncingWebDav;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _isDefinitionExpanded;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _emptyMessage = "还没有收藏的词条";
    public string DefinitionPreview => string.Join(" ", (SelectedWord?.Definition ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    partial void OnSearchQueryChanged(string value)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        _searchDelay = new CancellationTokenSource();
        ++_loadVersion;
        IsLoading = true;
        _ = SearchAfterDelayAsync(_searchDelay.Token);
    }

    private async Task SearchAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            await LoadWordsAsync();
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    [RelayCommand]
    private Task LoadMoreAsync() => LoadWordsAsync(append: true);

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private ObservableCollection<Word> _words = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteWordCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyWordCommand))]
    private Word? _selectedWord;

    [ObservableProperty]
    private ObservableCollection<Occurrence> _occurrences = new();

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editWordText = string.Empty;

    [ObservableProperty]
    private string _editReading = string.Empty;

    [ObservableProperty]
    private string _editPartOfSpeech = string.Empty;

    [ObservableProperty]
    private string _editDefinition = string.Empty;

    [ObservableProperty]
    private string _editMemoryHook = string.Empty;

    public LibraryViewModel(
        IWordRepository wordRepository,
        WordCollectionEvents wordCollectionEvents,
        ISpeechService? speechService = null,
        IWebDavSyncService? webDavSyncService = null,
        ILookupService? lookupService = null,
        string? preferencesPath = null)
    {
        _wordRepository = wordRepository;
        _speechService = speechService;
        _webDavSyncService = webDavSyncService;
        _lookupService = lookupService;
        _preferencesPath = preferencesPath;
        var preferences = LibraryViewPreferences.Load(preferencesPath);
        _selectedFilterIndex = Math.Clamp(preferences.Filter, 0, 5);
        _selectedSortIndex = Math.Clamp(preferences.Sort, 0, 2);
        _sourceFilter = preferences.Source ?? "";
        wordCollectionEvents.CollectionChanged += OnWordSaved;
    }

    private void OnWordSaved()
    {
        if (IsEditing || IsEditingContext || IsSavingContext || IsChangingCollection)
        {
            _refreshPending = true;
            return;
        }

        _ = LoadWordsAsync();
    }

    partial void OnIsEditingChanged(bool value)
    {
        StartEditCommand.NotifyCanExecuteChanged();
        DeleteWordCommand.NotifyCanExecuteChanged();
        UndoDeleteCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RefreshAfterEditing();
    }

    public async Task InitializeAsync()
    {
        await LoadWordsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        _searchDelay?.Cancel();
        await LoadWordsAsync().ConfigureAwait(true);
    }

    private async Task LoadWordsAsync(bool append = false)
    {
        if (IsEditing || IsEditingContext || IsSavingContext)
        {
            _refreshPending = true;
            IsLoading = false;
            return;
        }
        var version = ++_loadVersion;
        IsLoading = true;
        HasLoadError = false;
        StatusMessage = "正在加载词条…";
        try
        {
            var list = SelectedFilterIndex == 0 && SelectedSortIndex == 0 && string.IsNullOrWhiteSpace(SourceFilter)
                ? await _wordRepository.GetWordsAsync(SearchQuery, limit: PageSize + 1, offset: append ? Words.Count : 0).ConfigureAwait(true)
                : await _wordRepository.GetFilteredWordsAsync(SearchQuery, (WordFilter)SelectedFilterIndex,
                    limit: PageSize + 1, offset: append ? Words.Count : 0,
                    sourceProcess: SourceFilter, sort: SelectedSortIndex).ConfigureAwait(true);
            if (version != _loadVersion) return;
            var selectedId = SelectedWord?.Id;
            if (append)
            {
                foreach (var word in list.Take(PageSize)) Words.Add(word);
            }
            else
            {
                Words = new ObservableCollection<Word>(list.Take(PageSize));
                SelectedWord = Words.FirstOrDefault(w => w.Id == selectedId) ?? Words.FirstOrDefault();
            }
            HasMore = list.Count > PageSize;
            MatchingWordCount = list.FirstOrDefault()?.MatchingWordCount ?? Words.Count;
            OnPropertyChanged(nameof(ResultCountText));
            EmptyMessage = string.IsNullOrWhiteSpace(SearchQuery) && SelectedFilterIndex == 0 && string.IsNullOrWhiteSpace(SourceFilter) ? "还没有收藏的词条" : "没有匹配的词条";
            StatusMessage = string.Empty;
            try { new LibraryViewPreferences(SelectedFilterIndex, SelectedSortIndex, SourceFilter).Save(_preferencesPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusMessage += " · 视图偏好保存失败"; }
        }
        catch (Exception)
        {
            if (version == _loadVersion)
            {
                HasLoadError = true;
                EmptyMessage = "词库暂时无法加载";
                StatusMessage = "加载失败，请重试；本地词条不会因此丢失。";
            }
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    partial void OnSelectedWordChanged(Word? value)
    {
        EditingOccurrence = null;
        IsEditing = false;
        IsDefinitionExpanded = false;
        OnPropertyChanged(nameof(DefinitionPreview));
        ++_occurrenceVersion;
        Occurrences.Clear();
        if (value != null)
        {
            _ = LoadOccurrencesForWordAsync(value.Id);
        }
        else
        {
            Occurrences.Clear();
        }
    }

    public string WordDefinitionLabel => Occurrences.FirstOrDefault(o =>
        string.Equals(o.Definition.Trim(), SelectedWord?.Definition.Trim(), StringComparison.Ordinal))?.DefinitionLabel ?? "释义";
    public string SavedSentencesTitle => Occurrences.Count > 1 ? $"收藏原句 · {Occurrences.Count}" : "收藏原句";

    private async Task LoadOccurrencesForWordAsync(string wordId)
    {
        var version = _occurrenceVersion;
        try
        {
            var list = await _wordRepository.GetOccurrencesByWordIdAsync(wordId).ConfigureAwait(true);
            if (version != _occurrenceVersion || SelectedWord?.Id != wordId) return;
            foreach (var occurrence in list) occurrence.WordDefinition = SelectedWord.Definition;
            Occurrences = new ObservableCollection<Occurrence>(list);
            OnPropertyChanged(nameof(WordDefinitionLabel));
            OnPropertyChanged(nameof(SavedSentencesTitle));
        }
        catch (Exception)
        {
            if (version == _occurrenceVersion) StatusMessage = "语境加载失败，请重新选择词条。";
        }
    }

    private bool CanStartEdit() => SelectedWord != null && !IsChangingCollection && !IsEditing;

    [RelayCommand(CanExecute = nameof(CanStartEdit))]
    private void StartEdit()
    {
        if (!CanStartEdit() || SelectedWord == null) return;
        _searchDelay?.Cancel();
        ++_loadVersion;
        IsLoading = false;
        EditWordText = SelectedWord.DisplayWord;
        EditReading = SelectedWord.Reading;
        EditPartOfSpeech = SelectedWord.PartOfSpeech;
        EditDefinition = SelectedWord.Definition;
        EditMemoryHook = SelectedWord.MemoryHook;
        IsEditing = true;
        StatusMessage = "正在编辑词条，保存或取消后可继续搜索和切换。";
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (SelectedWord == null) return;

        if (string.IsNullOrWhiteSpace(EditWordText) || string.IsNullOrWhiteSpace(EditDefinition))
        {
            StatusMessage = "单词和释义不能为空，请补充后保存。";
            return;
        }
        var word = new Word
        {
            Id = SelectedWord.Id, DisplayWord = EditWordText.Trim(), Reading = EditReading.Trim(),
            PartOfSpeech = EditPartOfSpeech.Trim(), Definition = EditDefinition.Trim(),
            MemoryHook = EditMemoryHook.Trim()
        };
        IsSavingEdit = true;
        try
        {
            await _wordRepository.UpdateWordAsync(word).ConfigureAwait(true);
            IsEditing = false;
            await LoadWordsAsync().ConfigureAwait(true);
            if (!HasLoadError) StatusMessage = "词条修改已保存。";
            _webDavSyncService?.ScheduleAutoSync();
        }
        catch (Exception) { StatusMessage = "保存失败，修改内容已保留，请重试。"; }
        finally { IsSavingEdit = false; }
    }

    private bool CanCancelEdit() => !IsSavingEdit;

    [RelayCommand(CanExecute = nameof(CanCancelEdit))]
    private void CancelEdit()
    {
        if (!CanCancelEdit()) return;
        IsEditing = false;
        StatusMessage = "已取消编辑。";
    }

    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private async Task DeleteWordAsync()
    {
        if (SelectedWord == null || !CanChangeCollection) return;

        var word = SelectedWord;
        IsChangingCollection = true;
        try
        {
            await _wordRepository.MoveWordToTrashAsync(word.Id).ConfigureAwait(true);
            _deletedWords.Push(new() { (word.Id, word.DisplayWord) });
            CanUndoDelete = true;
            DeleteStatusText = $"已删除「{word.DisplayWord}」及其语境记录，可撤销。";
            if (SelectedWord?.Id == word.Id) SelectedWord = null;
            await LoadWordsAsync().ConfigureAwait(true);
            _webDavSyncService?.ScheduleAutoSync();
        }
        catch (Exception) { StatusMessage = "删除失败，请重试。"; }
        finally { IsChangingCollection = false; }
    }

    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private async Task UndoDeleteAsync()
    {
        if (!CanChangeCollection || !_deletedWords.TryPeek(out var deleted)) return;
        IsChangingCollection = true;
        var restored = 0;
        var firstId = deleted[0].Id;
        try
        {
            while (deleted.Count > 0)
            {
                await _wordRepository.RestoreWordAsync(deleted[^1].Id).ConfigureAwait(true);
                deleted.RemoveAt(deleted.Count - 1);
                restored++;
            }
            _deletedWords.Pop();
            CanUndoDelete = _deletedWords.Count > 0;
            DeleteStatusText = $"已恢复 {restored} 个词条及全部语境记录。";
        }
        catch (Exception) { DeleteStatusText = $"恢复失败：已恢复 {restored} 个，剩余 {deleted.Count} 个可再次撤销。"; }
        finally
        {
            if (restored > 0)
            {
                await LoadWordsAsync().ConfigureAwait(true);
                SelectedWord = Words.FirstOrDefault(word => word.Id == firstId) ?? SelectedWord;
                _webDavSyncService?.ScheduleAutoSync();
            }
            IsChangingCollection = false;
        }
    }

    private bool CanDeleteSelected() => CanChangeCollection && !IsLoading && SelectedWords.Count > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync()
    {
        if (!CanDeleteSelected()) return;
        var selected = SelectedWords.DistinctBy(w => w.Id).ToArray();
        var deleted = new List<(string Id, string Name)>();
        IsChangingCollection = true;
        try
        {
            foreach (var word in selected)
            {
                await _wordRepository.MoveWordToTrashAsync(word.Id).ConfigureAwait(true);
                deleted.Add((word.Id, word.DisplayWord));
            }
            DeleteStatusText = $"已删除 {deleted.Count} 个词条及其语境，可整批撤销。";
        }
        catch (Exception) { DeleteStatusText = $"删除中断：已删除 {deleted.Count} / {selected.Length} 个，已删除部分可撤销。"; }
        finally
        {
            if (deleted.Count > 0)
            {
                _deletedWords.Push(deleted);
                CanUndoDelete = true;
                SetSelectedWords(Array.Empty<Word>());
                await LoadWordsAsync().ConfigureAwait(true);
                _webDavSyncService?.ScheduleAutoSync();
            }
            IsChangingCollection = false;
        }
    }

    private bool CanCopyWord() => SelectedWord != null;

    [RelayCommand(CanExecute = nameof(CanCopyWord))]
    private void CopyWord()
    {
        if (SelectedWord is not { } word) return;
        try
        {
            Clipboard.SetText($"{word.DisplayWord}\n{word.Definition}");
            StatusMessage = "已复制单词与释义。";
        }
        catch (Exception) { StatusMessage = "复制失败，请稍后重试。"; }
    }

    [RelayCommand]
    private async Task SyncWebDavAsync()
    {
        if (IsSyncingWebDav || IsExporting) return;
        if (_webDavSyncService == null)
        {
            StatusMessage = "请先在「设置」页面配置并启用 WebDAV 同步。";
            return;
        }

        IsSyncingWebDav = true;
        StatusMessage = "正在与 WebDAV 云端同步词库…";
        try
        {
            var res = await _webDavSyncService.SyncAsync(forcePush: true).ConfigureAwait(true);
            await LoadWordsAsync().ConfigureAwait(true);
            var parts = new List<string>();
            if (res.Added > 0) parts.Add($"新增 {res.Added} 条");
            if (res.Updated > 0) parts.Add($"更新 {res.Updated} 条");
            if (res.Removed > 0) parts.Add($"删除 {res.Removed} 条");
            var summary = parts.Count > 0 ? string.Join("，", parts) : "数据已是最新";
            StatusMessage = $"WebDAV 同步完成：{summary}（共 {res.Total} 个词条）。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"WebDAV 同步失败：{ex.Message}";
        }
        finally
        {
            IsSyncingWebDav = false;
        }
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        var sfd = new SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"wordcatcher_backup_{DateTime.Now:yyyyMMdd}.json",
            Title = "导出词库备份"
        };

        if (sfd.ShowDialog() == true)
        {
            IsExporting = true;
            StatusMessage = "正在导出全部词条与语境…";
            try
            {
                var json = await _wordRepository.ExportWordsJsonAsync().ConfigureAwait(true);
                await File.WriteAllTextAsync(sfd.FileName, json).ConfigureAwait(true);
                StatusMessage = "词库备份已导出。";
            }
            catch (Exception) { StatusMessage = "导出失败，请检查目标文件是否可写后重试。"; }
            finally { IsExporting = false; }
        }
    }

    [RelayCommand]
    private async Task ImportJsonAsync()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "JSON 备份文件 (*.json)|*.json",
            Title = "导入词库或浏览器插件备份"
        };

        if (ofd.ShowDialog() == true)
        {
            IsExporting = true;
            StatusMessage = "正在导入词库备份…";
            try
            {
                var json = await File.ReadAllTextAsync(ofd.FileName).ConfigureAwait(true);
                var count = await _wordRepository.ImportWordsJsonAsync(json).ConfigureAwait(true);
                await LoadWordsAsync().ConfigureAwait(true);
                if (count > 0) _webDavSyncService?.ScheduleAutoSync();
                StatusMessage = count > 0
                    ? $"已导入 {count} 条词条或语境记录。"
                    : "备份文件已解析，未发现新的词条或语境。";
            }
            catch (Exception)
            {
                StatusMessage = "导入失败，请确认所选文件是有效的 JSON 备份。";
            }
            finally
            {
                IsExporting = false;
            }
        }
    }
}

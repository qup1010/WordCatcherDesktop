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
    private readonly SpeechService _speechService;

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
    private string _detailsToggleText = "展开全部义项";

    [ObservableProperty]
    private bool _isSaved;

    [ObservableProperty]
    private bool _canSave;

    [ObservableProperty]
    private string _savedStatusText = string.Empty;

    public event Action? RequestClose;

    public bool KeepOpenAfterSave { get; set; }

    private CaptureResult? _currentCapture;
    private TranslationResult? _currentTranslation;
    private CancellationTokenSource? _lookupCancellation;

    public LookupViewModel(
        ILookupService lookupService,
        IWordRepository wordRepository,
        IAnkiSyncQueue syncQueue,
        ISettingsService settingsService,
        SpeechService speechService)
    {
        _lookupService = lookupService;
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
        _settingsService = settingsService;
        _speechService = speechService;
    }

    public async Task StartLookupAsync(CaptureResult capture, bool forceAi = false)
    {
        var previousLookup = _lookupCancellation;
        var currentLookup = new CancellationTokenSource();
        _lookupCancellation = currentLookup;
        previousLookup?.Cancel();

        _currentCapture = capture;
        SelectedText = capture.SelectedText;
        Word = capture.SelectedText;
        Reading = string.Empty;
        PartOfSpeech = string.Empty;
        PosGroups = null;
        DisplayPosGroups = null;
        CanExpandDetails = false;
        IsLoading = true;
        HasResult = false;
        HasError = false;
        IsSaved = false;
        CanSave = false;
        SavedStatusText = string.Empty;
        StatusText = forceAi ? "正在调用 AI 语境详解..." : "正在查询词典与翻译...";

        try
        {
            var result = await _lookupService.LookupAsync(capture, forceAi, currentLookup.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_lookupCancellation, currentLookup))
                return;

            _currentTranslation = result;

            Word = result.Word;
            Reading = result.Reading;
            PartOfSpeech = result.PartOfSpeech;
            Definition = result.Definition;
            ContextTranslation = result.ContextTranslation;
            MemoryHook = result.MemoryHook;
            Source = result.Source;
            PosGroups = result.PosGroups;
            UpdateStructuredDetails(expanded: false);

            HasResult = true;
            CanSave = true;
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
        var lookup = _lookupCancellation;
        _lookupCancellation = null;
        lookup?.Cancel();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave || _currentCapture == null || _currentTranslation == null)
            return;

        CanSave = false;
        SavedStatusText = "正在保存到本地词库...";

        try
        {
            var cmd = new SaveCardCommand(_currentCapture, _currentTranslation);
            var (savedWord, occ, syncJob) = await _wordRepository.SaveAsync(cmd).ConfigureAwait(true);

            IsSaved = true;
            SavedStatusText = "✓ 已保存本地 (等待同步 Anki)";
            _syncQueue.Enqueue(syncJob.Id);

            if (_settingsService.Current.Ui.ClosePopupAfterSave)
            {
                await Task.Delay(800).ConfigureAwait(true);
                if (!KeepOpenAfterSave)
                    RequestClose?.Invoke();
            }
        }
        catch (Exception ex)
        {
            CanSave = true;
            SavedStatusText = $"保存失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ForceAiAsync()
    {
        if (_currentCapture != null)
        {
            await StartLookupAsync(_currentCapture, forceAi: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_currentCapture != null)
        {
            await StartLookupAsync(_currentCapture, forceAi: false).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ToggleDetails()
    {
        var expanded = DetailsToggleText.StartsWith("展开", StringComparison.Ordinal);
        UpdateStructuredDetails(expanded);
    }

    private void UpdateStructuredDetails(bool expanded)
    {
        var groups = PosGroups?.Where(group => group.Meanings.Count > 0).ToList();
        if (groups == null || groups.Count == 0)
        {
            DisplayPosGroups = groups;
            CanExpandDetails = false;
            return;
        }

        var totalMeanings = groups.Sum(group => group.Meanings.Count);
        CanExpandDetails = totalMeanings > 3;
        if (expanded || !CanExpandDetails)
        {
            DisplayPosGroups = groups;
            DetailsToggleText = "收起次要义项";
            return;
        }

        var remaining = 3;
        var preview = new List<DictPosGroup>();
        foreach (var group in groups)
        {
            if (remaining == 0)
                break;

            var meanings = group.Meanings.Take(remaining).ToList();
            if (meanings.Count > 0)
            {
                preview.Add(new DictPosGroup(group.Pos, group.Summary, meanings));
                remaining -= meanings.Count;
            }
        }

        DisplayPosGroups = preview;
        DetailsToggleText = $"展开全部 {totalMeanings} 个义项";
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Speak()
    {
        try
        {
            var text = !string.IsNullOrWhiteSpace(_currentCapture?.Sentence)
                ? _currentCapture.Sentence
                : SelectedText;
            _speechService.Speak(text);
            SavedStatusText = "正在朗读...";
        }
        catch (Exception ex)
        {
            SavedStatusText = $"朗读不可用: {ex.Message}";
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
            SavedStatusText = "✓ 已复制";
        }
        catch (Exception ex)
        {
            SavedStatusText = $"复制失败: {ex.Message}";
        }
    }
}

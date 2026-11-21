using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.ViewModels;

/// <summary>
/// Adapts the popup lookup flow to a persistent, manually driven page.
/// </summary>
public sealed partial class WordLookupViewModel : ObservableObject
{
    private readonly LookupViewModel _lookup;
    private readonly IOfflineDictionaryService? _offlineDictionary;
    private readonly int _suggestDebounceMs;
    private CancellationTokenSource? _suggestCts;
    private bool _suppressSuggestions;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private string _queryError = string.Empty;

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    [ObservableProperty]
    private bool _isSuggestionsOpen;

    public ObservableCollection<string> Suggestions { get; } = new();

    public WordLookupViewModel(LookupViewModel lookup, IOfflineDictionaryService? offlineDictionary = null)
        : this(lookup, offlineDictionary, suggestDebounceMs: 120)
    {
    }

    internal WordLookupViewModel(LookupViewModel lookup, IOfflineDictionaryService? offlineDictionary, int suggestDebounceMs)
    {
        _lookup = lookup;
        _offlineDictionary = offlineDictionary;
        _suggestDebounceMs = Math.Max(0, suggestDebounceMs);
        _lookup.KeepOpenAfterSave = true;
    }

    public LookupViewModel Lookup => _lookup;
    [RelayCommand] private Task LoadHistoryAsync() => _lookup.RefreshHistoryAsync();

    partial void OnQueryChanged(string value)
    {
        CancelPendingSuggest();
        if (_suppressSuggestions || _offlineDictionary == null || string.IsNullOrWhiteSpace(value))
        {
            DismissSuggestions();
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 40 || trimmed.IndexOfAny(['\r', '\n']) >= 0)
        {
            DismissSuggestions();
            return;
        }

        var cts = new CancellationTokenSource();
        _suggestCts = cts;
        _ = LoadSuggestionsAsync(trimmed, cts);
    }

    private async Task LoadSuggestionsAsync(string prefix, CancellationTokenSource cts)
    {
        try
        {
            if (_suggestDebounceMs > 0)
                await Task.Delay(_suggestDebounceMs, cts.Token).ConfigureAwait(true);

            var items = await _offlineDictionary!.SuggestAsync(prefix, 6, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(_suggestCts, cts))
                return;

            var current = (Query ?? string.Empty).Trim();
            if (!string.Equals(current, prefix, StringComparison.OrdinalIgnoreCase))
                return;

            if (items.Count == 0 || (items.Count == 1 && string.Equals(items[0], current, StringComparison.OrdinalIgnoreCase)))
            {
                DismissSuggestions();
                return;
            }

            Suggestions.Clear();
            foreach (var item in items)
                Suggestions.Add(item);

            SelectedSuggestionIndex = -1;
            IsSuggestionsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (ReferenceEquals(_suggestCts, cts))
                DismissSuggestions();
        }
    }

    public void MoveSuggestionSelection(int delta)
    {
        if (!IsSuggestionsOpen || Suggestions.Count == 0)
            return;

        if (SelectedSuggestionIndex < 0)
            SelectedSuggestionIndex = delta > 0 ? 0 : Suggestions.Count - 1;
        else
            SelectedSuggestionIndex = (SelectedSuggestionIndex + delta + Suggestions.Count) % Suggestions.Count;
    }

    public void DismissSuggestions()
    {
        CancelPendingSuggest();
        IsSuggestionsOpen = false;
        SelectedSuggestionIndex = -1;
        if (Suggestions.Count > 0)
            Suggestions.Clear();
    }

    private void CancelPendingSuggest()
    {
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;
    }

    private void SetQueryWithoutSuggestions(string value)
    {
        _suppressSuggestions = true;
        try
        {
            Query = value;
        }
        finally
        {
            _suppressSuggestions = false;
        }
    }

    [RelayCommand]
    private async Task SelectSuggestionAsync(string? word)
    {
        var selected = (word ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(selected))
            return;

        DismissSuggestions();
        SetQueryWithoutSuggestions(selected);
        await SearchForQueryAsync(selected).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task OpenHistoryAsync(QueryHistoryItem item)
    {
        DismissSuggestions();
        SetQueryWithoutSuggestions(item.Capture.SelectedText);
        HasSearched = true;
        QueryError = "";
        await _lookup.StartLookupAsync(item.Capture, historyResult: item.Result);
    }

    public async Task OpenHandoffAsync(MainWindowLookupHandoff handoff)
    {
        DismissSuggestions();
        SetQueryWithoutSuggestions(string.IsNullOrWhiteSpace(handoff.Result?.Word) ? handoff.Capture.SelectedText : handoff.Result.Word);
        QueryError = string.Empty;
        HasSearched = true;
        await _lookup.RestoreHandoffAsync(handoff).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task SearchAsync() => SearchForQueryAsync(Query);

    public async Task SearchForQueryAsync(string rawQuery)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            DismissSuggestions();
            QueryError = "请输入要查询的单词或短语";
            HasSearched = false;
            return;
        }

        DismissSuggestions();
        SetQueryWithoutSuggestions(query);
        QueryError = string.Empty;
        HasSearched = true;

        var capture = new CaptureResult(
            query,
            "WordCatcher",
            "查词页面",
            new ScreenPoint(0, 0),
            DateTimeOffset.Now);

        await _lookup.StartLookupAsync(capture).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearQuery()
    {
        DismissSuggestions();
        _lookup.CancelLookup();
        _lookup.IsLoading = false;
        _lookup.HasResult = false;
        _lookup.HasError = false;
        _lookup.CanSave = false;
        _lookup.SavedStatusText = string.Empty;
        _lookup.ActionStatusText = string.Empty;
        _lookup.SyncStatusText = string.Empty;
        SetQueryWithoutSuggestions(string.Empty);
        QueryError = string.Empty;
        HasSearched = false;
    }
}

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WordCatcher.Core.Models;

namespace WordCatcher.App.ViewModels;

/// <summary>
/// Adapts the popup lookup flow to a persistent, manually driven page.
/// </summary>
public sealed partial class WordLookupViewModel : ObservableObject
{
    private readonly LookupViewModel _lookup;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private bool _hasSearched;

    [ObservableProperty]
    private string _queryError = string.Empty;

    public WordLookupViewModel(LookupViewModel lookup)
    {
        _lookup = lookup;
        _lookup.KeepOpenAfterSave = true;
    }

    public LookupViewModel Lookup => _lookup;

    [RelayCommand]
    private async Task SearchAsync()
    {
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            QueryError = "请输入要查询的单词或短语";
            HasSearched = false;
            return;
        }

        Query = query;
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
        _lookup.CancelLookup();
        _lookup.IsLoading = false;
        _lookup.HasResult = false;
        _lookup.HasError = false;
        _lookup.CanSave = false;
        _lookup.SavedStatusText = string.Empty;
        Query = string.Empty;
        QueryError = string.Empty;
        HasSearched = false;
    }
}

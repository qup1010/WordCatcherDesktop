using System;
using System.Collections.ObjectModel;
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
    private const int PageSize = 100;
    private int _loadVersion;
    private int _occurrenceVersion;
    private CancellationTokenSource? _searchDelay;
    private bool _refreshPending;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isCompact = true;
    [ObservableProperty] private bool _isDefinitionExpanded;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _emptyMessage = "还没有收藏的单词";
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

    public LibraryViewModel(IWordRepository wordRepository, WordCollectionEvents wordCollectionEvents)
    {
        _wordRepository = wordRepository;
        wordCollectionEvents.WordSaved += OnWordSaved;
    }

    private void OnWordSaved()
    {
        if (IsEditing)
        {
            _refreshPending = true;
            return;
        }

        _ = LoadWordsAsync();
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (!value && _refreshPending)
        {
            _refreshPending = false;
            _ = LoadWordsAsync();
        }
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
        if (IsEditing)
        {
            IsLoading = false;
            return;
        }
        var version = ++_loadVersion;
        IsLoading = true;
        StatusMessage = "正在加载词条…";
        try
        {
            var list = await _wordRepository.GetWordsAsync(SearchQuery, limit: PageSize + 1,
                offset: append ? Words.Count : 0).ConfigureAwait(true);
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
            EmptyMessage = string.IsNullOrWhiteSpace(SearchQuery) ? "还没有收藏的单词" : "没有找到匹配的词条";
            StatusMessage = HasMore ? $"已显示 {Words.Count} 个词条 · 可继续加载" : $"{Words.Count} 个词条 · 最近更新优先";
        }
        catch (Exception)
        {
            if (version == _loadVersion) StatusMessage = "词库加载失败，请点击搜索重试。";
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    partial void OnSelectedWordChanged(Word? value)
    {
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

    private async Task LoadOccurrencesForWordAsync(string wordId)
    {
        var version = _occurrenceVersion;
        try
        {
            var list = await _wordRepository.GetOccurrencesByWordIdAsync(wordId).ConfigureAwait(true);
            if (version != _occurrenceVersion || SelectedWord?.Id != wordId) return;
            Occurrences = new ObservableCollection<Occurrence>(list);
        }
        catch (Exception)
        {
            if (version == _occurrenceVersion) StatusMessage = "语境加载失败，请重新选择词条。";
        }
    }

    [RelayCommand]
    private void StartEdit()
    {
        if (SelectedWord == null) return;
        _searchDelay?.Cancel();
        ++_loadVersion;
        IsLoading = false;
        EditWordText = SelectedWord.DisplayWord;
        EditReading = SelectedWord.Reading;
        EditPartOfSpeech = SelectedWord.PartOfSpeech;
        EditDefinition = SelectedWord.Definition;
        EditMemoryHook = SelectedWord.MemoryHook;
        IsEditing = true;
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
        try
        {
            await _wordRepository.UpdateWordAsync(word).ConfigureAwait(true);
            IsEditing = false;
            await LoadWordsAsync().ConfigureAwait(true);
        }
        catch (Exception) { StatusMessage = "保存失败，修改内容已保留，请重试。"; }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
    }

    [RelayCommand]
    private async Task DeleteWordAsync()
    {
        if (SelectedWord == null) return;

        var confirm = MessageBox.Show(
            $"确定要从词库中删除单词「{SelectedWord.DisplayWord}」及其所有语境记录吗？此操作不可撤销。",
            "删除确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            try
            {
                await _wordRepository.DeleteWordAsync(SelectedWord.Id).ConfigureAwait(true);
                SelectedWord = null;
                await LoadWordsAsync().ConfigureAwait(true);
            }
            catch (Exception) { StatusMessage = "删除失败，请重试。"; }
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
            try
            {
                var json = await _wordRepository.ExportWordsJsonAsync().ConfigureAwait(true);
                await File.WriteAllTextAsync(sfd.FileName, json).ConfigureAwait(true);
                StatusMessage = "词库备份导出成功。";
            }
            catch (Exception) { StatusMessage = "导出失败，请检查目标文件是否可写后重试。"; }
        }
    }
}

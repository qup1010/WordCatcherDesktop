using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
    private const int PageSize = 100;
    private int _loadVersion;
    private int _occurrenceVersion;
    private CancellationTokenSource? _searchDelay;
    private bool _refreshPending;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteWordCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoDeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartEditCommand))]
    private bool _isChangingCollection;
    private bool CanChangeCollection => !IsChangingCollection && !IsEditing;

    private readonly Stack<(string Id, string Name)> _deletedWords = new();
    [ObservableProperty] private bool _canUndoDelete;
    [ObservableProperty] private string _deleteStatusText = string.Empty;
    [ObservableProperty] private int _selectedFilterIndex;
    partial void OnSelectedFilterIndexChanged(int value) { if (!IsEditing) _ = SearchAsync(); }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLoadError;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelEditCommand))]
    private bool _isSavingEdit;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _isCompact;
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
        StartEditCommand.NotifyCanExecuteChanged();
        DeleteWordCommand.NotifyCanExecuteChanged();
        UndoDeleteCommand.NotifyCanExecuteChanged();
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
        HasLoadError = false;
        StatusMessage = "正在加载词条…";
        try
        {
            var list = SelectedFilterIndex == 0
                ? await _wordRepository.GetWordsAsync(SearchQuery, limit: PageSize + 1, offset: append ? Words.Count : 0).ConfigureAwait(true)
                : await _wordRepository.GetFilteredWordsAsync(SearchQuery, (WordFilter)SelectedFilterIndex,
                    limit: PageSize + 1, offset: append ? Words.Count : 0).ConfigureAwait(true);
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
            EmptyMessage = string.IsNullOrWhiteSpace(SearchQuery) && SelectedFilterIndex == 0 ? "还没有收藏的词条" : "没有匹配的词条";
            StatusMessage = HasMore ? $"已显示 {Words.Count} 个词条 · 可继续加载" : $"{Words.Count} 个词条 · 最近更新优先";
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
        StatusMessage = "正在编辑，保存或取消后可继续搜索和切换词条。";
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
            if (!HasLoadError) StatusMessage = "修改已保存。";
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
            _deletedWords.Push((word.Id, word.DisplayWord));
            CanUndoDelete = true;
            DeleteStatusText = $"已删除「{word.DisplayWord}」及其语境，可撤销。";
            if (SelectedWord?.Id == word.Id) SelectedWord = null;
            await LoadWordsAsync().ConfigureAwait(true);
        }
        catch (Exception) { StatusMessage = "删除失败，请重试。"; }
        finally { IsChangingCollection = false; }
    }

    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private async Task UndoDeleteAsync()
    {
        if (!CanChangeCollection || !_deletedWords.TryPeek(out var deleted)) return;
        IsChangingCollection = true;
        try
        {
            await _wordRepository.RestoreWordAsync(deleted.Id).ConfigureAwait(true);
            _deletedWords.Pop();
            CanUndoDelete = _deletedWords.Count > 0;
            DeleteStatusText = $"已恢复「{deleted.Name}」及全部语境记录。";
            await LoadWordsAsync().ConfigureAwait(true);
            SelectedWord = Words.FirstOrDefault(word => word.Id == deleted.Id) ?? SelectedWord;
        }
        catch (Exception) { DeleteStatusText = "恢复失败，请重试；记录仍保留。"; }
        finally { IsChangingCollection = false; }
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
                StatusMessage = "词库备份导出成功。";
            }
            catch (Exception) { StatusMessage = "导出失败，请检查目标文件是否可写后重试。"; }
            finally { IsExporting = false; }
        }
    }
}

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly IWordRepository _wordRepository;

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

    public LibraryViewModel(IWordRepository wordRepository)
    {
        _wordRepository = wordRepository;
    }

    public async Task InitializeAsync()
    {
        await LoadWordsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        await LoadWordsAsync().ConfigureAwait(true);
    }

    private async Task LoadWordsAsync()
    {
        var list = await _wordRepository.GetWordsAsync(SearchQuery, limit: 500).ConfigureAwait(true);
        Words.Clear();
        foreach (var w in list)
        {
            Words.Add(w);
        }

        if (Words.Count > 0 && SelectedWord == null)
        {
            SelectedWord = Words[0];
        }
    }

    partial void OnSelectedWordChanged(Word? value)
    {
        IsEditing = false;
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
        var list = await _wordRepository.GetOccurrencesByWordIdAsync(wordId).ConfigureAwait(true);
        Occurrences.Clear();
        foreach (var o in list)
        {
            Occurrences.Add(o);
        }
    }

    [RelayCommand]
    private void StartEdit()
    {
        if (SelectedWord == null) return;
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

        SelectedWord.DisplayWord = EditWordText.Trim();
        SelectedWord.Reading = EditReading.Trim();
        SelectedWord.PartOfSpeech = EditPartOfSpeech.Trim();
        SelectedWord.Definition = EditDefinition.Trim();
        SelectedWord.MemoryHook = EditMemoryHook.Trim();

        await _wordRepository.UpdateWordAsync(SelectedWord).ConfigureAwait(true);
        IsEditing = false;
        await LoadWordsAsync().ConfigureAwait(true);
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
            await _wordRepository.DeleteWordAsync(SelectedWord.Id).ConfigureAwait(true);
            SelectedWord = null;
            await LoadWordsAsync().ConfigureAwait(true);
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
            var json = await _wordRepository.ExportWordsJsonAsync().ConfigureAwait(true);
            await File.WriteAllTextAsync(sfd.FileName, json).ConfigureAwait(true);
            MessageBox.Show("词库备份导出成功！", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

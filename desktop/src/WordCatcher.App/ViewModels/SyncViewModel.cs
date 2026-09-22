using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.ViewModels;

public sealed partial class SyncViewModel : ObservableObject
{
    private readonly IWordRepository _wordRepository;
    private readonly IAnkiSyncQueue _syncQueue;

    [ObservableProperty]
    private ObservableCollection<SyncJob> _syncJobs = new();

    [ObservableProperty]
    private SyncJob? _selectedJob;

    public SyncViewModel(IWordRepository wordRepository, IAnkiSyncQueue syncQueue)
    {
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
    }

    public async Task InitializeAsync()
    {
        await LoadSyncJobsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        await LoadSyncJobsAsync().ConfigureAwait(true);
    }

    private async Task LoadSyncJobsAsync()
    {
        var list = await _wordRepository.GetSyncJobsAsync().ConfigureAwait(true);
        SyncJobs.Clear();
        foreach (var j in list)
        {
            SyncJobs.Add(j);
        }
    }

    [RelayCommand]
    private async Task RetrySelectedAsync()
    {
        if (SelectedJob == null) return;
        await _wordRepository.ResetSyncJobAsync(SelectedJob.Id).ConfigureAwait(true);
        _syncQueue.TriggerSync();
        await LoadSyncJobsAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryAllAsync()
    {
        await _wordRepository.ResetAllFailedSyncJobsAsync().ConfigureAwait(true);
        _syncQueue.TriggerSync();
        await LoadSyncJobsAsync().ConfigureAwait(true);
        MessageBox.Show("已将所有失败与待重试任务重新加入同步队列。", "已提交重试", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

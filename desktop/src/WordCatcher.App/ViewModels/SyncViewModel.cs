using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.ViewModels;

public sealed partial class SyncViewModel : ObservableObject
{
    private readonly IWordRepository _wordRepository;
    private readonly IAnkiSyncQueue _syncQueue;
    [ObservableProperty] private ObservableCollection<SyncJob> _syncJobs = new();
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetrySelectedCommand))]
    private SyncJob? _selectedJob;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetrySelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryAllCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "刷新查看最新同步状态";
    public string Summary => $"待处理 {SyncJobs.Count(j => j.Status is SyncStatus.Pending or SyncStatus.Syncing)} · 待重试 {SyncJobs.Count(IsRetryable)} · 已同步 {SyncJobs.Count(j => j.Status == SyncStatus.Synced)}";

    public SyncViewModel(IWordRepository wordRepository, IAnkiSyncQueue syncQueue)
    {
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
    }
    public Task InitializeAsync() => RefreshAsync();
    private bool CanRefresh() => !IsBusy;
    private static bool IsRetryable(SyncJob job) => job.Status is SyncStatus.Failed or SyncStatus.Retryable;
    private bool CanRetrySelected() => !IsBusy && SelectedJob != null && IsRetryable(SelectedJob);
    private bool CanRetryAll() => !IsBusy && SyncJobs.Any(IsRetryable);

    private async Task LoadSyncJobsAsync()
    {
        var list = await _wordRepository.GetSyncJobsAsync();
        var selectedId = SelectedJob?.Id;
        SyncJobs = new ObservableCollection<SyncJob>(list);
        SelectedJob = SyncJobs.FirstOrDefault(j => j.Id == selectedId);
        OnPropertyChanged(nameof(Summary));
        RetryAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "正在刷新…";
        try
        {
            await LoadSyncJobsAsync();
            StatusMessage = $"更新于 {DateTime.Now:HH:mm:ss} · 可选择失败任务查看原因并重试";
        }
        catch (Exception) { StatusMessage = "刷新失败，现有列表已保留，请重试。"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRetrySelected))]
    private async Task RetrySelectedAsync()
    {
        if (!CanRetrySelected()) return;
        var id = SelectedJob!.Id;
        await RetryAsync(() => _wordRepository.ResetSyncJobAsync(id));
    }

    [RelayCommand(CanExecute = nameof(CanRetryAll))]
    private async Task RetryAllAsync()
    {
        if (!CanRetryAll()) return;
        await RetryAsync(() => _wordRepository.ResetAllFailedSyncJobsAsync());
    }

    private async Task RetryAsync(Func<Task> reset)
    {
        IsBusy = true;
        StatusMessage = "正在提交重试…";
        try
        {
            await reset();
            _syncQueue.TriggerSync();
            await LoadSyncJobsAsync();
            StatusMessage = "已提交重试；请稍后刷新查看结果。";
        }
        catch (Exception) { StatusMessage = "重试未完成，请刷新队列后再试。"; }
        finally { IsBusy = false; }
    }
}

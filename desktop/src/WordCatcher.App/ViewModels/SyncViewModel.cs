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

public enum SyncPageState
{
    Disabled,
    Attention,
    Waiting,
    Complete,
    Empty
}

public sealed partial class SyncViewModel : ObservableObject
{
    private readonly IWordRepository _wordRepository;
    private readonly IAnkiSyncQueue _syncQueue;
    private readonly ISettingsService _settingsService;
    [ObservableProperty] private ObservableCollection<SyncJob> _syncJobs = new();
    [ObservableProperty]
    private ObservableCollection<SyncJob> _issueJobs = new();
    [ObservableProperty] private ObservableCollection<SyncJob> _recentJobs = new();
    private int _issueCount;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryAllCommand))]
    private bool _isAnkiEnabled;
    [ObservableProperty] private SyncPageState _pageState = SyncPageState.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryAllCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public int WaitingCount => SyncJobs.Count(j => j.Status is SyncStatus.Pending or SyncStatus.Syncing);
    public int IssueCount => _issueCount;
    public int MoreIssuesCount => Math.Max(0, IssueCount - IssueJobs.Count);
    public bool HasMoreIssues => MoreIssuesCount > 0;
    public int SyncedCount => SyncJobs.Count(j => j.Status == SyncStatus.Synced);
    public bool HasHistory => SyncJobs.Count > 0;
    public bool NeedsSetup => PageState == SyncPageState.Disabled;
    public bool HasActionableIssues => IsAnkiEnabled && IssueCount > 0;
    public string StateGlyph => PageState switch
    {
        SyncPageState.Disabled => "\uE713",
        SyncPageState.Attention => "\uE7BA",
        SyncPageState.Waiting => "\uE895",
        SyncPageState.Complete => "\uE73E",
        _ => "\uE8F1"
    };
    public string StateTitle => PageState switch
    {
        SyncPageState.Disabled => "Anki 同步未启用",
        SyncPageState.Attention => $"{IssueCount} 项同步需要处理",
        SyncPageState.Waiting => "正在等待同步",
        SyncPageState.Complete => "已同步到 Anki",
        _ => "还没有同步记录"
    };
    public string StateDescription => PageState switch
    {
        SyncPageState.Disabled => "收藏的单词仍保存在本地。可在设置中开启 Anki 同步。",
        SyncPageState.Attention => "收藏已保存在本地。请检查 Anki 连接或同步设置后重试。",
        SyncPageState.Waiting => "收藏已保存在本地，Anki 会在后台继续同步。",
        SyncPageState.Complete => "当前记录均已同步，无需其他操作。",
        _ => "收藏单词后，同步状态会显示在这里。"
    };

    public SyncViewModel(IWordRepository wordRepository, IAnkiSyncQueue syncQueue, ISettingsService settingsService)
    {
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
        _settingsService = settingsService;
    }
    public Task InitializeAsync() => RefreshAsync();
    private bool CanRefresh() => !IsBusy;
    private static bool IsRetryable(SyncJob job) => job.Status is SyncStatus.Failed or SyncStatus.Retryable;
    private bool CanRetryAll() => !IsBusy && HasActionableIssues;

    private async Task LoadSyncJobsAsync()
    {
        var list = await _wordRepository.GetSyncJobsAsync();
        SyncJobs = new ObservableCollection<SyncJob>(list);
        var issues = list.Where(IsRetryable).ToList();
        _issueCount = issues.Count;
        IssueJobs = new ObservableCollection<SyncJob>(issues.Take(20));
        RecentJobs = new ObservableCollection<SyncJob>(list.Take(50));
        UpdatePageState();
    }

    private void UpdatePageState()
    {
        PageState = !IsAnkiEnabled ? SyncPageState.Disabled
            : IssueCount > 0 ? SyncPageState.Attention
            : WaitingCount > 0 ? SyncPageState.Waiting
            : SyncedCount > 0 ? SyncPageState.Complete
            : SyncPageState.Empty;
        OnPropertyChanged(nameof(StateTitle));
        OnPropertyChanged(nameof(StateDescription));
        OnPropertyChanged(nameof(StateGlyph));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(IssueCount));
        OnPropertyChanged(nameof(MoreIssuesCount));
        OnPropertyChanged(nameof(HasMoreIssues));
        OnPropertyChanged(nameof(SyncedCount));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(NeedsSetup));
        OnPropertyChanged(nameof(HasActionableIssues));
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
            IsAnkiEnabled = _settingsService.Current.Anki.Enabled;
            await LoadSyncJobsAsync();
            StatusMessage = $"更新于 {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception) { StatusMessage = "状态更新失败，请重试。"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRetryAll))]
    private async Task RetryAllAsync()
    {
        if (!CanRetryAll()) return;
        IsBusy = true;
        StatusMessage = "正在提交重试…";
        try
        {
            await _wordRepository.ResetAllFailedSyncJobsAsync();
            _syncQueue.TriggerSync();
            await LoadSyncJobsAsync();
            StatusMessage = "已请求重试，后台将继续同步。";
        }
        catch (Exception) { StatusMessage = "重试未完成，请稍后再试。"; }
        finally { IsBusy = false; }
    }
}

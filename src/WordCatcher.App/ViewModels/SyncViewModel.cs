using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
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
    private readonly IWebDavSyncService? _webDav;
    private bool _webDavActionRunning;
    [ObservableProperty] private string _webDavActionMessage = string.Empty;
    public bool IsWebDavBusy => _webDavActionRunning || (_webDav?.IsSyncing ?? false);
    public bool HasWebDavConflict => _webDav?.Conflict != null;
    public string WebDavError => _webDav?.LastError ?? "";
    public bool IsWebDavEnabled => _settingsService.Current.WebDav.Enabled;
    public string WebDavLastSuccess => _webDav?.LastSyncedAtUtc is { } time
        ? $"上次同步：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : string.Empty;
    public string WebDavTitle => !_settingsService.Current.WebDav.Enabled ? "未启用"
        : IsWebDavBusy ? "同步中"
        : !string.IsNullOrEmpty(WebDavError) ? HasWebDavConflict ? "有冲突" : "同步失败"
        : (_webDav?.HasPendingChanges ?? false) ? "待同步"
        : (_webDav?.HasVerifiedSnapshot ?? false) ? "已同步" : "待同步";
    public string WebDavDescription => !_settingsService.Current.WebDav.Enabled ? "词条保存在本机。启用 WebDAV 后可与其他设备同步。"
        : _webDav?.FailureKind switch
        {
            WebDavFailureKind.Authentication => "请检查用户名、应用密码及服务器读写权限，然后重试。",
            WebDavFailureKind.Network => "网络连接失败或请求超时。本地修改已保留，恢复网络后可重试。",
            WebDavFailureKind.Conflict => HasWebDavConflict ? "请对照下方两端内容，选择保留的版本。一次处理一条语境。" : "云端数据已变化，请重新同步以获取最新内容。",
            WebDavFailureKind.Configuration => "请检查服务器地址、同步路径及文件格式；服务器需支持强 ETag。",
            WebDavFailureKind.Other => "本次同步未完成，请查看错误详情后重试。",
            _ => (_webDav?.HasPendingChanges ?? false)
                ? "本地有修改等待确认上传。同步期间新增的修改会保留在待同步状态。"
                : "WebDAV 用于设备间共享词库；下方 Anki 状态单独显示。"
        };
    public string LocalConflictText => DescribeConflict(_webDav?.Conflict?.LocalRecordJson);
    public string RemoteConflictText => DescribeConflict(_webDav?.Conflict?.RemoteRecordJson);
    public string ConflictRecordId => _webDav?.Conflict?.RecordId ?? "";

    private static string DescribeConflict(string? json)
    {
        if (json == null) return "";
        var r = JsonNode.Parse(json)!;
        var entry = r["card"]?["entry"];
        var edits = r["edits"];
        var meta = r["wordMeta"];
        string Text(JsonNode? value) => value?.ToString() ?? "";
        return $"状态：{(r["deletedAt"] != null ? "已删除（选择此版本将删除该语境）" : "保留记录")}\n"
            + $"表达：{Text(r["card"]?["selection"])}\n原句：{Text(r["card"]?["sentence"])}\n"
            + $"释义：{Text(edits?["definition"] ?? entry?["definition"])}\n译文：{Text(edits?["contextTranslation"] ?? entry?["contextTranslation"])}\n"
            + $"备注：{Text(edits?["note"])}\n读音：{Text(entry?["reading"])}\n词性：{Text(entry?["partOfSpeech"])}\n记忆线索：{Text(entry?["memoryHook"])}\n"
            + $"来源：{Text(r["card"]?["pageTitle"])}\n{Text(r["card"]?["pageUrl"])}\n"
            + $"\n词条摘要（同词语境共用）\n名称：{Text(meta?["displayWord"])}\n释义：{Text(meta?["definition"])}\n"
            + $"读音：{Text(meta?["reading"])}\n词性：{Text(meta?["partOfSpeech"])}\n记忆线索：{Text(meta?["memoryHook"])}";
    }

    private void RefreshWebDavState()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(RefreshWebDavState)); return; }
        foreach (var name in new[] { nameof(IsWebDavBusy), nameof(HasWebDavConflict), nameof(WebDavError), nameof(WebDavLastSuccess),
            nameof(IsWebDavEnabled), nameof(WebDavTitle), nameof(WebDavDescription), nameof(LocalConflictText), nameof(RemoteConflictText), nameof(ConflictRecordId) })
            OnPropertyChanged(name);
        RetryWebDavCommand.NotifyCanExecuteChanged();
        KeepLocalConflictCommand.NotifyCanExecuteChanged();
        KeepRemoteConflictCommand.NotifyCanExecuteChanged();
    }
    private bool CanRetryWebDav() => _webDav != null && !IsWebDavBusy && _settingsService.Current.WebDav.Enabled
        && !string.IsNullOrWhiteSpace(_settingsService.Current.WebDav.Url);
    private bool CanResolveWebDav() => CanRetryWebDav() && HasWebDavConflict;
    [RelayCommand(CanExecute = nameof(CanRetryWebDav))]
    private Task RetryWebDavAsync() => RunWebDavActionAsync(null);
    [RelayCommand(CanExecute = nameof(CanResolveWebDav))]
    private Task KeepLocalConflictAsync() => RunWebDavActionAsync(true);
    [RelayCommand(CanExecute = nameof(CanResolveWebDav))]
    private Task KeepRemoteConflictAsync() => RunWebDavActionAsync(false);
    private async Task RunWebDavActionAsync(bool? keepLocal)
    {
        if (!CanRetryWebDav()) return;
        _webDavActionRunning = true;
        WebDavActionMessage = "";
        RefreshWebDavState();
        try
        {
            if (keepLocal.HasValue) await _webDav!.ResolveConflictAsync(keepLocal.Value);
            else await _webDav!.SyncAsync(forcePush: true);
            WebDavActionMessage = "本轮同步完成。";
        }
        catch (Exception ex) { WebDavActionMessage = ex.Message; }
        finally { _webDavActionRunning = false; RefreshWebDavState(); }
    }
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

    public int WaitingCount => SyncJobs.Count(j => j.Status is SyncStatus.Pending or SyncStatus.Syncing
        || j.Status == SyncStatus.Retryable && j.NextAttemptAtUtc != null);
    public int IssueCount => _issueCount;
    public int MoreIssuesCount => Math.Max(0, IssueCount - IssueJobs.Count);
    public bool HasMoreIssues => MoreIssuesCount > 0;
    public int SyncedCount => SyncJobs.Count(j => j.Status == SyncStatus.Synced);
    public bool HasHistory => SyncJobs.Count > 0;
    public bool NeedsSetup => PageState == SyncPageState.Disabled;
    public bool HasActionableIssues => IsAnkiEnabled && IssueCount > 0;
    public string AnkiStatusLabel => PageState switch
    {
        SyncPageState.Disabled => "未启用",
        SyncPageState.Attention => $"{IssueCount} 项需处理",
        SyncPageState.Waiting => "待同步",
        SyncPageState.Complete => "已同步",
        _ => "暂无记录"
    };
    public string AnkiSummary => IsAnkiEnabled && HasHistory ? $"已同步 {SyncedCount} · 待同步 {WaitingCount} · 需处理 {IssueCount}" : string.Empty;
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
        SyncPageState.Disabled => WaitingCount + IssueCount > 0
            ? $"有 {WaitingCount + IssueCount} 项尚未同步，开启 Anki 同步后可继续处理。收藏已保存在生词库中。"
            : "将收藏的词条制作成 Anki 卡片，可在设置中开启。",
        SyncPageState.Attention => "收藏已保存在本地。请检查 Anki 连接或同步设置后重试。",
        SyncPageState.Waiting => "收藏已保存在本地，Anki 会在后台继续同步。",
        SyncPageState.Complete => "当前记录均已同步，无需其他操作。",
        _ => "收藏单词后，同步状态会显示在这里。"
    };

    public SyncViewModel(IWordRepository wordRepository, IAnkiSyncQueue syncQueue, ISettingsService settingsService, IWebDavSyncService? webDav = null)
    {
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
        _settingsService = settingsService;
        _webDav = webDav;
        if (_webDav != null) _webDav.StatusChanged += RefreshWebDavState;
        _settingsService.SettingsChanged += _ => RefreshWebDavState();
    }
    public Task InitializeAsync() => RefreshAsync();
    private bool CanRefresh() => !IsBusy;
    private static bool NeedsAttention(SyncJob job) => job.Status == SyncStatus.Failed
        || job.Status == SyncStatus.Retryable && job.NextAttemptAtUtc == null;
    private bool CanRetryAll() => !IsBusy && HasActionableIssues;

    private async Task LoadSyncJobsAsync()
    {
        var list = await _wordRepository.GetSyncJobsAsync();
        SyncJobs = new ObservableCollection<SyncJob>(list);
        var issues = list.Where(NeedsAttention).ToList();
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
        OnPropertyChanged(nameof(AnkiStatusLabel));
        OnPropertyChanged(nameof(AnkiSummary));
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
        RefreshWebDavState();
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

using System.IO;
using System.Reflection;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tests;

public class InteractionTests
{
    public class Proxy : DispatchProxy
    {
        public AppSettings Settings = new();
        public List<SyncJob> Jobs = [];
        public bool Fail;
        public int Writes;
        public int Triggers;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Current" => Settings,
            "GetApiKeyAsync" => Task.FromResult("test-key"),
            "SetApiKeyAsync" => Task.CompletedTask,
            "get_IsInstalled" => false,
            "SaveSettingsAsync" => Save((AppSettings)args![0]!),
            "GetSyncJobsAsync" => Fail ? Task.FromException<IReadOnlyList<SyncJob>>(new IOException()) : Task.FromResult<IReadOnlyList<SyncJob>>(Jobs.ToList()),
            "ResetSyncJobAsync" or "ResetAllFailedSyncJobsAsync" => Reset(),
            "TriggerSync" => Trigger(),
            _ => throw new NotSupportedException(method.Name)
        };
        private Task Save(AppSettings value)
        {
            if (Fail) throw new IOException();
            Writes++;
            Settings = value;
            return Task.CompletedTask;
        }
        private Task Reset() { Writes++; return Task.CompletedTask; }
        private object? Trigger() { Triggers++; return null; }
    }

    [Theory]
    [InlineData("Alt+Q", true, 81)]
    [InlineData("Ctrl+Shift+D", true, 68)]
    [InlineData("Alt+F8", true, 119)]
    [InlineData("Ctrl+", false, 0)]
    [InlineData("Alt+banana", false, 0)]
    [InlineData("Q", false, 0)]
    [InlineData("Ctrl+A+B", false, 0)]
    [InlineData("Ctrl++A", false, 0)]
    public void HotkeyRejectsInvalidInputInsteadOfSilentlyUsingC(string text, bool valid, int key)
    {
        Assert.Equal(valid, HotkeyManager.TryParseHotkey(text, out _, out var actual));
        if (valid) Assert.Equal((uint)key, actual);
    }

    public static (SettingsViewModel Vm, Proxy Service) Settings()
    {
        var settings = DispatchProxy.Create<ISettingsService, Proxy>();
        var secrets = DispatchProxy.Create<ISecretStore, Proxy>();
        var dict = DispatchProxy.Create<IOfflineDictionaryService, Proxy>();
        return (new SettingsViewModel(settings, secrets, dict, null!, null!, null!, new HotkeyManager()), (Proxy)settings);
    }

    [Fact]
    public async Task FailedSettingsSaveDoesNotChangeActiveConfiguration()
    {
        var (vm, service) = Settings();
        await vm.InitializeAsync();
        vm.ApiModel = "draft-model";
        service.Fail = true;
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal("gpt-4o-mini", service.Settings.ActiveTranslation.Model);
        Assert.Equal("draft-model", vm.ApiModel);
        Assert.False(vm.IsSaving);
        Assert.Contains("未完整保存", vm.StatusMessage);
    }

    [Fact]
    public async Task InvalidSettingsAreRejectedBeforeSaving()
    {
        var (vm, service) = Settings();
        await vm.InitializeAsync();
        vm.Hotkey = "Alt+";
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("快捷键格式无效", vm.StatusMessage);
        Assert.Equal(0, service.Writes);
    }

    [Fact]
    public async Task EditedAnkiAddressIsNotTestedAgainstStaleSettings()
    {
        var (vm, _) = Settings();
        await vm.InitializeAsync();
        vm.AnkiUrl = "http://localhost:1234";
        await vm.TestAnkiConnectionCommand.ExecuteAsync(null);
        Assert.Contains("请先保存", vm.StatusMessage);
    }

    [Fact]
    public async Task FailedJobsCanBeRetriedAndRefreshFailurePreservesList()
    {
        var repo = DispatchProxy.Create<IWordRepository, Proxy>();
        var proxy = (Proxy)repo;
        proxy.Jobs.Add(new SyncJob { Id = "synced", Status = SyncStatus.Synced });
        proxy.Jobs.Add(new SyncJob { Id = "failed", Status = SyncStatus.Failed });
        var queue = DispatchProxy.Create<IAnkiSyncQueue, Proxy>();
        var settings = DispatchProxy.Create<ISettingsService, Proxy>();
        var vm = new SyncViewModel(repo, queue, settings);
        await vm.InitializeAsync();
        Assert.Equal(SyncPageState.Attention, vm.PageState);
        Assert.Single(vm.IssueJobs);
        Assert.True(vm.RetryAllCommand.CanExecute(null));
        await vm.RetryAllCommand.ExecuteAsync(null);
        Assert.Equal(1, proxy.Writes);
        Assert.Equal(1, ((Proxy)queue).Triggers);
        proxy.Fail = true;
        await vm.RefreshAsync();
        Assert.Equal(2, vm.SyncJobs.Count);
        Assert.False(vm.IsBusy);
        Assert.Contains("更新失败", vm.StatusMessage);
    }

    [Fact]
    public async Task SyncPageStatePrioritizesSetupIssuesAndPendingWork()
    {
        var repo = DispatchProxy.Create<IWordRepository, Proxy>();
        var jobs = (Proxy)repo;
        var queue = DispatchProxy.Create<IAnkiSyncQueue, Proxy>();
        var settings = DispatchProxy.Create<ISettingsService, Proxy>();
        var vm = new SyncViewModel(repo, queue, settings);

        await vm.RefreshAsync();
        Assert.Equal(SyncPageState.Empty, vm.PageState);
        Assert.False(vm.HasHistory);

        jobs.Jobs.Add(new SyncJob { Status = SyncStatus.Pending });
        await vm.RefreshAsync();
        Assert.Equal(SyncPageState.Waiting, vm.PageState);
        Assert.Equal(1, vm.WaitingCount);

        jobs.Jobs[0].Status = SyncStatus.Synced;
        await vm.RefreshAsync();
        Assert.Equal(SyncPageState.Complete, vm.PageState);
        Assert.Equal(1, vm.SyncedCount);

        jobs.Jobs.Add(new SyncJob { Status = SyncStatus.Failed, LastError = "Anki unavailable" });
        await vm.RefreshAsync();
        Assert.Equal(SyncPageState.Attention, vm.PageState);
        Assert.True(vm.HasActionableIssues);
        Assert.Single(vm.IssueJobs);

        for (var index = 0; index < 24; index++)
            jobs.Jobs.Add(new SyncJob { Status = SyncStatus.Failed });
        await vm.RefreshAsync();
        Assert.Equal(25, vm.IssueCount);
        Assert.Equal(20, vm.IssueJobs.Count);
        Assert.Equal(5, vm.MoreIssuesCount);
        Assert.True(vm.HasMoreIssues);

        ((Proxy)settings).Settings.Anki.Enabled = false;
        await vm.RefreshAsync();
        Assert.Equal(SyncPageState.Disabled, vm.PageState);
        Assert.True(vm.NeedsSetup);
        Assert.False(vm.HasActionableIssues);
        Assert.False(vm.RetryAllCommand.CanExecute(null));
    }
}

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
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Current" => Settings,
            "GetApiKeyAsync" => Task.FromResult("test-key"),
            "SetApiKeyAsync" => Task.CompletedTask,
            "get_IsInstalled" => false,
            "SaveSettingsAsync" => Save((AppSettings)args![0]!),
            "GetSyncJobsAsync" => Fail ? Task.FromException<IReadOnlyList<SyncJob>>(new IOException()) : Task.FromResult<IReadOnlyList<SyncJob>>(Jobs.ToList()),
            "ResetSyncJobAsync" or "ResetAllFailedSyncJobsAsync" => Reset(),
            "TriggerSync" => null,
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
    public async Task SyncedJobsCannotBeRetriedAndRefreshFailurePreservesList()
    {
        var repo = DispatchProxy.Create<IWordRepository, Proxy>();
        var proxy = (Proxy)repo;
        proxy.Jobs.Add(new SyncJob { Id = "synced", Status = SyncStatus.Synced });
        proxy.Jobs.Add(new SyncJob { Id = "failed", Status = SyncStatus.Failed });
        var queue = DispatchProxy.Create<IAnkiSyncQueue, Proxy>();
        var vm = new SyncViewModel(repo, queue);
        await vm.InitializeAsync();
        vm.SelectedJob = vm.SyncJobs[0];
        Assert.False(vm.RetrySelectedCommand.CanExecute(null));
        vm.SelectedJob = vm.SyncJobs[1];
        Assert.True(vm.RetrySelectedCommand.CanExecute(null));
        Assert.True(vm.RetryAllCommand.CanExecute(null));
        proxy.Fail = true;
        await vm.RefreshAsync();
        Assert.Equal(2, vm.SyncJobs.Count);
        Assert.False(vm.IsBusy);
        Assert.Contains("刷新失败", vm.StatusMessage);
    }
}

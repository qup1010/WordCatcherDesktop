using System.Net;
using System.Net.Http;
using System.IO;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tests;

public sealed class AppUpdateTests
{
    [Fact]
    public void DevelopmentBuildDoesNotClaimToBeInnoManagedInstallation()
    {
        using var httpClient = new HttpClient();
        var updater = new AppUpdater(httpClient);
        Assert.False(updater.IsInstalled);
        Assert.False(updater.IsUpdateReady);
    }

    [Fact]
    public async Task NewReleaseDownloadsButDoesNotRestartUntilRequested()
    {
        var updater = new FakeUpdater();
        var vm = Create(updater);
        var installs = 0;
        vm.UpdateInstallRequested += () => installs++;

        Assert.False(vm.HasCheckedForUpdates);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, updater.Downloads);
        Assert.Equal("99.0.0", updater.DownloadedVersion);
        Assert.Equal("https://github.com/qup1010/WordCatcherDesktop/releases/download/v99.0.0/WordCatcherDesktop-win-Setup.exe", updater.DownloadedInstallerUri!.ToString());
        Assert.True(vm.HasCheckedForUpdates);
        Assert.True(vm.IsUpdateReady);
        Assert.Equal(0, installs);
        vm.InstallUpdateCommand.Execute(null);
        Assert.Equal(1, installs);
    }

    [Fact]
    public async Task UnmanagedPortableBuildProvidesDownloadPageInsteadOfReplacingItself()
    {
        var updater = new FakeUpdater { IsInstalled = false };
        var vm = Create(updater);
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(0, updater.Downloads);
        Assert.False(vm.IsUpdateReady);
        Assert.Contains("Setup", vm.UpdateStatus);
    }

    [Fact]
    public async Task SettingsDraftBlocksRestartWithoutDroppingDraft()
    {
        var vm = Create(new FakeUpdater());
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        vm.HasUnsavedChanges = true;
        var installs = 0;
        vm.UpdateInstallRequested += () => installs++;
        vm.InstallUpdateCommand.Execute(null);
        Assert.Equal(0, installs);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Contains("保存", vm.UpdateStatus);
    }

    [Fact]
    public async Task FailedDownloadLeavesRetryAvailableAndNoInstallButton()
    {
        var vm = Create(new FakeUpdater { Fail = true });
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.False(vm.IsUpdateReady);
        Assert.False(vm.IsCheckingForUpdates);
    }

    [Fact]
    public async Task CancelledDownloadDoesNotBecomeReadyAndCanRetry()
    {
        var updater = new FakeUpdater { WaitForCancel = true };
        var vm = Create(updater);
        var task = vm.CheckForUpdatesCommand.ExecuteAsync(null);
        await updater.Started.Task;
        vm.CheckForUpdatesCancelCommand.Execute(null);
        await task;
        Assert.False(vm.IsUpdateReady);
        Assert.False(vm.IsCheckingForUpdates);
        Assert.Contains("取消", vm.UpdateStatus);
        updater.WaitForCancel = false;
        await vm.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(vm.IsUpdateReady);
    }

    private static SettingsViewModel Create(FakeUpdater updater)
    {
        var settings = new FakeSettings();
        var client = new HttpClient(new ReleaseHandler());
        return new SettingsViewModel(settings, null!, null!, null!, null!, null!, null!,
            new UpdateCheckService(client), appUpdater: updater);
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public bool IsInstalled { get; set; } = true;
        public bool IsUpdateReady { get; private set; }
        public int Downloads;
        public string? DownloadedVersion;
        public Uri? DownloadedInstallerUri;
        public bool Fail;
        public bool WaitForCancel;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> DownloadLatestAsync(Uri installerUri, string version, Action<int> progress, CancellationToken ct)
        {
            Downloads++;
            DownloadedInstallerUri = installerUri;
            DownloadedVersion = version;
            Started.TrySetResult();
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, ct);
            if (Fail) throw new IOException("download failed");
            IsUpdateReady = true;
            return true;
        }
        public void LaunchInstaller() => throw new InvalidOperationException("Tests must not start an installer.");
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event Action<AppSettings>? SettingsChanged { add { } remove { } }
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"tag_name\":\"v99.0.0\",\"assets\":[{\"name\":\"WordCatcherDesktop-win-Setup.exe\",\"browser_download_url\":\"https://github.com/qup1010/WordCatcherDesktop/releases/download/v99.0.0/WordCatcherDesktop-win-Setup.exe\"}]}")
            });
    }
}

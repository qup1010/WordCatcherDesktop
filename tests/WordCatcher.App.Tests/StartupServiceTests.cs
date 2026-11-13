using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using WordCatcher.App.Services;

namespace WordCatcher.App.Tests;

public sealed class StartupServiceTests
{
    [Theory]
    [InlineData("\"C:\\Old Folder\\WordCatcher.App.exe\"")]
    [InlineData("\"C:\\Old Folder\\WordCatcher.App.exe\" --startup")]
    public async Task MovedApplicationCanRepairOrRemoveOldStartupEntryWithoutLosingDraft(string oldCommand)
    {
        using var registration = new StartupTestRegistration();
        using (var key = Registry.CurrentUser.CreateSubKey(registration.KeyPath))
        {
            key.SetValue("WordCatcherDesktop", oldCommand);
            key.SetValue("OtherApplication", "keep this value");
        }
        var (vm, _) = InteractionTests.Settings(registration.Service);
        await vm.InitializeAsync();
        Assert.True(vm.StartWithWindows);
        Assert.True(vm.StartupPathNeedsRepair);
        vm.ApiModel = "unsaved-model";
        vm.RepairStartupPathCommand.Execute(null);
        Assert.True(vm.StartWithWindows);
        Assert.False(vm.StartupPathNeedsRepair);
        using (var key = Registry.CurrentUser.OpenSubKey(registration.KeyPath))
            Assert.Equal($"\"{registration.Executable}\" --startup", key!.GetValue("WordCatcherDesktop"));
        // An external change must refresh even while the settings editor has a draft.
        registration.Service.SetEnabled(false);
        await vm.InitializeAsync();
        Assert.False(vm.StartWithWindows);
        Assert.Equal("unsaved-model", vm.ApiModel);
        Assert.True(vm.HasUnsavedChanges);
        using (var key = Registry.CurrentUser.OpenSubKey(registration.KeyPath))
            Assert.Equal("keep this value", key!.GetValue("OtherApplication"));
    }

    [Fact]
    public void DuplicateLoginStartupExitsWithoutShowingADialog()
    {
        using var mutex = new Mutex(true, "WordCatcher_Desktop_SingleInstance_Mutex", out var ownsMutex);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "WordCatcher.App.exe"),
                Arguments = "--startup",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            try
            {
                Assert.True(process.WaitForExit(10000), "重复自启动未静默退出，可能被提示对话框阻塞。");
                Assert.Equal(0, process.ExitCode);
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        finally { if (ownsMutex) mutex.ReleaseMutex(); }
    }

    [Fact]
    public async Task StartupPreferencePersistsImmediatelyAndCanBeRemoved()
    {
        using var registration = new StartupTestRegistration();
        var (vm, _) = InteractionTests.Settings(registration.Service);
        await vm.InitializeAsync();
        Assert.False(vm.StartWithWindows);
        vm.StartWithWindows = true;
        Assert.True(new StartupService(registration.KeyPath, registration.Executable).IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(registration.KeyPath))
            Assert.Equal($"\"{registration.Executable}\" --startup", key!.GetValue("WordCatcherDesktop"));
        Assert.False(vm.HasUnsavedChanges);
        await vm.InitializeAsync();
        Assert.True(vm.StartWithWindows);
        vm.StartWithWindows = false;
        using (var key = Registry.CurrentUser.OpenSubKey(registration.KeyPath))
            Assert.Null(key!.GetValue("WordCatcherDesktop"));
        Assert.False(registration.Service.IsEnabled);
    }

    [Fact]
    public async Task MissingExecutableRestoresSwitchAndShowsFailure()
    {
        using var registration = new StartupTestRegistration();
        File.Delete(registration.Executable);
        var (vm, _) = InteractionTests.Settings(registration.Service);
        await vm.InitializeAsync();
        vm.StartWithWindows = true;
        Assert.False(vm.StartWithWindows);
        Assert.False(registration.Service.IsEnabled);
        Assert.Contains("找不到应用程序", vm.StartupStatus);
    }
}

internal sealed class StartupTestRegistration : IDisposable
{
    public string KeyPath { get; } = @"Software\WordCatcherDesktop.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WordCatcher startup tests", Guid.NewGuid().ToString("N"));
    public string Executable { get; }
    public StartupService Service { get; }

    public StartupTestRegistration()
    {
        Directory.CreateDirectory(_directory);
        Executable = Path.Combine(_directory, "WordCatcher.App.exe");
        File.WriteAllText(Executable, "test fixture; never executed");
        Service = new StartupService(KeyPath, Executable);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        File.Delete(Executable);
        Directory.Delete(_directory);
    }
}

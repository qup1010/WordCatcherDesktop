using System.IO;
using System.Reflection;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Settings;

namespace WordCatcher.App.Tests;

public class MouseSelectionMonitorTests
{
    [Fact]
    public void NativeHookUsesDedicatedThreadAndJoinsOnStopAndDispose()
    {
        var settings = new MouseSelectionSettings { Enabled = false };
        using var monitor = new MouseSelectionMonitor(new FakePlatform(), () => settings, () => false);
        monitor.UpdateHook(hasUnpinnedLookup: false);
        Assert.Null(monitor.HookThread);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            monitor.UpdateHook(hasUnpinnedLookup: true);
            var thread = Assert.IsType<Thread>(monitor.HookThread);
            Assert.NotSame(Thread.CurrentThread, thread);
            Assert.True(thread.IsBackground);
            Assert.True(thread.IsAlive);
            monitor.UpdateHook(hasUnpinnedLookup: true);
            Assert.Same(thread, monitor.HookThread);

            monitor.UpdateHook(hasUnpinnedLookup: false);
            Assert.False(thread.IsAlive);
            Assert.Null(monitor.HookThread);
        }

        settings.Enabled = true;
        monitor.UpdateHook(hasUnpinnedLookup: false);
        var captureThread = Assert.IsType<Thread>(monitor.HookThread);
        Assert.True(captureThread.IsAlive);
        monitor.Dispose();
        Assert.False(captureThread.IsAlive);
        Assert.Null(monitor.HookThread);
        Assert.Throws<ObjectDisposedException>(() => monitor.UpdateHook(hasUnpinnedLookup: true));
    }

    [Theory]
    [InlineData(0x0200, null)] // WM_MOUSEMOVE
    [InlineData(0x0201, true)] // WM_LBUTTONDOWN
    [InlineData(0x0202, null)] // WM_LBUTTONUP
    [InlineData(0x0204, true)] // WM_RBUTTONDOWN
    [InlineData(0x020A, false)] // WM_MOUSEWHEEL
    [InlineData(0x020E, false)] // WM_MOUSEHWHEEL
    public void OnlyMouseDownRequestsLookupDismissal(int message, bool? expected)
    {
        using var monitor = new MouseSelectionMonitor(new FakePlatform(), () => new(), () => false);
        var point = new ScreenPoint(-150, 60);
        var requests = new List<(ScreenPoint Point, bool IsMouseDown)>();
        monitor.DismissRequested += (position, isMouseDown) => requests.Add((position, isMouseDown));

        monitor.NotifyDismissal(message, point);

        if (expected is { } mouseDown)
            Assert.Equal((point, mouseDown), Assert.Single(requests));
        else
            Assert.Empty(requests);
    }

    [Theory]
    [InlineData(MouseSelectionSettings.Pill, "hello", false)]
    [InlineData(MouseSelectionSettings.AutoEnglish, "hello", true)]
    [InlineData(MouseSelectionSettings.AutoEnglish, "你好", false)]
    [InlineData(MouseSelectionSettings.AutoEnglish, "https://example.com", false)]
    [InlineData(MouseSelectionSettings.AutoAll, "你好", true)]
    [InlineData(MouseSelectionSettings.AutoAll, "hello", true)]
    [InlineData(" AUTO-ENGLISH ", "hello", true)]
    [InlineData("unknown", "hello", false)]
    [InlineData(null, "hello", false)]
    public void SelectionTriggerModeControlsWhetherLookupOpensDirectly(string? mode, string text, bool expected)
    {
        Assert.Equal(expected, App.ShouldAutoTranslateSelection(mode, text));
    }

    [Fact]
    public void BlacklistParsesSeparatorsAndNormalizesProcessNames()
    {
        var blacklist = MouseSelectionMonitor.ParseBlacklistText(" , CMD.exe, pwsh\r\nMSTSC\tobs64  cmd.EXE");
        Assert.Equal(new[] { "CMD", "pwsh", "MSTSC", "obs64" }, blacklist);
        Assert.True(MouseSelectionMonitor.IsProcessBlacklisted(" cmd.EXE ", blacklist));
        Assert.True(MouseSelectionMonitor.IsProcessBlacklisted("PwSh", [" ", " pwsh.exe "]));
        Assert.False(MouseSelectionMonitor.IsProcessBlacklisted("cmdline.exe", blacklist));
        Assert.False(MouseSelectionMonitor.IsProcessBlacklisted(null, [" "]));
        Assert.False(MouseSelectionMonitor.IsProcessBlacklisted("cmd", null));
    }

    [Fact]
    public void GestureThresholdsUsePhysicalDistanceAndClickTiming()
    {
        var first = new ScreenPoint(-200, 300);
        Assert.False(MouseSelectionMonitor.IsDragSelection(first, new(-193, 308)));
        Assert.True(MouseSelectionMonitor.IsDragSelection(first, new(-188, 300)));
        Assert.True(MouseSelectionMonitor.IsDragSelection(first, new(-191, 309)));
        Assert.True(MouseSelectionMonitor.IsDoubleClickSelection(first, new(-194, 300), 100, 600, 500));
        Assert.False(MouseSelectionMonitor.IsDoubleClickSelection(first, new(-194, 301), 100, 600, 500));
        Assert.False(MouseSelectionMonitor.IsDoubleClickSelection(first, first, 100, 601, 500));
        Assert.True(MouseSelectionMonitor.IsDoubleClickSelection(first, first, uint.MaxValue - 1, 1, 500));
    }

    [Fact]
    public async Task ReadsSelectionSilentlyAndReturnsReleaseCoordinates()
    {
        var platform = new FakePlatform();
        using var monitor = new MouseSelectionMonitor(platform, () => new() { Enabled = true }, () => false);
        var cursor = new ScreenPoint(-180, 250);

        var result = await monitor.ReadSelectionAsync(platform.Source, cursor, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("selected", result.SelectedText);
        Assert.Equal(cursor, result.CursorPosition);
        Assert.Equal(cursor, Assert.Single(platform.Reads).Cursor);
        Assert.Equal(0, platform.ClipboardCalls);
    }

    [Fact]
    public async Task IneligibleForegroundNeverReachesAutomation()
    {
        var platform = new FakePlatform();
        var settings = new MouseSelectionSettings();
        var fullScreen = false;
        using var monitor = new MouseSelectionMonitor(platform, () => settings, () => fullScreen);
        Assert.Null(await monitor.ReadSelectionAsync(platform.Source, default, CancellationToken.None));

        settings.Enabled = true;
        settings.BlacklistedProcesses = ["editor.exe"];
        Assert.Null(await monitor.ReadSelectionAsync(platform.Source, default, CancellationToken.None));

        settings.BlacklistedProcesses.Clear();
        Assert.Null(await monitor.ReadSelectionAsync(platform.Source with { ProcessId = (uint)Environment.ProcessId },
            default, CancellationToken.None));
        fullScreen = true;
        Assert.Null(await monitor.ReadSelectionAsync(platform.Source, default, CancellationToken.None));
        Assert.Empty(platform.Reads);
        Assert.Equal(0, platform.ClipboardCalls);
    }

    [Fact]
    public async Task SettingsLoadTrackDraftAndPersistMouseSelection()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var settings = new JsonSettingsService(path);
            Assert.False(settings.Current.MouseSelection.Enabled);
            Assert.Contains("WindowsTerminal", settings.Current.MouseSelection.BlacklistedProcesses);
            await settings.SaveSettingsAsync(new AppSettings
            {
                MouseSelection = new() { Enabled = true, BlacklistedProcesses = ["cmd", "mstsc"] }
            });
            var notifications = new List<AppSettings>();
            settings.SettingsChanged += notifications.Add;
            var vm = new SettingsViewModel(settings, DispatchProxy.Create<ISecretStore, InteractionTests.Proxy>(),
                DispatchProxy.Create<IOfflineDictionaryService, InteractionTests.Proxy>(),
                null!, null!, null!, new HotkeyManager());

            await vm.InitializeAsync();
            Assert.True(vm.MouseSelectionEnabled);
            Assert.Equal($"cmd{Environment.NewLine}mstsc", vm.MouseSelectionBlacklistText);
            Assert.False(vm.HasUnsavedChanges);

            vm.MouseSelectionEnabled = false;
            Assert.True(vm.HasUnsavedChanges);
            vm.MouseSelectionEnabled = true;
            Assert.False(vm.HasUnsavedChanges);
            vm.MouseSelectionBlacklistText = "pwsh.exe, obs64\n obs64";
            Assert.True(vm.HasUnsavedChanges);
            vm.MouseSelectionEnabled = false;
            Assert.True(settings.Current.MouseSelection.Enabled);
            Assert.Empty(notifications);

            await vm.SaveSettingsCommand.ExecuteAsync(null);

            Assert.False(vm.HasUnsavedChanges);
            Assert.False(Assert.Single(notifications).MouseSelection.Enabled);
            var reloaded = new JsonSettingsService(path).Current.MouseSelection;
            Assert.False(reloaded.Enabled);
            Assert.Equal(new[] { "pwsh", "obs64" }, reloaded.BlacklistedProcesses);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class FakePlatform : ISelectionCapturePlatform
    {
        public CaptureSource Source { get; } = new(new IntPtr(1), uint.MaxValue, "editor", "Test editor", default);
        public List<CaptureSource> Reads { get; } = [];
        public int ClipboardCalls { get; private set; }

        public CaptureSource GetSource() => Source;
        public bool IsCurrent(CaptureSource source) => source.Window == Source.Window && source.ProcessId == Source.ProcessId;
        public Task<SelectionSnapshot?> ReadSelectionAsync(CaptureSource source, string? expectedText = null)
        {
            Assert.Null(expectedText);
            Reads.Add(source);
            return Task.FromResult<SelectionSnapshot?>(new("  selected  ", "selected text", 0));
        }

        public Task WaitForModifiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public uint ClipboardSequence => throw UnexpectedClipboardAccess();
        public Task<IClipboardBackup> BackupClipboardAsync() => throw UnexpectedClipboardAccess();
        public void SendCopy() => throw UnexpectedClipboardAccess();
        public Task<ClipboardRead> ReadCopiedTextAsync(CaptureSource source, uint expectedSequence) => throw UnexpectedClipboardAccess();
        public Task<bool> RestoreClipboardAsync(IClipboardBackup backup, uint expectedSequence) => throw UnexpectedClipboardAccess();

        private InvalidOperationException UnexpectedClipboardAccess()
        {
            ClipboardCalls++;
            return new InvalidOperationException("Mouse selection must not touch the clipboard.");
        }
    }
}

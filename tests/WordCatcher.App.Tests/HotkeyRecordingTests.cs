using System.Reflection;
using System.Windows.Input;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Interfaces;

namespace WordCatcher.App.Tests;

public class HotkeyRecordingTests
{
    [Theory]
    [InlineData(Key.Q, ModifierKeys.Alt, "Alt+Q")]
    [InlineData(Key.D5, ModifierKeys.Control, "Ctrl+5")]
    [InlineData(Key.F8, ModifierKeys.Control | ModifierKeys.Shift, "Ctrl+Shift+F8")]
    [InlineData(Key.D, ModifierKeys.Windows | ModifierKeys.Alt, "Alt+Win+D")]
    public void RecordsCanonicalCombination(Key key, ModifierKeys modifiers, string expected)
    {
        Assert.True(SettingsViewModel.TryFormatHotkey(key, modifiers, out var text));
        Assert.Equal(expected, text);
        Assert.True(HotkeyManager.TryParseHotkey(text, out _, out _));
    }

    [Fact]
    public void InvalidInputKeepsRecordingAndEscapePreservesDraft()
    {
        var (vm, _) = InteractionTests.Settings();
        vm.Hotkey = "Ctrl+D";
        vm.RecordHotkeyCommand.Execute(null);
        vm.CaptureHotkey(Key.LeftCtrl, ModifierKeys.Control);
        Assert.True(vm.IsRecordingHotkey);
        vm.CaptureHotkey(Key.A, ModifierKeys.None);
        Assert.True(vm.IsRecordingHotkey);
        Assert.Contains("请使用", vm.HotkeyHint);
        vm.CaptureHotkey(Key.Escape, ModifierKeys.None);
        Assert.False(vm.IsRecordingHotkey);
        Assert.Equal("Ctrl+D", vm.Hotkey);
    }

    [Fact]
    public void CompletedRecordingUpdatesDraftOnlyAndResetIsExplicit()
    {
        var (vm, service) = InteractionTests.Settings();
        vm.RecordHotkeyCommand.Execute(null);
        vm.CaptureHotkey(Key.F8, ModifierKeys.Alt);
        Assert.False(vm.IsRecordingHotkey);
        Assert.Equal("Alt+F8", vm.Hotkey);
        Assert.Equal("Alt+Q", vm.AppliedHotkey);
        Assert.Equal(0, service.Writes);
        vm.ResetHotkeyCommand.Execute(null);
        Assert.Equal("Alt+Q", vm.Hotkey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellingRestoresPreviousHotkeyEnabledState(bool enabled)
    {
        using var manager = new HotkeyManager();
        manager.SetEnabled(enabled);
        var vm = new SettingsViewModel(DispatchProxy.Create<ISettingsService, InteractionTests.Proxy>(),
            null!, null!, null!, null!, null!, manager);
        vm.RecordHotkeyCommand.Execute(null);
        Assert.False(manager.IsEnabled);
        vm.CancelHotkeyRecording();
        Assert.Equal(enabled, manager.IsEnabled);
    }

    [Fact]
    public async Task SaveWhileRecordingDoesNotApplyUnfinishedInput()
    {
        var (vm, service) = InteractionTests.Settings();
        await vm.InitializeAsync();
        vm.RecordHotkeyCommand.Execute(null);
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Writes);
        Assert.Contains("完成或取消", vm.StatusMessage);
        vm.CancelHotkeyRecording();
    }
}

using System.Reflection;
using System.Windows.Input;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Models;
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
        Assert.Equal("Ctrl+Shift+Z", vm.AppliedHotkey);
        Assert.Equal(0, service.Writes);
        vm.ResetHotkeyCommand.Execute(null);
        Assert.Equal("Ctrl+Shift+Z", vm.Hotkey);
    }

    [Fact]
    public async Task OcrHotkeyDefaultsToCtrlShiftOAndTracksUnsavedChanges()
    {
        var (vm, service) = InteractionTests.Settings();
        await vm.InitializeAsync();

        Assert.Equal("Ctrl+Shift+O", AppSettings.DefaultOcrHotkey);
        Assert.Equal(AppSettings.DefaultOcrHotkey, vm.OcrHotkey);
        Assert.Equal(AppSettings.DefaultOcrHotkey, vm.AppliedOcrHotkey);
        Assert.False(vm.HasUnsavedChanges);

        vm.OcrHotkey = "Alt+F8";
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(0, service.Writes);
    }

    [Fact]
    public void OcrHotkeyCanBeRecordedCancelledAndResetToDefault()
    {
        using var manager = new HotkeyManager();
        manager.SetEnabled(true);
        var settings = DispatchProxy.Create<ISettingsService, InteractionTests.Proxy>();
        var vm = new SettingsViewModel(settings, null!, null!, null!, null!, null!, manager);

        vm.RecordOcrHotkeyCommand.Execute(null);
        Assert.True(vm.IsRecordingOcrHotkey);
        Assert.False(manager.IsEnabled);
        vm.CaptureOcrHotkey(Key.F8, ModifierKeys.Alt);
        Assert.False(vm.IsRecordingOcrHotkey);
        Assert.Equal("Alt+F8", vm.OcrHotkey);
        Assert.True(manager.IsEnabled);

        vm.RecordOcrHotkeyCommand.Execute(null);
        vm.CaptureOcrHotkey(Key.F9, ModifierKeys.Alt);
        vm.ResetOcrHotkeyCommand.Execute(null);
        Assert.Equal(AppSettings.DefaultOcrHotkey, vm.OcrHotkey);

        vm.RecordOcrHotkeyCommand.Execute(null);
        vm.CaptureOcrHotkey(Key.Escape, ModifierKeys.None);
        Assert.False(vm.IsRecordingOcrHotkey);
        Assert.Equal(AppSettings.DefaultOcrHotkey, vm.OcrHotkey);
        Assert.True(manager.IsEnabled);
    }

    [Fact]
    public async Task OcrHotkeyCannotConflictWithLookupHotkey()
    {
        var (vm, service) = InteractionTests.Settings();
        await vm.InitializeAsync();
        vm.Hotkey = "Ctrl+Shift+O";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Contains("不能与取词快捷键相同", vm.StatusMessage);
        Assert.Equal(0, service.Writes);
        Assert.Equal(AppSettings.DefaultHotkey, service.Settings.Hotkey);
        Assert.Equal(AppSettings.DefaultOcrHotkey, service.Settings.OcrHotkey);
    }

    [Fact]
    public async Task SavingOcrHotkeyRegistersAndPersistsIt()
    {
        var settings = DispatchProxy.Create<ISettingsService, InteractionTests.Proxy>();
        var secrets = DispatchProxy.Create<ISecretStore, InteractionTests.Proxy>();
        var dict = DispatchProxy.Create<IOfflineDictionaryService, InteractionTests.Proxy>();
        using var manager = new HotkeyManager();
        var registrations = new List<string>();
        var vm = new SettingsViewModel(settings, secrets, dict, null!, null!, null!, manager,
            ocrHotkeyRegistrar: hotkey => { registrations.Add(hotkey); return true; });
        var service = (InteractionTests.Proxy)settings;
        await vm.InitializeAsync();
        vm.OcrHotkey = "Alt+F8";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Equal(["Alt+F8"], registrations);
        Assert.Equal("Alt+F8", service.Settings.OcrHotkey);
        Assert.Equal("Alt+F8", vm.AppliedOcrHotkey);
        Assert.Equal(1, service.Writes);
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public async Task FailedOcrRegistrationKeepsPersistedHotkeyAndMainHotkey()
    {
        var settings = DispatchProxy.Create<ISettingsService, InteractionTests.Proxy>();
        var secrets = DispatchProxy.Create<ISecretStore, InteractionTests.Proxy>();
        var dict = DispatchProxy.Create<IOfflineDictionaryService, InteractionTests.Proxy>();
        using var manager = new HotkeyManager();
        var vm = new SettingsViewModel(settings, secrets, dict, null!, null!, null!, manager,
            ocrHotkeyRegistrar: _ => false);
        var service = (InteractionTests.Proxy)settings;
        await vm.InitializeAsync();
        vm.OcrHotkey = "Alt+F8";

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.Contains("截屏 OCR 快捷键注册失败", vm.StatusMessage);
        Assert.Equal(0, service.Writes);
        Assert.Equal(AppSettings.DefaultHotkey, service.Settings.Hotkey);
        Assert.Equal(AppSettings.DefaultOcrHotkey, service.Settings.OcrHotkey);
        Assert.Equal(AppSettings.DefaultOcrHotkey, vm.AppliedOcrHotkey);
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

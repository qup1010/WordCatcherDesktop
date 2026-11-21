using System;
using System.Drawing;
using System.Windows.Forms;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly HotkeyManager _hotkeyManager;
    private readonly ISettingsService? _settingsService;
    private Action? _notificationAction;
    private TrayContextMenuWindow? _activeMenuWindow;
    private bool _disposed;

    public event Action? ToggleLibraryRequested;
    public event Action? OpenLibraryRequested;
    public event Action? OpenLookupRequested;
    public event Action? OpenSettingsRequested;
#pragma warning disable CS0067 // Retained for backwards compatibility with existing subscribers
    public event Action? CaptureNowRequested;
#pragma warning restore CS0067
    public event Action? OcrCaptureRequested;
    public event Action<bool>? MouseSelectionToggled;

    public TrayIconManager(
        HotkeyManager hotkeyManager,
        ISettingsService? settingsService = null)
    {
        _hotkeyManager = hotkeyManager;
        _settingsService = settingsService;
        _applicationIcon = LoadApplicationIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = BuildTooltipText(_hotkeyManager.IsEnabled),
            Icon = _applicationIcon,
            Visible = true
        };

        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var action = _notificationAction;
            _notificationAction = null;
            action?.Invoke();
        };
    }

    internal static string BuildTooltipText(bool hotkeysEnabled) =>
        hotkeysEnabled ? "Word Catcher" : "Word Catcher（快捷键已暂停）";

    public void RefreshTooltip()
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(RefreshTooltip);
            return;
        }
        if (_disposed) return;
        _notifyIcon.Text = BuildTooltipText(_hotkeyManager.IsEnabled);
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            (ToggleLibraryRequested ?? OpenLibraryRequested)?.Invoke();
        }
        else if (e.Button == MouseButtons.Middle)
        {
            OcrCaptureRequested?.Invoke();
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(ShowContextMenu);
            return;
        }

        _activeMenuWindow?.SafeClose();

        var settings = _settingsService?.Current;
        var mouseEnabled = settings?.MouseSelection.Enabled ?? true;
        var hotkeyText = !string.IsNullOrWhiteSpace(settings?.Hotkey) ? settings.Hotkey : AppSettings.DefaultHotkey;
        var ocrHotkeyText = !string.IsNullOrWhiteSpace(settings?.OcrHotkey) ? settings.OcrHotkey : AppSettings.DefaultOcrHotkey;

        var snapshot = new TrayMenuSnapshot(
            mouseEnabled,
            _hotkeyManager.IsEnabled,
            hotkeyText,
            ocrHotkeyText);

        var actions = new TrayMenuActions(
            OpenLibrary: () => OpenLibraryRequested?.Invoke(),
            OpenLookup: () => OpenLookupRequested?.Invoke(),
            TriggerOcrCapture: () => OcrCaptureRequested?.Invoke(),
            ToggleMouseSelection: () =>
            {
                var current = _settingsService?.Current.MouseSelection.Enabled ?? true;
                MouseSelectionToggled?.Invoke(!current);
            },
            ToggleHotkeys: () =>
            {
                _hotkeyManager.SetEnabled(!_hotkeyManager.IsEnabled);
                RefreshTooltip();
            },
            OpenSettings: () => OpenSettingsRequested?.Invoke(),
            ExitApp: () =>
            {
                _notifyIcon.Visible = false;
                System.Windows.Application.Current?.Shutdown();
            });

        var menuWindow = new TrayContextMenuWindow(snapshot, actions);
        _activeMenuWindow = menuWindow;
        menuWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeMenuWindow, menuWindow))
                _activeMenuWindow = null;
        };
        menuWindow.ShowAtCursor();
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info, Action? onClick = null)
    {
        _notificationAction = onClick;
        _notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeMenuWindow?.SafeClose();
        _activeMenuWindow = null;
        _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(
                "pack://application:,,,/WordCatcher.App;component/Assets/WordCatcher.ico"));
            if (resource != null)
            {
                using var stream = resource.Stream;
                using var icon = new Icon(stream, SystemInformation.SmallIconSize);
                // 克隆后释放资源流，托盘直接使用对应 DPI 的小尺寸帧。
                return (Icon)icon.Clone();
            }
        }
        catch (Exception)
        {
            // 资源读取失败时继续尝试 exe 图标。
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon != null)
                {
                    return icon;
                }
            }
            catch (Exception)
            {
                // 读取 exe 图标失败时回退到系统默认应用图标。
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}

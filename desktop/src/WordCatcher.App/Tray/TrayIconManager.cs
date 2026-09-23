using System;
using System.Drawing;
using System.Windows.Forms;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;

namespace WordCatcher.App.Tray;

public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _applicationIcon;
    private readonly HotkeyManager _hotkeyManager;
    private readonly IAnkiSyncQueue _syncQueue;

    public event Action? OpenLibraryRequested;
    public event Action? OpenSettingsRequested;
    public event Action? CaptureNowRequested;

    public TrayIconManager(
        HotkeyManager hotkeyManager,
        IAnkiSyncQueue syncQueue)
    {
        _hotkeyManager = hotkeyManager;
        _syncQueue = syncQueue;
        _applicationIcon = LoadApplicationIcon();

        _notifyIcon = new NotifyIcon
        {
            Text = "Word Catcher",
            Icon = _applicationIcon,
            Visible = true
        };

        _notifyIcon.DoubleClick += (s, e) => OpenLibraryRequested?.Invoke();

        var menu = new ContextMenuStrip();

        var openLibItem = new ToolStripMenuItem("打开词库 (&L)");
        openLibItem.Click += (s, e) => OpenLibraryRequested?.Invoke();
        menu.Items.Add(openLibItem);

        var captureNowItem = new ToolStripMenuItem("立即取词 (&C)");
        captureNowItem.Click += (s, e) => CaptureNowRequested?.Invoke();
        menu.Items.Add(captureNowItem);

        var retrySyncItem = new ToolStripMenuItem("重试 Anki 待同步任务 (&R)");
        retrySyncItem.Click += (s, e) => _syncQueue.TriggerSync();
        menu.Items.Add(retrySyncItem);

        var toggleHotKeyItem = new ToolStripMenuItem("启用全局快捷键");
        toggleHotKeyItem.Checked = _hotkeyManager.IsEnabled;
        toggleHotKeyItem.Click += (s, e) =>
        {
            var newState = !_hotkeyManager.IsEnabled;
            _hotkeyManager.SetEnabled(newState);
            toggleHotKeyItem.Checked = newState;
        };
        menu.Items.Add(toggleHotKeyItem);

        menu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("设置 (&S)");
        settingsItem.Click += (s, e) => OpenSettingsRequested?.Invoke();
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出 (&X)");
        exitItem.Click += (s, e) =>
        {
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
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

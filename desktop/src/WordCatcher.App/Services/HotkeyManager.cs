using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using WordCatcher.App.Interop;

namespace WordCatcher.App.Services;

public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 9001;
    private readonly ILogger<HotkeyManager>? _logger;
    private HwndSource? _hwndSource;
    private bool _isRegistered;
    private bool _isEnabled = true;

    public event Action? HotkeyTriggered;

    public bool IsRegistered => _isRegistered;
    public bool IsEnabled => _isEnabled;

    public HotkeyManager(ILogger<HotkeyManager>? logger = null)
    {
        _logger = logger;
    }

    public bool Register(string hotkeyText = "Alt+Q")
    {
        if (_hwndSource == null)
        {
            var parameters = new HwndSourceParameters("WordCatcher_HotkeyReceiver")
            {
                WindowStyle = 0
            };
            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(HwndHook);
        }

        Unregister();

        ParseHotkey(hotkeyText, out var modifiers, out var vk);
        modifiers |= NativeMethods.MOD_NOREPEAT;

        var handle = _hwndSource.Handle;
        _isRegistered = NativeMethods.RegisterHotKey(handle, HotkeyId, modifiers, vk);

        if (!_isRegistered)
        {
            var err = Marshal.GetLastWin32Error();
            _logger?.LogWarning("Failed to register hotkey '{Hotkey}'. Win32 Error: {Error}", hotkeyText, err);
            return false;
        }

        _logger?.LogInformation("Global hotkey '{Hotkey}' registered successfully", hotkeyText);
        return true;
    }

    public void Unregister()
    {
        if (_isRegistered && _hwndSource != null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _isRegistered = false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            if (_isEnabled)
            {
                HotkeyTriggered?.Invoke();
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = NativeMethods.VK_C; // Default fallback

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            var upper = p.ToUpperInvariant();
            if (upper == "ALT") modifiers |= NativeMethods.MOD_ALT;
            else if (upper is "CTRL" or "CONTROL") modifiers |= NativeMethods.MOD_CONTROL;
            else if (upper == "SHIFT") modifiers |= NativeMethods.MOD_SHIFT;
            else if (upper == "WIN") modifiers |= NativeMethods.MOD_WIN;
            else if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z')
            {
                vk = upper[0];
            }
            else if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9')
            {
                vk = upper[0];
            }
        }

        if (vk == 0)
        {
            vk = 0x51; // 'Q'
        }
    }

    public void Dispose()
    {
        Unregister();
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(HwndHook);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}

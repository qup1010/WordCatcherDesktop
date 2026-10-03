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
    private int _registeredId = HotkeyId;
    private uint _registeredModifiers;
    private uint _registeredKey;

    public event Action? HotkeyTriggered;

    public bool IsRegistered => _isRegistered;
    public bool IsEnabled => _isEnabled;

    public HotkeyManager(ILogger<HotkeyManager>? logger = null)
    {
        _logger = logger;
    }

    public bool Register(string hotkeyText = "Alt+Q")
    {
        if (!TryParseHotkey(hotkeyText, out var modifiers, out var vk)) return false;
        if (_isRegistered && modifiers == _registeredModifiers && vk == _registeredKey) return true;
        if (_hwndSource == null)
        {
            var parameters = new HwndSourceParameters("WordCatcher_HotkeyReceiver")
            {
                WindowStyle = 0
            };
            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(HwndHook);
        }

        var handle = _hwndSource.Handle;
        var nextId = _registeredId == HotkeyId ? HotkeyId + 1 : HotkeyId;
        var registered = NativeMethods.RegisterHotKey(handle, nextId, modifiers | NativeMethods.MOD_NOREPEAT, vk);

        if (!registered)
        {
            var err = Marshal.GetLastWin32Error();
            _logger?.LogWarning("Failed to register hotkey '{Hotkey}'. Win32 Error: {Error}", hotkeyText, err);
            return false;
        }

        // Acquire the new combination first. A conflict must never disable the old one.
        Unregister();
        _registeredId = nextId;
        _registeredModifiers = modifiers;
        _registeredKey = vk;
        _isRegistered = true;

        _logger?.LogInformation("Global hotkey '{Hotkey}' registered successfully", hotkeyText);
        return true;
    }

    public void Unregister()
    {
        if (_isRegistered && _hwndSource != null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, _registeredId);
            _isRegistered = false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == _registeredId)
        {
            if (_isEnabled)
            {
                HotkeyTriggered?.Invoke();
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            if (upper == "ALT") modifiers |= NativeMethods.MOD_ALT;
            else if (upper is "CTRL" or "CONTROL") modifiers |= NativeMethods.MOD_CONTROL;
            else if (upper == "SHIFT") modifiers |= NativeMethods.MOD_SHIFT;
            else if (upper is "WIN" or "WINDOWS") modifiers |= NativeMethods.MOD_WIN;
            else
            {
                if (vk != 0) return false;
                if (upper.Length == 1 && (upper[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) vk = upper[0];
                else if (upper.StartsWith('F') && int.TryParse(upper.AsSpan(1), out var key) && key is >= 1 and <= 24) vk = (uint)(0x70 + key - 1);
                else return false;
            }
        }
        return vk != 0 && modifiers != 0;
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

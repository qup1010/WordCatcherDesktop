using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WordCatcher.App.Interop;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

public sealed class MouseSelectionMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint WmQuit = 0x0012;
    private const int ErrorInvalidThreadId = 1444;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int LeftDown = 0x0201;
    private const int LeftUp = 0x0202;
    private const int RightDown = 0x0204;
    private const int MouseWheel = 0x020A;
    private const int MouseHorizontalWheel = 0x020E;

    private readonly Channel<MonitorEvent> _events = Channel.CreateUnbounded<MonitorEvent>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly ISelectionCapturePlatform _platform;
    private readonly Func<MouseSelectionSettings?> _settings;
    private readonly Func<ScreenPoint, bool> _isOverFloat;
    private readonly Func<bool> _isFullScreen;
    private readonly HookProc _hookProc;
    private readonly HookProc _keyboardHookProc;
    private readonly object _lifetimeLock = new();
    private volatile Thread? _hookThread;
    private uint _hookThreadId;
    private bool _readerStarted;
    private volatile bool _captureEnabled;
    private volatile bool _dismissPillOnKey;
    private volatile bool _disposed;

    public MouseSelectionMonitor(ISettingsService settingsService, Func<ScreenPoint, bool>? isOverFloat = null)
        : this(new NativeSelectionCapturePlatform(), () => settingsService.Current.MouseSelection, IsFullScreen, isOverFloat)
    {
    }

    internal MouseSelectionMonitor(
        ISelectionCapturePlatform platform,
        Func<MouseSelectionSettings?> settings,
        Func<bool> isFullScreen,
        Func<ScreenPoint, bool>? isOverFloat = null)
    {
        _platform = platform;
        _settings = settings;
        _isFullScreen = isFullScreen;
        _isOverFloat = isOverFloat ?? (_ => false);
        _hookProc = OnMouseInput;
        _keyboardHookProc = OnKeyboardInput;
    }

    // Selection reading and delivery run on a background thread.
    public event Action<CaptureResult>? SelectionDetected;
    // Dismissal runs on the hook thread. Hand off UI work asynchronously.
    public event Action<ScreenPoint, bool>? DismissRequested;
    // Fired when Esc or a typing key is pressed while the selection pill is visible.
    public event Action? KeyboardDismissRequested;

    internal Thread? HookThread => _hookThread;

    public void UpdateHook(bool hasUnpinnedLookup, bool hasVisiblePill = false)
    {
        VerifyLifecycleThread();
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _dismissPillOnKey = hasVisiblePill;
            var captureEnabled = _settings()?.Enabled == true;
            if (_captureEnabled != captureEnabled)
            {
                _captureEnabled = captureEnabled;
                _events.Writer.TryWrite(new ResetGesture());
            }

            if (!captureEnabled && !hasUnpinnedLookup && !hasVisiblePill)
            {
                StopHook();
                return;
            }
            if (_hookThread is { IsAlive: true }) return;

            StartHook();
            if (!_readerStarted)
            {
                _readerStarted = true;
                _ = Task.Run(ProcessEventsAsync);
            }
        }
    }

    private void StartHook()
    {
        var ready = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunHook(ready))
        {
            IsBackground = true,
            Name = "WordCatcher mouse hook"
        };
        _hookThread = thread;
        try
        {
            thread.Start();
            _hookThreadId = ready.Task.GetAwaiter().GetResult();
        }
        catch
        {
            if (thread.IsAlive) thread.Join();
            _hookThread = null;
            _hookThreadId = 0;
            throw;
        }
    }

    private void RunHook(TaskCompletionSource<uint> ready)
    {
        IntPtr hook = IntPtr.Zero;
        IntPtr keyboardHook = IntPtr.Zero;
        try
        {
            // Create the thread message queue before publishing its ID to StopHook.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            var module = GetModuleHandle(null);
            hook = SetWindowsHookEx(WhMouseLl, _hookProc, module, 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, module, 0);
            if (keyboardHook == IntPtr.Zero)
                Trace.TraceWarning("Keyboard hook could not be installed (Win32 error {0}); pill keyboard dismiss unavailable.", Marshal.GetLastWin32Error());
            ready.SetResult(GetCurrentThreadId());

            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(in message);
                DispatchMessage(in message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            if (!ready.TrySetException(ex))
                Trace.TraceWarning("Mouse hook message pump failed: {0}", ex.Message);
        }
        finally
        {
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        }
    }

    private IntPtr OnKeyboardInput(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disposed && _dismissPillOnKey && (int)wParam is WmKeyDown or WmSysKeyDown)
        {
            var vkCode = Marshal.ReadInt32(lParam);
            if (ShouldDismissPillForVirtualKey(vkCode))
            {
                try { KeyboardDismissRequested?.Invoke(); }
                catch (Exception ex) { Trace.TraceWarning("Keyboard dismiss callback failed: {0}", ex.Message); }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    internal static bool ShouldDismissPillForVirtualKey(int vkCode) =>
        vkCode is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C);

    private IntPtr OnMouseInput(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !_disposed)
        {
            var message = (int)wParam;
            if (message is LeftDown or LeftUp or RightDown or MouseWheel or MouseHorizontalWheel)
            {
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                var position = new ScreenPoint(data.X, data.Y);
                try
                {
                    if (_captureEnabled)
                    {
                        var overFloat = message == LeftDown && _isOverFloat(position);
                        _events.Writer.TryWrite(new MouseEvent(message, position, data.Time, overFloat));
                    }
                    NotifyDismissal(message, position);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Mouse hook input failed: {0}", ex.Message);
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    internal void NotifyDismissal(int message, ScreenPoint position)
    {
        if (message is LeftDown or RightDown or MouseWheel or MouseHorizontalWheel)
            DismissRequested?.Invoke(position, message is LeftDown or RightDown);
    }

    private async Task ProcessEventsAsync()
    {
        MouseEvent? down = null;
        MouseEvent? previousClick = null;
        CancellationTokenSource? pending = null;
        var doubleClickTime = GetDoubleClickTime();
        try
        {
            await foreach (var input in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_disposed) break;
                try
                {
                    if (input is SelectionReady ready)
                    {
                        if (!ready.Token.IsCancellationRequested && CanCapture(ready.Source))
                            SelectionDetected?.Invoke(ready.Result);
                        continue;
                    }

                    if (input is ResetGesture)
                    {
                        pending?.Cancel();
                        pending?.Dispose();
                        pending = null;
                        down = previousClick = null;
                        continue;
                    }

                    var mouse = (MouseEvent)input;
                    if (mouse.Message != LeftUp)
                    {
                        pending?.Cancel();
                        pending?.Dispose();
                        pending = null;
                        if (mouse.Message == LeftDown && !mouse.OverFloat)
                        {
                            down = mouse;
                        }
                        else
                        {
                            down = null;
                            previousClick = null;
                        }
                        continue;
                    }

                    if (down is not { } press) continue;
                    down = null;
                    var drag = IsDragSelection(press.Position, mouse.Position);
                    var click = DistanceSquared(press.Position, mouse.Position) <= 36;
                    var doubleClick = click && previousClick is { } first &&
                        IsDoubleClickSelection(first.Position, press.Position, first.Time, press.Time, doubleClickTime);
                    previousClick = click && !doubleClick ? press : null;
                    if ((!drag && !doubleClick) || _settings()?.Enabled != true) continue;

                    var window = NativeMethods.GetForegroundWindow();
                    pending = new CancellationTokenSource();
                    _ = ReadAfterDelayAsync(mouse.Position, window, pending.Token);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Mouse selection event failed: {0}", ex.Message);
                }
            }
        }
        finally
        {
            pending?.Cancel();
            pending?.Dispose();
        }
    }

    private async Task ReadAfterDelayAsync(ScreenPoint cursor, IntPtr window, CancellationToken token)
    {
        try
        {
            await Task.Delay(80, token).ConfigureAwait(false);
            var source = _platform.GetSource();
            if (source.Window != window) return;
            var result = await ReadSelectionAsync(source, cursor, token).ConfigureAwait(false);
            if (result != null) _events.Writer.TryWrite(new SelectionReady(source, result, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            Trace.TraceWarning("Mouse selection read failed: {0}", ex.Message);
        }
    }

    internal async Task<CaptureResult?> ReadSelectionAsync(CaptureSource source, ScreenPoint cursor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanCapture(source)) return null;

        var selection = await _platform.ReadSelectionAsync(source with { Cursor = cursor }, null)
            .WaitAsync(TimeSpan.FromMilliseconds(750), token).ConfigureAwait(false);
        var text = selection?.Text.Trim();
        if (text is not { Length: >= 1 and <= 5000 } || !CanCapture(source)) return null;

        return new CaptureResult(text, source.Process, source.Title, cursor, DateTimeOffset.UtcNow,
            selection!.Sentence, selection.Offset);
    }

    private bool CanCapture(CaptureSource source) =>
        !_disposed && _settings() is { Enabled: true } settings
        && source.Window != IntPtr.Zero && source.ProcessId != 0 && source.ProcessId != Environment.ProcessId
        && !string.IsNullOrWhiteSpace(source.Process)
        && !IsProcessBlacklisted(source.Process, settings.BlacklistedProcesses)
        && !_isFullScreen() && _platform.IsCurrent(source);

    internal static bool IsDragSelection(ScreenPoint down, ScreenPoint up) => DistanceSquared(down, up) >= 144;

    internal static bool IsDoubleClickSelection(ScreenPoint first, ScreenPoint second, uint firstTime, uint secondTime, uint doubleClickTime) =>
        unchecked(secondTime - firstTime) <= doubleClickTime && DistanceSquared(first, second) <= 36;

    private static double DistanceSquared(ScreenPoint first, ScreenPoint second)
    {
        var dx = (double)first.X - second.X;
        var dy = (double)first.Y - second.Y;
        return dx * dx + dy * dy;
    }

    public static bool IsProcessBlacklisted(string? processName, IEnumerable<string>? blacklist)
    {
        var name = NormalizeProcessName(processName);
        return name.Length > 0 && blacklist?.Any(entry =>
            string.Equals(name, NormalizeProcessName(entry), StringComparison.OrdinalIgnoreCase)) == true;
    }

    internal static List<string> ParseBlacklistText(string? text) =>
        (text ?? "").Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeProcessName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static string NormalizeProcessName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4].TrimEnd() : trimmed;
    }

    private static bool IsFullScreen() => SHQueryUserNotificationState(out var state) == 0 &&
        state is NotificationState.Busy or NotificationState.ExclusiveDirect3D or NotificationState.Presentation;

    public void Dispose()
    {
        VerifyLifecycleThread();
        lock (_lifetimeLock)
        {
            if (_disposed) return;
            StopHook();
            _disposed = true;
            _events.Writer.TryComplete();
        }
    }

    private void StopHook()
    {
        if (_hookThread is not { } thread) return;
        if (thread.IsAlive && !PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInvalidThreadId) throw new Win32Exception(error);
        }
        thread.Join();
        _hookThread = null;
        _hookThreadId = 0;
    }

    private void VerifyLifecycleThread()
    {
        if (Thread.CurrentThread == _hookThread)
            throw new InvalidOperationException("Mouse hook lifecycle changes must run outside the hook callback.");
    }

    private abstract record MonitorEvent;
    private sealed record ResetGesture : MonitorEvent;
    private sealed record MouseEvent(int Message, ScreenPoint Position, uint Time, bool OverFloat) : MonitorEvent;
    private sealed record SelectionReady(CaptureSource Source, CaptureResult Result, CancellationToken Token) : MonitorEvent;

    private enum NotificationState { Busy = 2, ExclusiveDirect3D = 3, Presentation = 4 }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativeMethods.POINT Point;
        public uint Private;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(in NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessage(in NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out NotificationState state);
}

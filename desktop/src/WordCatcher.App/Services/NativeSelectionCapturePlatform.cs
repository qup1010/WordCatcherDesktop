using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using WordCatcher.App.Interop;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

internal sealed class NativeSelectionCapturePlatform : ISelectionCapturePlatform
{
    private static readonly SemaphoreSlim UiaLock = new(1, 1);
    public uint ClipboardSequence => NativeMethods.GetClipboardSequenceNumber();

    public CaptureSource GetSource()
    {
        var window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var title = new StringBuilder(512);
        NativeMethods.GetWindowText(window, title, title.Capacity);
        var process = "";
        try { using var target = Process.GetProcessById((int)processId); process = target.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException) { }
        NativeMethods.GetCursorPos(out var cursor);
        return new CaptureSource(window, processId, process, title.ToString(), new ScreenPoint(cursor.X, cursor.Y));
    }

    public bool IsCurrent(CaptureSource source)
    {
        if (source.Window == IntPtr.Zero || NativeMethods.GetForegroundWindow() != source.Window) return false;
        NativeMethods.GetWindowThreadProcessId(source.Window, out var processId);
        return processId == source.ProcessId;
    }

    public async Task WaitForModifiersAsync(CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (ModifiersHeld())
        {
            ct.ThrowIfCancellationRequested();
            if (timer.ElapsedMilliseconds >= 500) throw new CaptureException(CaptureFailure.ModifiersHeld);
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static bool ModifiersHeld() => new[] {
        NativeMethods.VK_MENU, NativeMethods.VK_CONTROL, NativeMethods.VK_SHIFT,
        NativeMethods.VK_LWIN, NativeMethods.VK_RWIN
    }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);

    public Task<IClipboardBackup> BackupClipboardAsync()
        => ExecuteInStaAsync<IClipboardBackup>(NativeClipboardBackup.Capture);
    public Task<ClipboardRead> ReadCopiedTextAsync(CaptureSource source, uint sequence)
        => ExecuteInStaAsync(() => NativeClipboardBackup.Read(source, sequence));
    public Task<bool> RestoreClipboardAsync(IClipboardBackup backup, uint sequence)
        => ExecuteInStaAsync(() => ((NativeClipboardBackup)backup).Restore(sequence));

    public async Task<SelectionSnapshot?> ReadSelectionAsync(CaptureSource source, string? expectedText = null)
    {
        // 慢/挂起的外部 provider 最多保留一个后台调用，不为每次热键新建挂起线程。
        if (!await UiaLock.WaitAsync(0).ConfigureAwait(false)) return null;
        try { return await ExecuteInStaAsync(() => ReadSelection(source, expectedText)).ConfigureAwait(false); }
        finally { UiaLock.Release(); }
    }

    private SelectionSnapshot? ReadSelection(CaptureSource source, string? expectedText)
    {
        try
        {
            if (!IsCurrent(source)) return null;
            var window = AutomationElement.FromHandle(source.Window);
            var candidates = new List<AutomationElement>();
            var focused = AutomationElement.FocusedElement;
            bool belongsToSource = false;
            for (var element = focused; element != null && candidates.Count < 64;
                 element = TreeWalker.ControlViewWalker.GetParent(element))
            {
                candidates.Add(element);
                if (Automation.Compare(element, window)) { belongsToSource = true; break; }
            }
            if (!belongsToSource) return null;
            foreach (var element in candidates)
            {
                if (!IsCurrent(source)) return null;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var value) || value is not TextPattern pattern)
                    continue;
                var ranges = pattern.GetSelection();
                // 不合并离散选区，避免把多个不相邻位置当作同一句语境。
                if (ranges == null || ranges.Length != 1) continue;
                var selectedRange = ranges[0].Clone();
                var raw = selectedRange.GetText(5001);
                var text = raw.Trim();
                if (text.Length == 0) continue;
                if (expectedText != null && !string.Equals(NormalizeWhitespace(text),
                    NormalizeWhitespace(expectedText), StringComparison.OrdinalIgnoreCase)) continue;
                if (raw.Length > 5000) throw new CaptureException(CaptureFailure.SelectionTooLong);
                var selectedOnly = new SelectionSnapshot(text, text, 0);
                try
                {
                    var paragraphRange = selectedRange.Clone();
                    paragraphRange.ExpandToEnclosingUnit(TextUnit.Paragraph);
                    var paragraph = paragraphRange.GetText(5001);
                    if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length > 5000) return selectedOnly;
                    var prefixRange = paragraphRange.Clone();
                    prefixRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, selectedRange, TextPatternRangeEndpoint.Start);
                    var prefix = prefixRange.GetText(5001);
                    var offset = prefix.Length + raw.Length - raw.TrimStart().Length;
                    if (offset + text.Length > paragraph.Length ||
                        !string.Equals(paragraph.Substring(offset, text.Length), text, StringComparison.Ordinal))
                        return selectedOnly;
                    return ExtractSentence(paragraph, offset, text.Length);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ElementNotAvailableException)
                { return selectedOnly; }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ElementNotAvailableException
            or UnauthorizedAccessException or NotSupportedException)
        { }
        return null;
    }

    public void SendCopy()
    {
        var inputs = new NativeMethods.INPUT[4];

        // Ctrl down
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].data.ki.wVk = NativeMethods.VK_CONTROL;

        // C down
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].data.ki.wVk = NativeMethods.VK_C;

        // C up
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].data.ki.wVk = NativeMethods.VK_C;
        inputs[2].data.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        // Ctrl up
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].data.ki.wVk = NativeMethods.VK_CONTROL;
        inputs[3].data.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.INPUT>());

        if (sent != (uint)inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            // 部分注入时尽力释放模拟按键，避免留下 Ctrl 按下状态。
            var releases = new[] { inputs[2], inputs[3] };
            NativeMethods.SendInput(2, releases, Marshal.SizeOf<NativeMethods.INPUT>());
            throw new CaptureException(CaptureFailure.InputBlocked,
                new InvalidOperationException($"SendInput returned {sent}/{inputs.Length}, error {error}."));
        }
    }


    private static Task<T> ExecuteInStaAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    internal static SelectionSnapshot ExtractSentence(string paragraph, int selectionOffset, int selectionLength)
    {
        var from = selectionOffset;
        while (from > 0 && !IsSentenceBoundary(paragraph[from - 1]))
            from--;

        var to = selectionOffset + selectionLength;
        while (to < paragraph.Length && !IsSentenceBoundary(paragraph[to]))
            to++;
        if (to < paragraph.Length)
            to++;

        var sentence = NormalizeWhitespace(paragraph[from..to]).Trim();
        var prefix = NormalizeWhitespace(paragraph[from..selectionOffset]);
        var offset = prefix.Length;
        if (offset > 0
            && selectionOffset > from
            && char.IsWhiteSpace(paragraph[selectionOffset - 1])
            && offset < sentence.Length)
        {
            offset++;
        }
        return new SelectionSnapshot(paragraph.Substring(selectionOffset, selectionLength).Trim(), sentence, offset);
    }

    private static bool IsSentenceBoundary(char value)
        => value is '.' or '!' or '?' or '。' or '！' or '？' or '；' or ';';


}

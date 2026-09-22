using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using WordCatcher.App.Interop;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

public sealed class SelectionCaptureService : ISelectionCaptureService
{
    private readonly ILogger<SelectionCaptureService>? _logger;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public SelectionCaptureService(ILogger<SelectionCaptureService>? logger = null)
    {
        _logger = logger;
    }

    public async Task<CaptureResult?> CaptureSelectionAsync(CancellationToken ct = default)
    {
        if (!await _captureLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger?.LogWarning("Selection capture already in progress, ignoring duplicate request");
            return null;
        }

        ClipboardSnapshot? snapshot = null;
        try
        {
            // 1. Wait for physical keys release (Alt, Ctrl, Shift, Win)
            await WaitForModifiersReleaseAsync(ct).ConfigureAwait(false);

            // 2. Snapshot metadata BEFORE simulating Ctrl+C
            var fgHwnd = NativeMethods.GetForegroundWindow();
            var titleSb = new StringBuilder(512);
            NativeMethods.GetWindowText(fgHwnd, titleSb, titleSb.Capacity);
            var sourceWindowTitle = titleSb.ToString();

            NativeMethods.GetWindowThreadProcessId(fgHwnd, out var procId);
            var sourceProcess = string.Empty;
            if (procId != 0)
            {
                try
                {
                    sourceProcess = Process.GetProcessById((int)procId).ProcessName;
                }
                catch
                {
                    // ignore
                }
            }

            NativeMethods.GetCursorPos(out var cursorPos);
            var point = new ScreenPoint(cursorPos.X, cursorPos.Y);
            var initialSeq = NativeMethods.GetClipboardSequenceNumber();

            // 3. Backup current clipboard in STA thread
            snapshot = await ExecuteInStaAsync(CaptureClipboardSnapshot).ConfigureAwait(false);

            // 4. Send Ctrl+C via Win32 SendInput
            SendCtrlC();

            // 5. Poll clipboard sequence number change
            var sw = Stopwatch.StartNew();
            bool seqChanged = false;
            while (sw.ElapsedMilliseconds < 1000 && !ct.IsCancellationRequested)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
                if (NativeMethods.GetClipboardSequenceNumber() != initialSeq)
                {
                    seqChanged = true;
                    break;
                }
            }

            if (!seqChanged)
            {
                _logger?.LogInformation("Clipboard sequence number did not change within timeout");
                return null;
            }

            // 6. Read text from clipboard in STA thread
            var rawText = await ExecuteInStaAsync(ReadClipboardText).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return null;
            }

            var text = rawText.Trim();
            if (text.Length > 5000)
            {
                _logger?.LogWarning("Selected text exceeds 5000 characters limit, ignoring");
                return null;
            }

            var context = TryCaptureSentence(fgHwnd, text);

            return new CaptureResult(
                SelectedText: text,
                SourceProcess: sourceProcess,
                SourceWindowTitle: sourceWindowTitle,
                CursorPosition: point,
                CapturedAt: DateTimeOffset.UtcNow,
                Sentence: context?.Sentence ?? text,
                SentenceOffset: context?.Offset ?? 0);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during selection capture");
            return null;
        }
        finally
        {
            // 7. Best-effort restore of clipboard snapshot
            if (snapshot != null)
            {
                try
                {
                    await ExecuteInStaAsync(() => RestoreClipboardSnapshot(snapshot)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Clipboard ownership can be taken by another process between
                    // capture and restore. Restoration must never crash the async
                    // void hotkey handler or terminate the desktop app.
                    _logger?.LogWarning(ex, "Failed to restore the previous clipboard contents");
                }
            }
            _captureLock.Release();
        }
    }

    private static async Task WaitForModifiersReleaseAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500 && !ct.IsCancellationRequested)
        {
            bool alt = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
            bool ctrl = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
            bool shift = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            bool win = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0
                    || (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

            if (!alt && !ctrl && !shift && !win)
            {
                break;
            }
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static void SendCtrlC()
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
            throw new InvalidOperationException(
                $"发送 Ctrl+C 失败：仅注入 {sent}/{inputs.Length} 个按键事件，Win32 错误码 {error}。");
        }
    }

    private static Task<T> ExecuteInStaAsync<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return Task.FromResult(action());
        }

        var tcs = new TaskCompletionSource<T>();
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

    private static Task ExecuteInStaAsync(Action action)
    {
        return ExecuteInStaAsync(() =>
        {
            action();
            return true;
        });
    }

    private static string? ReadClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    return Clipboard.GetText(TextDataFormat.UnicodeText);
                }
                if (Clipboard.ContainsText(TextDataFormat.Text))
                {
                    return Clipboard.GetText(TextDataFormat.Text);
                }
                return null;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }
        return null;
    }

    /// <summary>
    /// Best-effort context capture through Windows UI Automation. Many native
    /// editors and Office controls expose TextPattern; applications that do not
    /// expose it still use the clipboard selection as a safe fallback.
    /// </summary>
    private static ContextSnapshot? TryCaptureSentence(IntPtr sourceHwnd, string selectedText)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            var root = focused ?? (sourceHwnd != IntPtr.Zero
                ? AutomationElement.FromHandle(sourceHwnd)
                : null);

            for (var element = root; element != null; element = TreeWalker.ControlViewWalker.GetParent(element))
            {
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject)
                    || patternObject is not TextPattern textPattern)
                {
                    continue;
                }

                var selection = textPattern.GetSelection();
                if (selection == null || selection.Length == 0)
                    continue;

                var paragraphRange = selection[0].Clone();
                paragraphRange.ExpandToEnclosingUnit(TextUnit.Paragraph);
                var paragraph = NormalizeWhitespace(paragraphRange.GetText(5000));
                if (string.IsNullOrWhiteSpace(paragraph))
                    continue;

                var selected = NormalizeWhitespace(selectedText);
                var selectedOffset = paragraph.IndexOf(selected, StringComparison.OrdinalIgnoreCase);
                if (selectedOffset >= 0)
                {
                    return ExtractSentence(paragraph, selectedOffset, selected.Length);
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            // The target window can disappear while the user switches apps.
        }
        catch (COMException)
        {
            // UI Automation is optional; clipboard capture remains reliable.
        }
        catch (InvalidOperationException)
        {
            // Some providers reject TextPattern calls from a different process.
        }

        return null;
    }

    private static string NormalizeWhitespace(string value)
    {
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static ContextSnapshot ExtractSentence(string paragraph, int selectionOffset, int selectionLength)
    {
        var from = selectionOffset;
        while (from > 0 && !IsSentenceBoundary(paragraph[from - 1]))
            from--;

        var to = selectionOffset + selectionLength;
        while (to < paragraph.Length && !IsSentenceBoundary(paragraph[to]))
            to++;
        if (to < paragraph.Length)
            to++;

        var sentence = paragraph[from..to].Trim();
        var offset = Math.Max(0, selectionOffset - from);
        return new ContextSnapshot(sentence, offset);
    }

    private static bool IsSentenceBoundary(char value)
        => value is '.' or '!' or '?' or '。' or '！' or '？' or '；' or ';';

    private static ClipboardSnapshot CaptureClipboardSnapshot()
    {
        var snap = new ClipboardSnapshot();
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return snap;

                if (data.GetDataPresent(DataFormats.UnicodeText))
                    snap.UnicodeText = data.GetData(DataFormats.UnicodeText) as string;

                if (data.GetDataPresent(DataFormats.Html))
                    snap.Html = data.GetData(DataFormats.Html) as string;

                if (data.GetDataPresent(DataFormats.Rtf))
                    snap.Rtf = data.GetData(DataFormats.Rtf) as string;

                if (Clipboard.ContainsFileDropList())
                    snap.FileDropList = Clipboard.GetFileDropList();

                if (Clipboard.ContainsImage())
                {
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        // Clone while still on the clipboard STA, then freeze so
                        // the snapshot can safely cross to the restore STA.
                        var detachedImage = image.Clone();
                        if (detachedImage.CanFreeze)
                        {
                            detachedImage.Freeze();
                        }
                        image = detachedImage;
                    }
                    snap.Image = image;
                }

                break;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }
        return snap;
    }

    private static void RestoreClipboardSnapshot(ClipboardSnapshot snapshot)
    {
        if (snapshot.IsEmpty) return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                var dataObj = new DataObject();
                bool hasContent = false;

                if (!string.IsNullOrEmpty(snapshot.UnicodeText))
                {
                    dataObj.SetData(DataFormats.UnicodeText, snapshot.UnicodeText);
                    dataObj.SetData(DataFormats.Text, snapshot.UnicodeText);
                    hasContent = true;
                }
                if (!string.IsNullOrEmpty(snapshot.Html))
                {
                    dataObj.SetData(DataFormats.Html, snapshot.Html);
                    hasContent = true;
                }
                if (!string.IsNullOrEmpty(snapshot.Rtf))
                {
                    dataObj.SetData(DataFormats.Rtf, snapshot.Rtf);
                    hasContent = true;
                }
                if (snapshot.FileDropList != null && snapshot.FileDropList.Count > 0)
                {
                    dataObj.SetFileDropList(snapshot.FileDropList);
                    hasContent = true;
                }
                if (snapshot.Image != null)
                {
                    dataObj.SetImage(snapshot.Image);
                    hasContent = true;
                }

                if (hasContent)
                {
                    Clipboard.SetDataObject(dataObj, copy: true);
                }
                break;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
    }

    private sealed class ClipboardSnapshot
    {
        public string? UnicodeText { get; set; }
        public string? Html { get; set; }
        public string? Rtf { get; set; }
        public StringCollection? FileDropList { get; set; }
        public BitmapSource? Image { get; set; }

        public bool IsEmpty =>
            string.IsNullOrEmpty(UnicodeText)
            && string.IsNullOrEmpty(Html)
            && string.IsNullOrEmpty(Rtf)
            && (FileDropList == null || FileDropList.Count == 0)
            && Image == null;
    }

    private sealed record ContextSnapshot(string Sentence, int Offset);
}

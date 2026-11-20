using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

public sealed class SelectionCaptureService : ISelectionCaptureService
{
    private readonly ILogger<SelectionCaptureService>? _logger;
    private readonly ISelectionCapturePlatform _platform;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public SelectionCaptureService(ILogger<SelectionCaptureService>? logger = null)
        : this(new NativeSelectionCapturePlatform(), logger) { }

    internal SelectionCaptureService(ISelectionCapturePlatform platform, ILogger<SelectionCaptureService>? logger = null)
    {
        _platform = platform;
        _logger = logger;
    }

    public async Task<CaptureResult?> CaptureSelectionAsync(CancellationToken ct = default)
    {
        if (!await _captureLock.WaitAsync(0, ct).ConfigureAwait(false)) return null;
        IClipboardBackup? backup = null;
        CaptureSource? source = null;
        uint? copiedSequence = null;
        var copySent = false;
        try
        {
            // 快捷键松开之前也记录来源，避免等待期间切换应用后取错窗口。
            source = _platform.GetSource();
            EnsureSource(source);
            await _platform.WaitForModifiersAsync(ct).ConfigureAwait(false);
            EnsureSource(source);
            var selection = await ReadSelectionAsync(source, null, ct).ConfigureAwait(false);
            EnsureSource(source);
            if (selection != null) return BuildResult(source, selection);

            backup = await _platform.BackupClipboardAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            EnsureSource(source);
            if (_platform.ClipboardSequence != backup.Sequence)
                throw new CaptureException(CaptureFailure.ClipboardChanged);
            _platform.SendCopy();
            copySent = true;

            // 有些程序会在失焦时停止后续读取；先捕获 SendInput 返回后的即时序号。
            var sequenceAfterSend = _platform.ClipboardSequence;
            if (sequenceAfterSend != backup.Sequence) copiedSequence = sequenceAfterSend;

            var timer = Stopwatch.StartNew();
            var observedSequence = _platform.ClipboardSequence;
            while (observedSequence == backup.Sequence)
            {
                EnsureSource(source);
                ct.ThrowIfCancellationRequested();
                if (timer.ElapsedMilliseconds >= 3000)
                    throw new CaptureException(CaptureFailure.CopyTimeout);
                await Task.Delay(25, ct).ConfigureAwait(false);
                observedSequence = _platform.ClipboardSequence;
            }
            if (copiedSequence.HasValue && copiedSequence.Value != observedSequence)
                throw new CaptureException(CaptureFailure.ClipboardChanged);
            // 先记下序号，再做焦点校验或读取；避免之后抛错时 finally 不知道本次写入的序号。
            copiedSequence = observedSequence;

            // 序号变化仅表示有人复制，还需确认来源和剪贴板所有者。
            EnsureSource(source);
            var read = await _platform.ReadCopiedTextAsync(source, observedSequence).ConfigureAwait(false);
            if (_platform.ClipboardSequence != read.Sequence)
                throw new CaptureException(CaptureFailure.ClipboardChanged);
            copiedSequence = read.Sequence;
            if (read.Failure.HasValue) throw new CaptureException(read.Failure.Value);
            ct.ThrowIfCancellationRequested();
            EnsureSource(source);
            if (string.IsNullOrWhiteSpace(read.Text)) throw new CaptureException(CaptureFailure.NoSelection);
            var text = read.Text.Trim();
            ValidateText(text);
            selection = await ReadSelectionAsync(source, text, ct).ConfigureAwait(false);
            EnsureSource(source);
            if (_platform.ClipboardSequence != copiedSequence)
                throw new CaptureException(CaptureFailure.ClipboardChanged);
            return BuildResult(source, selection ?? new SelectionSnapshot(text, text, 0));
        }
        catch (OperationCanceledException) { throw; }
        catch (CaptureException ex)
        {
            _logger?.LogInformation("Selection capture stopped: {Failure}", ex.Failure);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during selection capture");
            throw new CaptureException(CaptureFailure.Unexpected, ex);
        }
        finally
        {
            try
            {
                // 独立重查序号和 owner。只有明确观察到本次复制序号（copiedSequence）时才恢复；
                // 若注入后未观察到写入（例如焦点切换/取消），绝不推测恢复同进程的新复制。
                if (backup != null && source != null && copySent && copiedSequence.HasValue)
                {
                    var currentSequence = _platform.ClipboardSequence;
                    var expectedSequence = copiedSequence.Value;
                    if (currentSequence != backup.Sequence && currentSequence == expectedSequence)
                    {
                        var ownership = await _platform.ReadCopiedTextAsync(source, currentSequence).ConfigureAwait(false);
                        if (ownership.Failure != CaptureFailure.ClipboardChanged
                            && _platform.ClipboardSequence == ownership.Sequence)
                        {
                            var restored = await _platform.RestoreClipboardAsync(backup, ownership.Sequence).ConfigureAwait(false);
                            LogRestoreResult(backup, restored);
                        }
                        else
                            _logger?.LogInformation("Clipboard restore skipped: ownership or sequence check failed (expected {ExpectedSequence}, observed {ObservedSequence})",
                                currentSequence, ownership.Sequence);
                    }
                    else if (currentSequence != backup.Sequence)
                        _logger?.LogInformation("Clipboard restore skipped: clipboard sequence changed after our copy (expected {ExpectedSequence}, observed {ObservedSequence})",
                            expectedSequence, currentSequence);
                }
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Could not restore clipboard backup"); }
            finally
            {
                try { backup?.Dispose(); }
                finally { _captureLock.Release(); }
            }
        }
    }

    private void LogRestoreResult(IClipboardBackup backup, bool restored)
    {
        if (backup is NativeClipboardBackup native && native.LastRestoreResult is { } result)
        {
            if (result.Restored)
                _logger?.LogDebug("Clipboard backup restore completed: {Status}", result.Status);
            else
                _logger?.LogInformation("Clipboard backup restore skipped: {Status}. {Failure}", result.Status, result.Failure);
            return;
        }
        _logger?.LogDebug("Clipboard backup restore {Status}", restored ? "completed" : "skipped");
    }

    private async Task<SelectionSnapshot?> ReadSelectionAsync(CaptureSource source, string? text, CancellationToken ct)
    {
        try
        {
            return await _platform.ReadSelectionAsync(source, text).WaitAsync(TimeSpan.FromMilliseconds(750), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return null; }
    }

    private void EnsureSource(CaptureSource source)
    {
        if (!_platform.IsCurrent(source)) throw new CaptureException(CaptureFailure.SourceChanged);
    }

    private static void ValidateText(string text)
    {
        if (text.Length > 5000) throw new CaptureException(CaptureFailure.SelectionTooLong);
        if (string.IsNullOrWhiteSpace(text)) throw new CaptureException(CaptureFailure.NoSelection);
    }

    private static CaptureResult BuildResult(CaptureSource source, SelectionSnapshot selection)
    {
        var text = selection.Text.Trim();
        ValidateText(text);
        return new CaptureResult(text, source.Process, source.Title, source.Cursor, DateTimeOffset.UtcNow,
            selection.Sentence, selection.Offset);
    }
}

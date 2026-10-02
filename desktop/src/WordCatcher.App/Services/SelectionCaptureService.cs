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
        uint? copiedSequence = null;
        try
        {
            // 快捷键松开之前也记录来源，避免等待期间切换应用后取错窗口。
            var source = _platform.GetSource();
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

            var timer = Stopwatch.StartNew();
            while (_platform.ClipboardSequence == backup.Sequence)
            {
                EnsureSource(source);
                ct.ThrowIfCancellationRequested();
                if (timer.ElapsedMilliseconds >= 1000)
                    throw new CaptureException(CaptureFailure.CopyTimeout);
                await Task.Delay(25, ct).ConfigureAwait(false);
            }

            // 序号变化仅表示有人复制，还需确认来源和剪贴板所有者。
            EnsureSource(source);
            var read = await _platform.ReadCopiedTextAsync(source, _platform.ClipboardSequence).ConfigureAwait(false);
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
                // 只恢复确认为来源复制的数据；恢复内部锁住剪贴板后再次比较序号。
                if (backup != null && copiedSequence.HasValue)
                    await _platform.RestoreClipboardAsync(backup, copiedSequence.Value).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Could not restore clipboard backup"); }
            finally
            {
                try { backup?.Dispose(); }
                finally { _captureLock.Release(); }
            }
        }
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

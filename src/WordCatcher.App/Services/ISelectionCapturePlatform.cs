using WordCatcher.Core.Models;
namespace WordCatcher.App.Services;

internal sealed record CaptureSource(IntPtr Window, uint ProcessId, string Process, string Title, ScreenPoint Cursor);
internal sealed record SelectionSnapshot(string Text, string Sentence, int Offset);
internal sealed record ClipboardRead(string? Text, uint Sequence, CaptureFailure? Failure = null);
internal interface IClipboardBackup : IDisposable { uint Sequence { get; } }
internal interface ISelectionCapturePlatform
{
    CaptureSource GetSource();
    bool IsCurrent(CaptureSource source);
    uint ClipboardSequence { get; }
    Task WaitForModifiersAsync(CancellationToken ct);
    Task<SelectionSnapshot?> ReadSelectionAsync(CaptureSource source, string? expectedText = null);
    Task<IClipboardBackup> BackupClipboardAsync();
    void SendCopy();
    Task<ClipboardRead> ReadCopiedTextAsync(CaptureSource source, uint expectedSequence);
    Task<bool> RestoreClipboardAsync(IClipboardBackup backup, uint expectedSequence);
}

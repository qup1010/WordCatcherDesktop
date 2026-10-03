using WordCatcher.App.Services;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tests;

public class SelectionCaptureTests
{
    [Fact]
    public async Task UiaSelectionNeverTouchesClipboard()
    {
        var platform = new FakePlatform { Uia = new("ocean", "The ocean is blue.", 4) };
        var result = await new SelectionCaptureService(platform).CaptureSelectionAsync();
        Assert.Equal("The ocean is blue.", result!.Sentence);
        Assert.Equal(4, result.SentenceOffset);
        Assert.Equal(0, platform.Backups);
        Assert.Equal(0, platform.Copies);
        Assert.Equal("original", platform.Clipboard);
    }

    [Theory]
    [InlineData("original")]
    [InlineData(null)]
    public async Task ClipboardFallbackRestoresTextAndEmptyClipboard(string? original)
    {
        var platform = new FakePlatform { Clipboard = original };
        var result = await new SelectionCaptureService(platform).CaptureSelectionAsync();
        Assert.Equal("ocean", result!.SelectedText);
        Assert.Equal(original, platform.Clipboard);
        Assert.True(platform.Backup!.Disposed);
        Assert.Equal(1, platform.Restores);
    }

    [Fact]
    public async Task NewCopyDuringContextReadIsPreserved()
    {
        var platform = new FakePlatform();
        platform.OnContext = () => { platform.Clipboard = "user's new copy"; platform.Sequence++; };
        var failure = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ClipboardChanged, failure.Failure);
        Assert.Equal("user's new copy", platform.Clipboard);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task NewCopyAtRestoreBoundaryIsPreserved()
    {
        var platform = new FakePlatform();
        platform.OnRestore = () => { platform.Clipboard = "last-moment copy"; platform.Sequence++; };
        Assert.NotNull(await new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal("last-moment copy", platform.Clipboard);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task ClipboardChangeBeforeInjectionCancelsWithoutSendingCopy()
    {
        var platform = new FakePlatform();
        platform.OnBackup = () => { platform.Clipboard = "new copy"; platform.Sequence++; };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ClipboardChanged, error.Failure);
        Assert.Equal(0, platform.Copies);
        Assert.Equal("new copy", platform.Clipboard);
    }

    [Fact]
    public async Task UnrelatedClipboardOwnerIsNotAcceptedOrRestored()
    {
        var platform = new FakePlatform { ReadError = CaptureFailure.ClipboardChanged };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ClipboardChanged, error.Failure);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task SwitchingWindowWhileReleasingKeysCancelsBeforeUiaOrCopy()
    {
        var platform = new FakePlatform();
        platform.OnModifiers = () => platform.Current = false;
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.SourceChanged, error.Failure);
        Assert.Equal(0, platform.Backups);
        Assert.Equal(0, platform.Copies);
    }

    [Fact]
    public async Task SwitchingWindowDuringContextReadRestoresOnlyOurCopy()
    {
        var platform = new FakePlatform();
        platform.OnContext = () => platform.Current = false;
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.SourceChanged, error.Failure);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task CancellationAfterCopyStillRestoresAndReleasesCaptureLock()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform { OnRead = cancellation.Cancel };
        var service = new SelectionCaptureService(platform);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CaptureSelectionAsync(cancellation.Token));
        Assert.Equal("original", platform.Clipboard);
        platform.OnRead = null;
        Assert.NotNull(await service.CaptureSelectionAsync());
    }

    [Theory]
    [InlineData(CaptureFailure.ClipboardBusy)]
    [InlineData(CaptureFailure.UnsupportedClipboard)]
    public async Task BackupFailureDoesNotOverwriteClipboard(CaptureFailure failure)
    {
        var platform = new FakePlatform { BackupError = failure };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(failure, error.Failure);
        Assert.Equal(0, platform.Copies);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task InputFailureIsReportedAndLockCanBeUsedAgain()
    {
        var platform = new FakePlatform { CopyError = CaptureFailure.InputBlocked };
        var service = new SelectionCaptureService(platform);
        var error = await Assert.ThrowsAsync<CaptureException>(() => service.CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.InputBlocked, error.Failure);
        Assert.Equal("original", platform.Clipboard);
        platform.CopyError = null;
        Assert.NotNull(await service.CaptureSelectionAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyCopyReportsNoSelectionAndRestores(string copied)
    {
        var platform = new FakePlatform { CopiedText = copied };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.NoSelection, error.Failure);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task TooLongCopiedSelectionRestoresClipboard()
    {
        var platform = new FakePlatform { CopiedText = new string('a', 5001) };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.SelectionTooLong, error.Failure);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task TimeoutDoesNotRestoreUnchangedClipboard()
    {
        var platform = new FakePlatform { UpdateOnCopy = false };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.CopyTimeout, error.Failure);
        Assert.Equal(0, platform.Restores);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task UiaTimeoutFallsBackToCopy()
    {
        var platform = new FakePlatform { UiaGate = new TaskCompletionSource<SelectionSnapshot?>().Task };
        Assert.NotNull(await new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(1, platform.Copies);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task TooLongUiaSelectionDoesNotTouchClipboard()
    {
        var platform = new FakePlatform { Uia = new(new string('a', 5001), "", 0) };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.SelectionTooLong, error.Failure);
        Assert.Equal(0, platform.Copies);
        Assert.Equal(0, platform.Backups);
    }

    [Fact]
    public async Task TrustedCopyReadFailureRestoresOriginal()
    {
        var platform = new FakePlatform { TrustedReadFailure = CaptureFailure.ClipboardBusy };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ClipboardBusy, error.Failure);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task HeldModifiersAreReportedWithoutCopying()
    {
        var platform = new FakePlatform { ModifierFailure = CaptureFailure.ModifiersHeld };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ModifiersHeld, error.Failure);
        Assert.Equal(0, platform.Copies);
    }

    [Fact]
    public async Task DuplicateCaptureIsIgnored()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakePlatform { ModifierGate = gate.Task };
        var service = new SelectionCaptureService(platform);
        var first = service.CaptureSelectionAsync();
        Assert.Null(await service.CaptureSelectionAsync());
        gate.SetResult();
        Assert.NotNull(await first);
        Assert.Equal(1, platform.Copies);
    }

    [Fact]
    public void RepeatedWordUsesActualSelectionOffset()
    {
        const string paragraph = "An ocean and another ocean are different. Next sentence.";
        var offset = paragraph.IndexOf("ocean", paragraph.IndexOf("ocean", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
        var result = NativeSelectionCapturePlatform.ExtractSentence(paragraph, offset, 5);
        Assert.Equal("An ocean and another ocean are different.", result.Sentence);
        Assert.Equal(offset, result.Offset);
        Assert.Equal("ocean", result.Sentence.Substring(result.Offset, 5));
    }

    private sealed class Backup(uint sequence, string? text) : IClipboardBackup
    {
        public uint Sequence { get; } = sequence;
        public string? Text { get; } = text;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakePlatform : ISelectionCapturePlatform
    {
        public uint Sequence = 10;
        public uint ClipboardSequence => Sequence;
        public string? Clipboard = "original";
        public string CopiedText = "ocean";
        public SelectionSnapshot? Uia;
        public bool Current = true, UpdateOnCopy = true;
        public int Copies, Backups, Restores;
        public Backup? Backup;
        public CaptureFailure? BackupError, CopyError, ReadError, TrustedReadFailure, ModifierFailure;
        public Action? OnModifiers, OnBackup, OnContext, OnRestore, OnRead;
        public Task? ModifierGate;
        public Task<SelectionSnapshot?>? UiaGate;
        public CaptureSource GetSource() => new(new IntPtr(123), 456, "test", "Test window", new(10, 20));
        public bool IsCurrent(CaptureSource source) => Current;
        public async Task WaitForModifiersAsync(CancellationToken ct)
        {
            if (ModifierFailure.HasValue) throw new CaptureException(ModifierFailure.Value);
            if (ModifierGate != null) await ModifierGate.WaitAsync(ct);
            OnModifiers?.Invoke();
        }
        public Task<SelectionSnapshot?> ReadSelectionAsync(CaptureSource source, string? expectedText = null)
        {
            if (expectedText != null) OnContext?.Invoke();
            if (expectedText == null && UiaGate != null) return UiaGate;
            return Task.FromResult(Uia);
        }
        public Task<IClipboardBackup> BackupClipboardAsync()
        {
            if (BackupError.HasValue) throw new CaptureException(BackupError.Value);
            Backups++;
            Backup = new Backup(Sequence, Clipboard);
            OnBackup?.Invoke();
            return Task.FromResult<IClipboardBackup>(Backup);
        }
        public void SendCopy()
        {
            if (CopyError.HasValue) throw new CaptureException(CopyError.Value);
            Copies++;
            if (UpdateOnCopy) { Sequence++; Clipboard = CopiedText; }
        }
        public Task<ClipboardRead> ReadCopiedTextAsync(CaptureSource source, uint expectedSequence)
        {
            if (ReadError.HasValue) throw new CaptureException(ReadError.Value);
            OnRead?.Invoke();
            return Task.FromResult(new ClipboardRead(Clipboard, Sequence, TrustedReadFailure));
        }
        public Task<bool> RestoreClipboardAsync(IClipboardBackup backup, uint expectedSequence)
        {
            OnRestore?.Invoke();
            if (Sequence != expectedSequence) return Task.FromResult(false);
            Clipboard = ((Backup)backup).Text;
            Sequence++;
            Restores++;
            return Task.FromResult(true);
        }
    }
}

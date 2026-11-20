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
    public async Task FocusSwitchImmediatelyAfterCopyStillRestoresOriginalClipboard()
    {
        var platform = new FakePlatform();
        platform.OnCopy = () => platform.Current = false;
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.SourceChanged, error.Failure);
        Assert.Equal("original", platform.Clipboard);
        Assert.Equal(1, platform.Restores);
    }

    [Fact]
    public async Task LaterCopyBeforeRecoveryIsPreserved()
    {
        var platform = new FakePlatform();
        platform.OnCopy = () => { platform.Clipboard = "our selection"; platform.Sequence++; };
        platform.OnRead = () => { platform.Clipboard = "user's later copy"; platform.Sequence++; };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ClipboardChanged, error.Failure);
        Assert.Equal("user's later copy", platform.Clipboard);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task CopyWithUnverifiedOwnerIsNeverRestored()
    {
        var platform = new FakePlatform { ReadOwnerMatchesSource = false };
        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.ClipboardChanged, error.Failure);
        Assert.Equal("ocean", platform.Clipboard);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task SameProcessCopyAfterFocusSwitchWithoutObservedCopyIsNeverOverwritten()
    {
        var platform = new FakePlatform { UpdateOnCopy = false };
        // Inject Ctrl+C without writing clipboard, then switch focus.
        platform.OnCopy = () => platform.Current = false;
        // During the source check, user copies in another window of the same process (same PID).
        platform.OnSourceCheck = () =>
        {
            if (!platform.Current && platform.Copies > 0 && platform.Sequence == 10)
            {
                platform.Clipboard = "user same-pid copy";
                platform.Sequence = 11;
            }
        };

        var error = await Assert.ThrowsAsync<CaptureException>(() => new SelectionCaptureService(platform).CaptureSelectionAsync());
        Assert.Equal(CaptureFailure.SourceChanged, error.Failure);
        Assert.Equal("user same-pid copy", platform.Clipboard);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task DelayedRenderingSequenceIncrementDuringReadStillSucceedsAndRestores()
    {
        var platform = new FakePlatform();
        platform.OnDelayedRender = () => platform.Sequence++; // Delayed rendering increments sequence inside lock.
        var result = await new SelectionCaptureService(platform).CaptureSelectionAsync();
        Assert.Equal("ocean", result!.SelectedText);
        Assert.Equal("original", platform.Clipboard);
        Assert.Equal(1, platform.Restores);
    }

    [Fact]
    public void ClipboardBackupBudgetIncludesLargeGraphicsAndMemoryFormats()
    {
        const long budget = 64L * 1024 * 1024;
        long used = 0;
        Assert.True(NativeClipboardBackup.TryAddToBudget(ref used, 24L * 1024 * 1024)); // bitmap estimate
        Assert.True(NativeClipboardBackup.TryAddToBudget(ref used, 24L * 1024 * 1024)); // enhanced metafile estimate
        Assert.True(NativeClipboardBackup.TryAddToBudget(ref used, 16L * 1024 * 1024)); // global memory format
        Assert.Equal(budget, used);
        Assert.False(NativeClipboardBackup.TryAddToBudget(ref used, 1));
        Assert.Equal(budget, used);
    }

    [Fact]
    public void ClipboardBackupBudgetRejectsOversizedAndInvalidItems()
    {
        long used = 0;
        Assert.False(NativeClipboardBackup.TryAddToBudget(ref used, 64L * 1024 * 1024 + 1));
        Assert.False(NativeClipboardBackup.TryAddToBudget(ref used, 0));
        Assert.False(NativeClipboardBackup.TryAddToBudget(ref used, -1));
        Assert.Equal(0, used);
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
    public async Task SlowCopyBeyondOneSecondSucceedsWithoutSendingAnotherCopy()
    {
        var platform = new FakePlatform { CopyDelay = TimeSpan.FromMilliseconds(1250) };
        var result = await new SelectionCaptureService(platform).CaptureSelectionAsync();
        Assert.Equal("ocean", result!.SelectedText);
        Assert.Equal(1, platform.Copies);
        Assert.Equal("original", platform.Clipboard);
    }

    [Fact]
    public async Task SwitchingWindowWhileWaitingForCopyStopsCapture()
    {
        var platform = new FakePlatform { UpdateOnCopy = false };
        var capture = new SelectionCaptureService(platform).CaptureSelectionAsync();
        platform.Current = false;
        var error = await Assert.ThrowsAsync<CaptureException>(() => capture);
        Assert.Equal(CaptureFailure.SourceChanged, error.Failure);
        Assert.Equal("original", platform.Clipboard);
        Assert.Equal(0, platform.Restores);
    }

    [Fact]
    public async Task CancellationWhileWaitingForCopyReleasesCaptureLock()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new FakePlatform { UpdateOnCopy = false };
        var service = new SelectionCaptureService(platform);
        var capture = service.CaptureSelectionAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal("original", platform.Clipboard);
        platform.UpdateOnCopy = true;
        Assert.NotNull(await service.CaptureSelectionAsync());
    }

    [Fact]
    public async Task BlockedUiaWorkerDoesNotBlockClipboardWorker()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uia = NativeSelectionCapturePlatform.ExecuteInStaAsync(() =>
        {
            entered.SetResult();
            release.Wait();
            return true;
        }, useUiaWorker: true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var clipboard = NativeSelectionCapturePlatform.ExecuteInStaAsync(() => true);
            Assert.True(await clipboard.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.Set();
            await uia.WaitAsync(TimeSpan.FromSeconds(5));
        }
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
        var captured = new List<CaptureResult>();
        var failures = new List<CaptureException>();
        Task ShowResult(CaptureResult result) { captured.Add(result); return Task.CompletedTask; }
        void ShowFailure(CaptureException error) { platform.Current = false; failures.Add(error); }
        var first = HotkeyCaptureHandler.HandleAsync(service, ShowResult, ShowFailure);
        await HotkeyCaptureHandler.HandleAsync(service, ShowResult, ShowFailure);
        Assert.Empty(captured);
        Assert.Empty(failures);
        Assert.True(platform.Current);
        gate.SetResult();
        await first;
        Assert.Equal("ocean", Assert.Single(captured).SelectedText);
        Assert.Empty(failures);
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
        public uint ClipboardSequence
        {
            get
            {
                if (_copyTimer != null && _copyTimer.Elapsed >= CopyDelay)
                {
                    _copyTimer = null;
                    Sequence++;
                    Clipboard = CopiedText;
                }
                return Sequence;
            }
        }
        public TimeSpan CopyDelay;
        private System.Diagnostics.Stopwatch? _copyTimer;
        public string? Clipboard = "original";
        public string CopiedText = "ocean";
        public SelectionSnapshot? Uia;
        public bool Current = true, UpdateOnCopy = true;
        public int Copies, Backups, Restores;
        public Backup? Backup;
        public CaptureFailure? BackupError, CopyError, ReadError, TrustedReadFailure, ModifierFailure;
        public bool ReadOwnerMatchesSource = true;
        public Action? OnModifiers, OnBackup, OnContext, OnRestore, OnRead, OnDelayedRender, OnCopy, OnSourceCheck;
        public Task? ModifierGate;
        public Task<SelectionSnapshot?>? UiaGate;
        public CaptureSource GetSource() => new(new IntPtr(123), 456, "test", "Test window", new(10, 20));
        public bool IsCurrent(CaptureSource source)
        {
            OnSourceCheck?.Invoke();
            return Current;
        }
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
            if (UpdateOnCopy)
            {
                if (CopyDelay > TimeSpan.Zero) _copyTimer = System.Diagnostics.Stopwatch.StartNew();
                else { Sequence++; Clipboard = CopiedText; }
            }
            OnCopy?.Invoke();
        }
        public Task<ClipboardRead> ReadCopiedTextAsync(CaptureSource source, uint expectedSequence)
        {
            if (ReadError.HasValue) throw new CaptureException(ReadError.Value);
            if (!ReadOwnerMatchesSource) throw new CaptureException(CaptureFailure.ClipboardChanged);
            OnRead?.Invoke();
            if (Sequence != expectedSequence) throw new CaptureException(CaptureFailure.ClipboardChanged);
            OnDelayedRender?.Invoke();
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

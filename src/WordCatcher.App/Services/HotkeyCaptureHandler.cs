using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

internal static class HotkeyCaptureHandler
{
    internal static async Task HandleAsync(ISelectionCaptureService service,
        Func<CaptureResult, Task> onCaptured, Action<CaptureException> onFailure)
    {
        CaptureResult? capture;
        try { capture = await service.CaptureSelectionAsync().ConfigureAwait(true); }
        catch (CaptureException ex) { onFailure(ex); return; }
        catch (OperationCanceledException) { return; }

        // A null result means another capture is still running. Keep its source window focused.
        if (capture == null) return;
        if (string.IsNullOrWhiteSpace(capture.SelectedText))
        {
            onFailure(new CaptureException(CaptureFailure.NoSelection));
            return;
        }
        await onCaptured(capture).ConfigureAwait(true);
    }
}

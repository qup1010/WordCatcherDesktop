using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface IStreamingTranslationService : ITranslationService
{
    Task<TranslationResult> TranslateStreamingAsync(CaptureResult capture,
        IProgress<TranslationResult> progress, bool bypassCache = false, CancellationToken ct = default);
}

public interface IStreamingLookupService : ILookupService
{
    Task<TranslationResult> LookupStreamingAsync(CaptureResult capture, bool forceAi,
        IProgress<TranslationResult> progress, bool bypassCache = false, CancellationToken ct = default);
}

using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface ILookupService
{
    Task<TranslationResult> LookupAsync(CaptureResult capture, bool forceAi, CancellationToken ct = default);
    Task<string> TranslateSentenceAsync(string sentence, string? selection = null, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}

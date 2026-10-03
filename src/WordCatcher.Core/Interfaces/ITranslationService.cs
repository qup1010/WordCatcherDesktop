using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(CaptureResult capture, CancellationToken ct = default);
}

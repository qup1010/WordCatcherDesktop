using System.Threading;
using System.Threading.Tasks;

namespace WordCatcher.Core.Interfaces;

public interface IMachineTranslationService
{
    Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string provider = "microsoft",
        CancellationToken ct = default);
}

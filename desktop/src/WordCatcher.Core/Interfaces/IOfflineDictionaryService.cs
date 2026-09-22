using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface IOfflineDictionaryService
{
    bool IsInstalled { get; }
    DictionaryMetadata? Metadata { get; }
    Task<TranslationResult?> LookupAsync(string text, CancellationToken ct = default);
}

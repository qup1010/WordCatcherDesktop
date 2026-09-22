using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface IAnkiClient
{
    Task<int> CheckVersionAsync(CancellationToken ct = default);
    Task EnsureDeckAndModelAsync(CancellationToken ct = default);
    Task<long> AddNoteAsync(Word word, Occurrence occurrence, CancellationToken ct = default);
}

using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public sealed record QueryHistoryItem(CaptureResult Capture, TranslationResult Result);

public interface IQueryHistory
{
    Task RecordAsync(CaptureResult capture, TranslationResult result, CancellationToken ct = default);
    Task<IReadOnlyList<QueryHistoryItem>> ReadAsync(CancellationToken ct = default);
}

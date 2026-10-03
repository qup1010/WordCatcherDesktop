using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;
using WordCatcher.Core.Enums;

namespace WordCatcher.Core.Interfaces;

public interface IWordRepository
{
    Task<(Word Word, Occurrence Occurrence, SyncJob SyncJob)> SaveAsync(SaveCardCommand command, CancellationToken ct = default);
    Task<IReadOnlyList<Word>> GetWordsAsync(string? query = null, int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<IReadOnlyList<Word>> GetFilteredWordsAsync(string? query, WordFilter filter, int limit = 100, int offset = 0, CancellationToken ct = default);
    Task<Word?> FindWordAsync(string text, CancellationToken ct = default);
    Task MoveWordToTrashAsync(string wordId, CancellationToken ct = default);
    Task RestoreWordAsync(string wordId, CancellationToken ct = default);
    Task<Word?> GetWordByIdAsync(string id, CancellationToken ct = default);
    Task<Occurrence?> GetOccurrenceByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Occurrence>> GetOccurrencesByWordIdAsync(string wordId, CancellationToken ct = default);
    Task UpdateWordAsync(Word word, CancellationToken ct = default);
    Task DeleteWordAsync(string wordId, CancellationToken ct = default);
    Task<string> ExportWordsJsonAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SyncJob>> GetSyncJobsAsync(CancellationToken ct = default);
    Task<SyncJob?> GetSyncJobByIdAsync(string id, CancellationToken ct = default);
    Task UpdateSyncJobAsync(SyncJob job, CancellationToken ct = default);
    Task RecoverInterruptedSyncJobsAsync(CancellationToken ct = default);
    Task ResetSyncJobAsync(string jobId, CancellationToken ct = default);
    Task ResetAllFailedSyncJobsAsync(CancellationToken ct = default);
}

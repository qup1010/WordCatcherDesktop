using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Database;

public sealed class WordRepository : IWordRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public WordRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<(Word Word, Occurrence Occurrence, SyncJob SyncJob)> SaveAsync(
        SaveCardCommand command,
        CancellationToken ct = default)
    {
        var capture = command.Capture;
        var trans = command.Translation;

        var displayWord = trans.Word.Trim();
        var normalized = displayWord.ToLowerInvariant();
        var language = "en"; // MVP primary language is English
        var now = DateTimeOffset.UtcNow;
        var nowStr = now.ToString("o");

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {
            // 1. Check existing word
            string wordId;
            Word word;

            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.Transaction = transaction;
                checkCmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc
FROM words
WHERE language = $lang AND normalized_word = $norm
LIMIT 1;";
                checkCmd.Parameters.AddWithValue("$lang", language);
                checkCmd.Parameters.AddWithValue("$norm", normalized);

                await using var reader = await checkCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    wordId = reader.GetString(0);
                    var existingReading = reader.GetString(4);
                    var existingPos = reader.GetString(5);
                    var existingDef = reader.GetString(6);
                    var existingHook = reader.GetString(7);
                    var createdAt = DateTimeOffset.Parse(reader.GetString(8));

                    var newReading = !string.IsNullOrWhiteSpace(trans.Reading) ? trans.Reading : existingReading;
                    var newPos = !string.IsNullOrWhiteSpace(trans.PartOfSpeech) ? trans.PartOfSpeech : existingPos;
                    var newDef = !string.IsNullOrWhiteSpace(trans.Definition) ? trans.Definition : existingDef;
                    var newHook = !string.IsNullOrWhiteSpace(trans.MemoryHook) ? trans.MemoryHook : existingHook;

                    await using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
UPDATE words
SET deleted_at_utc = NULL,
    display_word = $display,
    reading = $reading,
    part_of_speech = $pos,
    definition = $def,
    memory_hook = $hook,
    updated_at_utc = $updated
WHERE id = $id;";
                        updateCmd.Parameters.AddWithValue("$display", displayWord);
                        updateCmd.Parameters.AddWithValue("$reading", newReading);
                        updateCmd.Parameters.AddWithValue("$pos", newPos);
                        updateCmd.Parameters.AddWithValue("$def", newDef);
                        updateCmd.Parameters.AddWithValue("$hook", newHook);
                        updateCmd.Parameters.AddWithValue("$updated", nowStr);
                        updateCmd.Parameters.AddWithValue("$id", wordId);
                        await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    word = new Word
                    {
                        Id = wordId,
                        NormalizedWord = normalized,
                        DisplayWord = displayWord,
                        Language = language,
                        Reading = newReading,
                        PartOfSpeech = newPos,
                        Definition = newDef,
                        MemoryHook = newHook,
                        CreatedAtUtc = createdAt,
                        UpdatedAtUtc = now
                    };
                }
                else
                {
                    wordId = Guid.NewGuid().ToString("N");
                    await using (var insertCmd = connection.CreateCommand())
                    {
                        insertCmd.Transaction = transaction;
                        insertCmd.CommandText = @"
INSERT INTO words (id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc)
VALUES ($id, $norm, $display, $lang, $reading, $pos, $def, $hook, $created, $updated);";
                        insertCmd.Parameters.AddWithValue("$id", wordId);
                        insertCmd.Parameters.AddWithValue("$norm", normalized);
                        insertCmd.Parameters.AddWithValue("$display", displayWord);
                        insertCmd.Parameters.AddWithValue("$lang", language);
                        insertCmd.Parameters.AddWithValue("$reading", trans.Reading);
                        insertCmd.Parameters.AddWithValue("$pos", trans.PartOfSpeech);
                        insertCmd.Parameters.AddWithValue("$def", trans.Definition);
                        insertCmd.Parameters.AddWithValue("$hook", trans.MemoryHook ?? string.Empty);
                        insertCmd.Parameters.AddWithValue("$created", nowStr);
                        insertCmd.Parameters.AddWithValue("$updated", nowStr);
                        await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    word = new Word
                    {
                        Id = wordId,
                        NormalizedWord = normalized,
                        DisplayWord = displayWord,
                        Language = language,
                        Reading = trans.Reading,
                        PartOfSpeech = trans.PartOfSpeech,
                        Definition = trans.Definition,
                        MemoryHook = trans.MemoryHook ?? string.Empty,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    };
                }
            }

            // 2. Insert occurrence
            var occurrenceId = Guid.NewGuid().ToString("N");
            var occurrence = new Occurrence
            {
                Id = occurrenceId,
                WordId = wordId,
                SelectedText = capture.SelectedText,
                Sentence = string.IsNullOrWhiteSpace(capture.Sentence)
                    ? capture.SelectedText
                    : capture.Sentence,
                SelectionOffset = capture.SentenceOffset,
                ContextTranslation = trans.ContextTranslation,
                SourceProcess = capture.SourceProcess,
                SourceWindowTitle = capture.SourceWindowTitle,
                SourceUri = capture.SourceUri,
                LookupSource = trans.Source.ToString(),
                SourceEntryId = trans.SourceEntryId ?? string.Empty,
                SourceSchemaVersion = trans.SourceSchemaVersion ?? string.Empty,
                CapturedAtUtc = capture.CapturedAt
            };

            await using (var insertOccCmd = connection.CreateCommand())
            {
                insertOccCmd.Transaction = transaction;
                insertOccCmd.CommandText = @"
INSERT INTO occurrences (id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc)
VALUES ($id, $wordId, $sel, $sentence, $selectionOffset, $trans, $proc, $win, $uri, $source, $entryId, $schemaVer, $captured);";
                insertOccCmd.Parameters.AddWithValue("$id", occurrence.Id);
                insertOccCmd.Parameters.AddWithValue("$wordId", occurrence.WordId);
                insertOccCmd.Parameters.AddWithValue("$sel", occurrence.SelectedText);
                insertOccCmd.Parameters.AddWithValue("$sentence", occurrence.Sentence);
                insertOccCmd.Parameters.AddWithValue("$selectionOffset", occurrence.SelectionOffset);
                insertOccCmd.Parameters.AddWithValue("$trans", occurrence.ContextTranslation);
                insertOccCmd.Parameters.AddWithValue("$proc", occurrence.SourceProcess);
                insertOccCmd.Parameters.AddWithValue("$win", occurrence.SourceWindowTitle);
                insertOccCmd.Parameters.AddWithValue("$uri", occurrence.SourceUri);
                insertOccCmd.Parameters.AddWithValue("$source", occurrence.LookupSource);
                insertOccCmd.Parameters.AddWithValue("$entryId", occurrence.SourceEntryId);
                insertOccCmd.Parameters.AddWithValue("$schemaVer", occurrence.SourceSchemaVersion);
                insertOccCmd.Parameters.AddWithValue("$captured", occurrence.CapturedAtUtc.ToString("o"));
                await insertOccCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // 3. Insert sync_job
            var syncJob = new SyncJob
            {
                Id = Guid.NewGuid().ToString("N"),
                WordId = wordId,
                OccurrenceId = occurrenceId,
                Target = "anki",
                Status = SyncStatus.Pending,
                Attempts = 0,
                LastError = string.Empty,
                NextAttemptAtUtc = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await using (var insertJobCmd = connection.CreateCommand())
            {
                insertJobCmd.Transaction = transaction;
                insertJobCmd.CommandText = @"
INSERT INTO sync_jobs (id, word_id, occurrence_id, target, status, attempts, last_error, next_attempt_at_utc, created_at_utc, updated_at_utc)
VALUES ($id, $wordId, $occId, $target, $status, $attempts, $lastError, $nextAttempt, $created, $updated);";
                insertJobCmd.Parameters.AddWithValue("$id", syncJob.Id);
                insertJobCmd.Parameters.AddWithValue("$wordId", syncJob.WordId);
                insertJobCmd.Parameters.AddWithValue("$occId", syncJob.OccurrenceId);
                insertJobCmd.Parameters.AddWithValue("$target", syncJob.Target);
                insertJobCmd.Parameters.AddWithValue("$status", syncJob.Status.ToString());
                insertJobCmd.Parameters.AddWithValue("$attempts", syncJob.Attempts);
                insertJobCmd.Parameters.AddWithValue("$lastError", syncJob.LastError);
                insertJobCmd.Parameters.AddWithValue("$nextAttempt", (object?)syncJob.NextAttemptAtUtc?.ToString("o") ?? DBNull.Value);
                insertJobCmd.Parameters.AddWithValue("$created", syncJob.CreatedAtUtc.ToString("o"));
                insertJobCmd.Parameters.AddWithValue("$updated", syncJob.UpdatedAtUtc.ToString("o"));
                await insertJobCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (word, occurrence, syncJob);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public Task<IReadOnlyList<Word>> GetWordsAsync(string? query = null, int limit = 100,
        int offset = 0, CancellationToken ct = default)
        => GetFilteredWordsAsync(query, WordFilter.All, limit, offset, ct);

    public async Task<IReadOnlyList<Word>> GetFilteredWordsAsync(string? query, WordFilter filter,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc
FROM words w
WHERE deleted_at_utc IS NULL
AND ($q = '' OR normalized_word LIKE $pattern OR display_word LIKE $pattern OR definition LIKE $pattern
    OR EXISTS (SELECT 1 FROM occurrences o WHERE o.word_id = w.id
        AND (o.sentence LIKE $pattern OR o.context_translation LIKE $pattern)))
AND ($filter = 0
    OR ($filter = 1 AND EXISTS (SELECT 1 FROM sync_jobs j WHERE j.word_id = w.id AND j.status IN ('Pending', 'Syncing', 'Retryable')))
    OR ($filter = 2 AND EXISTS (SELECT 1 FROM sync_jobs j WHERE j.word_id = w.id AND j.status = 'Failed')))
ORDER BY updated_at_utc DESC, id ASC
LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$q", query?.Trim() ?? string.Empty);
        cmd.Parameters.AddWithValue("$pattern", $"%{query?.Trim()}%");
        cmd.Parameters.AddWithValue("$filter", (int)filter);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var list = new List<Word>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadWord(reader));
        return list;
    }

    public async Task<Word?> FindWordAsync(string text, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc
FROM words WHERE language = 'en' AND normalized_word = $word AND deleted_at_utc IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$word", text.Trim().ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadWord(reader) : null;
    }

    public Task MoveWordToTrashAsync(string wordId, CancellationToken ct = default)
        => SetDeletedAsync(wordId, DateTimeOffset.UtcNow.ToString("o"), ct);

    public Task RestoreWordAsync(string wordId, CancellationToken ct = default)
        => SetDeletedAsync(wordId, null, ct);

    private async Task SetDeletedAsync(string wordId, string? deletedAt, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE words SET deleted_at_utc = $deleted WHERE id = $id;";
        cmd.Parameters.AddWithValue("$deleted", (object?)deletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", wordId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<Word?> GetWordByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc
FROM words
WHERE id = $id AND deleted_at_utc IS NULL
LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadWord(reader);
        }

        return null;
    }

    public async Task<IReadOnlyList<Occurrence>> GetOccurrencesByWordIdAsync(string wordId, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc
FROM occurrences
WHERE word_id = $wordId
ORDER BY captured_at_utc DESC;";
        cmd.Parameters.AddWithValue("$wordId", wordId);

        var list = new List<Occurrence>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new Occurrence
            {
                Id = reader.GetString(0),
                WordId = reader.GetString(1),
                SelectedText = reader.GetString(2),
                Sentence = reader.GetString(3),
                SelectionOffset = reader.GetInt32(4),
                ContextTranslation = reader.GetString(5),
                SourceProcess = reader.GetString(6),
                SourceWindowTitle = reader.GetString(7),
                SourceUri = reader.GetString(8),
                LookupSource = reader.GetString(9),
                SourceEntryId = reader.GetString(10),
                SourceSchemaVersion = reader.GetString(11),
                CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(12))
            });
        }

        return list;
    }

    public async Task<Occurrence?> GetOccurrenceByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc
FROM occurrences
WHERE id = $id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new Occurrence
        {
            Id = reader.GetString(0),
            WordId = reader.GetString(1),
            SelectedText = reader.GetString(2),
            Sentence = reader.GetString(3),
            SelectionOffset = reader.GetInt32(4),
            ContextTranslation = reader.GetString(5),
            SourceProcess = reader.GetString(6),
            SourceWindowTitle = reader.GetString(7),
            SourceUri = reader.GetString(8),
            LookupSource = reader.GetString(9),
            SourceEntryId = reader.GetString(10),
            SourceSchemaVersion = reader.GetString(11),
            CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(12))
        };
    }

    public async Task UpdateWordAsync(Word word, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE words
SET display_word = $display,
    reading = $reading,
    part_of_speech = $pos,
    definition = $def,
    memory_hook = $hook,
    updated_at_utc = $updated
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$display", word.DisplayWord);
        cmd.Parameters.AddWithValue("$reading", word.Reading);
        cmd.Parameters.AddWithValue("$pos", word.PartOfSpeech);
        cmd.Parameters.AddWithValue("$def", word.Definition);
        cmd.Parameters.AddWithValue("$hook", word.MemoryHook);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", word.Id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteWordAsync(string wordId, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM words WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", wordId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> ExportWordsJsonAsync(CancellationToken ct = default)
    {
        // SQLite 的负数 LIMIT 表示不限制行数，备份必须包含全部未删除词条。
        var words = await GetWordsAsync(limit: -1, ct: ct).ConfigureAwait(false);
        var exportList = new List<object>();

        foreach (var word in words)
        {
            var occurrences = await GetOccurrencesByWordIdAsync(word.Id, ct).ConfigureAwait(false);
            exportList.Add(new
            {
                word.Id,
                word.NormalizedWord,
                word.DisplayWord,
                word.Language,
                word.Reading,
                word.PartOfSpeech,
                word.Definition,
                word.MemoryHook,
                word.CreatedAtUtc,
                word.UpdatedAtUtc,
                Occurrences = occurrences
            });
        }

        return JsonSerializer.Serialize(exportList, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<IReadOnlyList<SyncJob>> GetSyncJobsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT j.id, j.word_id, j.occurrence_id, j.target, j.status, j.attempts, j.last_error, j.next_attempt_at_utc, j.created_at_utc, j.updated_at_utc,
       w.id, w.normalized_word, w.display_word, w.language, w.reading, w.part_of_speech, w.definition, w.memory_hook, w.created_at_utc, w.updated_at_utc,
       o.id, o.word_id, o.selected_text, o.sentence, o.selection_offset, o.context_translation, o.source_process, o.source_window_title, o.source_uri, o.lookup_source, o.source_entry_id, o.source_schema_version, o.captured_at_utc
FROM sync_jobs j
INNER JOIN words w ON j.word_id = w.id
INNER JOIN occurrences o ON j.occurrence_id = o.id
WHERE w.deleted_at_utc IS NULL
ORDER BY j.created_at_utc DESC;";

        var list = new List<SyncJob>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var job = new SyncJob
            {
                Id = reader.GetString(0),
                WordId = reader.GetString(1),
                OccurrenceId = reader.GetString(2),
                Target = reader.GetString(3),
                Status = Enum.Parse<SyncStatus>(reader.GetString(4)),
                Attempts = reader.GetInt32(5),
                LastError = reader.GetString(6),
                NextAttemptAtUtc = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(8)),
                UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(9)),
                Word = new Word
                {
                    Id = reader.GetString(10),
                    NormalizedWord = reader.GetString(11),
                    DisplayWord = reader.GetString(12),
                    Language = reader.GetString(13),
                    Reading = reader.GetString(14),
                    PartOfSpeech = reader.GetString(15),
                    Definition = reader.GetString(16),
                    MemoryHook = reader.GetString(17),
                    CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(18)),
                    UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(19))
                },
                Occurrence = new Occurrence
                {
                    Id = reader.GetString(20),
                    WordId = reader.GetString(21),
                    SelectedText = reader.GetString(22),
                    Sentence = reader.GetString(23),
                    SelectionOffset = reader.GetInt32(24),
                    ContextTranslation = reader.GetString(25),
                    SourceProcess = reader.GetString(26),
                    SourceWindowTitle = reader.GetString(27),
                    SourceUri = reader.GetString(28),
                    LookupSource = reader.GetString(29),
                    SourceEntryId = reader.GetString(30),
                    SourceSchemaVersion = reader.GetString(31),
                    CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(32))
                }
            };
            list.Add(job);
        }

        return list;
    }

    public async Task<SyncJob?> GetSyncJobByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, word_id, occurrence_id, target, status, attempts, last_error, next_attempt_at_utc, created_at_utc, updated_at_utc
FROM sync_jobs
WHERE word_id IN (SELECT id FROM words WHERE deleted_at_utc IS NULL) AND id = $id
LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return new SyncJob
            {
                Id = reader.GetString(0),
                WordId = reader.GetString(1),
                OccurrenceId = reader.GetString(2),
                Target = reader.GetString(3),
                Status = Enum.Parse<SyncStatus>(reader.GetString(4)),
                Attempts = reader.GetInt32(5),
                LastError = reader.GetString(6),
                NextAttemptAtUtc = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(8)),
                UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(9))
            };
        }

        return null;
    }

    public async Task UpdateSyncJobAsync(SyncJob job, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE sync_jobs
SET status = $status,
    attempts = $attempts,
    last_error = $error,
    next_attempt_at_utc = $nextAttempt,
    updated_at_utc = $updated
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$status", job.Status.ToString());
        cmd.Parameters.AddWithValue("$attempts", job.Attempts);
        cmd.Parameters.AddWithValue("$error", job.LastError);
        cmd.Parameters.AddWithValue("$nextAttempt", (object?)job.NextAttemptAtUtc?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", job.Id);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecoverInterruptedSyncJobsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE sync_jobs
SET status = 'Pending',
    last_error = '应用上次在同步过程中退出，已重新排队',
    next_attempt_at_utc = NULL,
    updated_at_utc = $now
WHERE status = 'Syncing';";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetSyncJobAsync(string jobId, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE sync_jobs
SET status = 'Pending',
    attempts = 0,
    last_error = '',
    next_attempt_at_utc = NULL,
    updated_at_utc = $now
WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", jobId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ResetAllFailedSyncJobsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
UPDATE sync_jobs
SET status = 'Pending',
    attempts = 0,
    last_error = '',
    next_attempt_at_utc = NULL,
    updated_at_utc = $now
WHERE status IN ('Failed', 'Retryable');";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Word ReadWord(SqliteDataReader reader)
    {
        return new Word
        {
            Id = reader.GetString(0),
            NormalizedWord = reader.GetString(1),
            DisplayWord = reader.GetString(2),
            Language = reader.GetString(3),
            Reading = reader.GetString(4),
            PartOfSpeech = reader.GetString(5),
            Definition = reader.GetString(6),
            MemoryHook = reader.GetString(7),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(8)),
            UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(9))
        };
    }
}

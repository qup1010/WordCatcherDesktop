using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Database;

public sealed class WordRepository : IWordRepository
{
    private static readonly SemaphoreSlim DatabaseMutationLock = new(1, 1);
    private readonly SqliteConnectionFactory _connectionFactory;

    public WordRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<(Word Word, Occurrence Occurrence, SyncJob SyncJob)> SaveAsync(
        SaveCardCommand command,
        CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(command, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task<(Word Word, Occurrence Occurrence, SyncJob SyncJob)> SaveCoreAsync(
        SaveCardCommand command,
        CancellationToken ct,
        SqliteConnection? sharedConnection = null,
        SqliteTransaction? sharedTransaction = null)
    {
        var capture = command.Capture;
        var trans = command.Translation;
        if (string.IsNullOrWhiteSpace(trans.Word) || string.IsNullOrWhiteSpace(trans.Definition))
            throw new ArgumentException("表达和释义不能为空。");

        var displayWord = trans.Word.Trim();
        var normalized = displayWord.ToLowerInvariant();
        var language = "en"; // MVP primary language is English
        var now = DateTimeOffset.UtcNow;
        var nowStr = now.ToString("o");

        SqliteConnection? ownedConnection = null;
        var connection = sharedConnection ?? (ownedConnection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false));
        SqliteTransaction? ownedTransaction = null;
        var transaction = sharedTransaction ?? (ownedTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false));

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

                    var newReading = existingReading;
                    var newPos = existingPos;
                    var newDef = existingDef;
                    var newHook = existingHook;

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

            // 显式标识始终定位原记录；未指定标识的本地收藏才按语境查重。
            string? existingId = null;
            string? existingEdits = null;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT id, user_edits_json FROM occurrences WHERE word_id = $word AND selected_text = $sel AND sentence = $sentence AND selection_offset = $offset AND source_uri = $uri AND ($sync = 0 OR id = $id) LIMIT 1;";
                find.Parameters.AddWithValue("$sync", command.OccurrenceId == null ? 0 : 1);
                find.Parameters.AddWithValue("$id", command.OccurrenceId ?? string.Empty);
                find.Parameters.AddWithValue("$word", wordId);
                find.Parameters.AddWithValue("$sel", capture.SelectedText);
                find.Parameters.AddWithValue("$sentence", string.IsNullOrWhiteSpace(capture.Sentence) ? capture.SelectedText : capture.Sentence);
                find.Parameters.AddWithValue("$offset", capture.SentenceOffset);
                find.Parameters.AddWithValue("$uri", capture.SourceUri);
                await using var existing = await find.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await existing.ReadAsync(ct).ConfigureAwait(false))
                { existingId = existing.GetString(0); existingEdits = existing.GetString(1); }
            }
            if (existingId != null && !command.UpdateExisting)
            {
                await using (var restoreMeta = connection.CreateCommand())
                {
                    restoreMeta.Transaction = transaction;
                    restoreMeta.CommandText = @"
UPDATE sync_records_meta SET deleted_at_utc = NULL, updated_at_utc = $now, revision = revision + 1
WHERE occurrence_id = $id AND deleted_at_utc IS NOT NULL;";
                    restoreMeta.Parameters.AddWithValue("$now", nowStr);
                    restoreMeta.Parameters.AddWithValue("$id", existingId);
                    await restoreMeta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await AdvanceSyncVersionVectorsForOccurrenceAsync(connection, transaction, existingId, ct).ConfigureAwait(false);
                }
                if (ownedTransaction != null) await transaction.CommitAsync(ct).ConfigureAwait(false);
                var saved = await GetOccurrenceByIdAsync(existingId, ct).ConfigureAwait(false);
                var jobs = await GetSyncJobsAsync(ct).ConfigureAwait(false);
                var job = System.Linq.Enumerable.FirstOrDefault(jobs, j => j.OccurrenceId == existingId)
                    ?? throw new InvalidOperationException("已保存语境缺少同步记录。");
                return (word, saved!, job);
            }
            var preservedEdits = existingEdits == null ? null : JsonSerializer.Deserialize<LearningEdits>(existingEdits);
            var occurrenceId = existingId ?? command.OccurrenceId ?? Guid.NewGuid().ToString("N");
            if (existingId == null && command.OccurrenceId != null)
            {
                if (!IsValidRecordId(command.OccurrenceId))
                    throw new ArgumentException("语境标识只能包含字母、数字、连字符和下划线，且不超过 128 个字符。", nameof(command));
                await using var check = connection.CreateCommand();
                check.Transaction = transaction;
                check.CommandText = "SELECT COUNT(*) FROM occurrences WHERE id = $id;";
                check.Parameters.AddWithValue("$id", occurrenceId);
                if (Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0)
                    throw new InvalidOperationException("语境标识已被另一条记录使用。");
            }
            var occurrence = new Occurrence
            {
                Id = occurrenceId,
                LearningResult = trans,
                Edits = command.ReplaceEdits ? command.Edits : command.Edits ?? preservedEdits,
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
INSERT INTO occurrences (id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc, learning_json, user_edits_json)
VALUES ($id, $wordId, $sel, $sentence, $selectionOffset, $trans, $proc, $win, $uri, $source, $entryId, $schemaVer, $captured, $learning, $edits)
ON CONFLICT(id) DO UPDATE SET context_translation = excluded.context_translation, lookup_source = excluded.lookup_source,
source_schema_version = excluded.source_schema_version, learning_json = excluded.learning_json, user_edits_json = excluded.user_edits_json;";
                insertOccCmd.Parameters.AddWithValue("$learning", JsonSerializer.Serialize(trans));
                insertOccCmd.Parameters.AddWithValue("$edits", JsonSerializer.Serialize(occurrence.Edits));
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
                Id = existingId == null ? Guid.NewGuid().ToString("N") : await FindSyncJobIdAsync(connection, transaction, existingId, ct),
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
VALUES ($id, $wordId, $occId, $target, $status, $attempts, $lastError, $nextAttempt, $created, $updated)
ON CONFLICT(id) DO UPDATE SET status = 'Pending', attempts = 0, last_error = '', next_attempt_at_utc = NULL, updated_at_utc = excluded.updated_at_utc, revision = revision + 1;";
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

            // 4. Upsert sync_records_meta
            await UpsertSyncRecordMetaAsync(connection, transaction, word, occurrence, nowStr, ct).ConfigureAwait(false);

            if (ownedTransaction != null) await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (word, occurrence, syncJob);
        }
        catch
        {
            if (ownedTransaction?.Connection != null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (ownedTransaction != null) await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            if (ownedConnection != null) await ownedConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task UpsertSyncRecordMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Word word,
        Occurrence occurrence,
        string nowStr,
        CancellationToken ct)
    {
        var deviceId = await GetOrCreateSyncDeviceIdAsync(connection, transaction, ct).ConfigureAwait(false);
        var vectorJson = await BuildNextLocalVersionVectorJsonAsync(connection, transaction, occurrence.Id, deviceId, ct).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT INTO sync_records_meta (
  occurrence_id, word_id, normalized_word, display_word, selection, sentence, selection_offset, source_uri,
  revision, saved_at_utc, updated_at_utc, deleted_at_utc, snapshot_json, version_vector_json
)
VALUES (
  $occId, $wordId, $norm, $display, $sel, $sentence, $offset, $uri,
  1, $saved, $updated, NULL, $snapshot, $vector
)
ON CONFLICT(occurrence_id) DO UPDATE SET
  word_id = excluded.word_id,
  normalized_word = excluded.normalized_word,
  display_word = excluded.display_word,
  selection = excluded.selection,
  sentence = excluded.sentence,
  selection_offset = excluded.selection_offset,
  source_uri = excluded.source_uri,
  revision = sync_records_meta.revision + 1,
  updated_at_utc = excluded.updated_at_utc,
  deleted_at_utc = NULL,
  snapshot_json = excluded.snapshot_json,
  version_vector_json = excluded.version_vector_json;";
            cmd.Parameters.AddWithValue("$occId", occurrence.Id);
            cmd.Parameters.AddWithValue("$wordId", word.Id);
            cmd.Parameters.AddWithValue("$norm", word.NormalizedWord);
            cmd.Parameters.AddWithValue("$display", word.DisplayWord);
            cmd.Parameters.AddWithValue("$sel", occurrence.SelectedText);
            cmd.Parameters.AddWithValue("$sentence", occurrence.Sentence);
            cmd.Parameters.AddWithValue("$offset", occurrence.SelectionOffset);
            cmd.Parameters.AddWithValue("$uri", occurrence.SourceUri);
            cmd.Parameters.AddWithValue("$saved", occurrence.CapturedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$updated", nowStr);
            cmd.Parameters.AddWithValue("$snapshot", BuildRecordCardJson(word, occurrence));
            cmd.Parameters.AddWithValue("$vector", vectorJson);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsMissingSyncTable(ex))
        {
            // Ignore only if table does not exist in partial test environments
        }
    }

    private static async Task<string> GetOrCreateSyncDeviceIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT value FROM app_meta WHERE key = 'sync_device_id';";
        var existing = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var deviceId = Guid.NewGuid().ToString("N");
        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = "INSERT INTO app_meta (key, value) VALUES ('sync_device_id', $id);";
        write.Parameters.AddWithValue("$id", deviceId);
        await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return deviceId;
    }

    private static async Task EnsureSyncVersionVectorsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string deviceId, CancellationToken ct)
    {
        var missing = new List<(string Id, int Revision)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT occurrence_id, revision FROM sync_records_meta WHERE version_vector_json = '{}' OR version_vector_json = '';";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                missing.Add((reader.GetString(0), Math.Max(1, reader.GetInt32(1))));
        }
        foreach (var (id, revision) in missing)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE sync_records_meta SET version_vector_json = $vector WHERE occurrence_id = $id;";
            update.Parameters.AddWithValue("$vector", SyncVersionVector.Serialize(new Dictionary<string, long> { [deviceId] = revision }));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> BuildNextLocalVersionVectorJsonAsync(
        SqliteConnection connection, SqliteTransaction transaction, string occurrenceId, string deviceId, CancellationToken ct)
    {
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT version_vector_json FROM sync_records_meta WHERE occurrence_id = $id;";
        read.Parameters.AddWithValue("$id", occurrenceId);
        var current = await read.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return SyncVersionVector.Serialize(SyncVersionVector.Increment(SyncVersionVector.Parse(current), deviceId));
    }

    private static async Task AdvanceSyncVersionVectorsForOccurrenceAsync(
        SqliteConnection connection, SqliteTransaction transaction, string occurrenceId, CancellationToken ct)
    {
        var deviceId = await GetOrCreateSyncDeviceIdAsync(connection, transaction, ct).ConfigureAwait(false);
        var next = await BuildNextLocalVersionVectorJsonAsync(connection, transaction, occurrenceId, deviceId, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE sync_records_meta SET version_vector_json = $vector WHERE occurrence_id = $id;";
        cmd.Parameters.AddWithValue("$vector", next);
        cmd.Parameters.AddWithValue("$id", occurrenceId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task AdvanceSyncVersionVectorsForWordAsync(
        SqliteConnection connection, SqliteTransaction transaction, string wordId, CancellationToken ct)
    {
        var ids = new List<string>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT occurrence_id FROM sync_records_meta WHERE word_id = $id;";
            cmd.Parameters.AddWithValue("$id", wordId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }
        foreach (var id in ids)
            await AdvanceSyncVersionVectorsForOccurrenceAsync(connection, transaction, id, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Word>> GetWordsAsync(string? query = null, int limit = 100,
        int offset = 0, CancellationToken ct = default)
        => GetFilteredWordsAsync(query, WordFilter.All, limit, offset, ct);

    public async Task<IReadOnlyList<Word>> GetFilteredWordsAsync(string? query, WordFilter filter,
        int limit = 100, int offset = 0, CancellationToken ct = default, string? sourceProcess = null, int sort = 0)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc,
    COUNT(*) OVER(), (SELECT COUNT(*) FROM occurrences c WHERE c.word_id = w.id)
FROM words w
WHERE deleted_at_utc IS NULL
AND ($q = '' OR normalized_word LIKE $pattern ESCAPE '\' OR display_word LIKE $pattern ESCAPE '\' OR definition LIKE $pattern ESCAPE '\'
    OR EXISTS (SELECT 1 FROM occurrences o WHERE o.word_id = w.id
        AND (o.sentence LIKE $pattern ESCAPE '\' OR o.context_translation LIKE $pattern ESCAPE '\'
          OR json_extract(o.user_edits_json, '$.Note') LIKE $pattern ESCAPE '\'
          OR COALESCE(json_extract(o.user_edits_json, '$.Definition'), json_extract(NULLIF(o.learning_json, ''), '$.Definition')) LIKE $pattern ESCAPE '\'
          OR json_extract(o.user_edits_json, '$.ContextTranslation') LIKE $pattern ESCAPE '\')))
AND ($source = '' OR EXISTS (SELECT 1 FROM occurrences s WHERE s.word_id = w.id AND s.source_process LIKE $sourcePattern ESCAPE '\'))
AND ($filter = 0
    OR ($filter = 1 AND EXISTS (SELECT 1 FROM sync_jobs j WHERE j.word_id = w.id AND j.status IN ('Pending', 'Syncing', 'Retryable')))
    OR ($filter = 2 AND EXISTS (SELECT 1 FROM sync_jobs j WHERE j.word_id = w.id AND j.status = 'Failed'))
    OR ($filter = 3 AND EXISTS (SELECT 1 FROM occurrences r WHERE r.word_id = w.id AND julianday(r.captured_at_utc) >= julianday($recent)))
    OR ($filter = 4 AND EXISTS (SELECT 1 FROM occurrences n WHERE n.word_id = w.id AND trim(COALESCE(json_extract(n.user_edits_json, '$.Note'), '')) <> ''))
    OR ($filter = 5 AND NOT EXISTS (SELECT 1 FROM occurrences n WHERE n.word_id = w.id AND trim(COALESCE(json_extract(n.user_edits_json, '$.Note'), '')) <> '')))
ORDER BY CASE WHEN $sort = 1 THEN display_word END COLLATE NOCASE ASC,
    CASE WHEN $sort = 2 THEN COALESCE((SELECT MAX(julianday(r.captured_at_utc)) FROM occurrences r WHERE r.word_id = w.id), julianday(created_at_utc)) END DESC,
    CASE WHEN $sort = 0 THEN updated_at_utc END DESC, id ASC
LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$q", query?.Trim() ?? string.Empty);
        static string Pattern(string? value) => "%" + (value?.Trim() ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        cmd.Parameters.AddWithValue("$pattern", Pattern(query));
        cmd.Parameters.AddWithValue("$source", sourceProcess?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$sourcePattern", Pattern(sourceProcess));
        cmd.Parameters.AddWithValue("$recent", DateTimeOffset.UtcNow.AddDays(-7).ToString("o"));
        cmd.Parameters.AddWithValue("$sort", Math.Clamp(sort, 0, 2));
        cmd.Parameters.AddWithValue("$filter", (int)filter);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var list = new List<Word>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var word = ReadWord(reader);
            word.MatchingWordCount = reader.GetInt32(10);
            word.OccurrenceCount = reader.GetInt32(11);
            list.Add(word);
        }
        return list;
    }

    public async Task<Word?> FindWordAsync(string text, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        return await FindWordAsync(connection, null, text, ct).ConfigureAwait(false);
    }

    private static async Task<Word?> FindWordAsync(SqliteConnection connection, SqliteTransaction? transaction, string text, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc
FROM words WHERE language = 'en' AND normalized_word = $word AND deleted_at_utc IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$word", text.Trim().ToLowerInvariant());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadWord(reader) : null;
    }

    public Task MoveWordToTrashAsync(string wordId, CancellationToken ct = default)
        => SetDeletedWithLockAsync(wordId, DateTimeOffset.UtcNow.ToString("o"), ct);

    public Task RestoreWordAsync(string wordId, CancellationToken ct = default)
        => SetDeletedWithLockAsync(wordId, null, ct);

    private async Task SetDeletedWithLockAsync(string wordId, string? deletedAt, CancellationToken ct)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SetDeletedCoreAsync(wordId, deletedAt, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task SetDeletedCoreAsync(string wordId, string? deletedAt, CancellationToken ct)
    {
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE words SET deleted_at_utc = $deleted, updated_at_utc = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$deleted", (object?)deletedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", nowStr);
            cmd.Parameters.AddWithValue("$id", wordId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            try
            {
                await using var metaCmd = connection.CreateCommand();
                metaCmd.Transaction = transaction;
                metaCmd.CommandText = @"
UPDATE sync_records_meta
SET deleted_at_utc = $deleted,
    updated_at_utc = $now,
    revision = revision + 1
WHERE word_id = $id;";
                metaCmd.Parameters.AddWithValue("$deleted", (object?)deletedAt ?? DBNull.Value);
                metaCmd.Parameters.AddWithValue("$now", nowStr);
                metaCmd.Parameters.AddWithValue("$id", wordId);
                await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await AdvanceSyncVersionVectorsForWordAsync(connection, transaction, wordId, ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (transaction.Connection != null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Word?> GetWordByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        return await GetWordByIdAsync(connection, null, id, ct).ConfigureAwait(false);
    }

    private static async Task<Word?> GetWordByIdAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
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
        return await ReadOccurrencesByWordIdAsync(connection, null, wordId, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Occurrence>> ReadOccurrencesByWordIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string wordId,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc, learning_json, user_edits_json
FROM occurrences
WHERE word_id = $wordId
  AND (NOT EXISTS (SELECT 1 FROM sync_records_meta m WHERE m.occurrence_id = occurrences.id AND m.deleted_at_utc IS NOT NULL)
       OR EXISTS (SELECT 1 FROM words w WHERE w.id = occurrences.word_id AND w.deleted_at_utc IS NOT NULL))
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
                CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(12)),
                LearningResult = DeserializeResult(reader.GetString(13)),
                Edits = JsonSerializer.Deserialize<LearningEdits>(reader.GetString(14))
            });
        }

        return list;
    }

    public async Task<Occurrence?> GetOccurrenceByIdAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        return await GetOccurrenceByIdAsync(connection, null, id, ct).ConfigureAwait(false);
    }

    private static async Task<Occurrence?> GetOccurrenceByIdAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
SELECT id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc, learning_json, user_edits_json
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
            CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(12)),
            LearningResult = DeserializeResult(reader.GetString(13)),
            Edits = JsonSerializer.Deserialize<LearningEdits>(reader.GetString(14))
        };
    }

    public async Task UpdateWordAsync(Word word, CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UpdateWordCoreAsync(word, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task UpdateWordCoreAsync(Word word, CancellationToken ct)
    {
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        var displayWord = word.DisplayWord.Trim();
        var normalizedWord = displayWord.ToLowerInvariant();
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var duplicateCheck = connection.CreateCommand())
            {
                duplicateCheck.Transaction = transaction;
                duplicateCheck.CommandText = "SELECT id FROM words WHERE normalized_word = $normalized AND id <> $id LIMIT 1;";
                duplicateCheck.Parameters.AddWithValue("$normalized", normalizedWord);
                duplicateCheck.Parameters.AddWithValue("$id", word.Id);
                if (await duplicateCheck.ExecuteScalarAsync(ct).ConfigureAwait(false) is string conflictingWordId)
                    throw new InvalidOperationException($"本地已有同名词条（{conflictingWordId}），请先合并该词条后再改名。");
            }
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
UPDATE words
SET normalized_word = $normalized,
    display_word = $display,
    reading = $reading,
    part_of_speech = $pos,
    definition = $def,
    memory_hook = $hook,
    updated_at_utc = $updated
WHERE id = $id;";
            cmd.Parameters.AddWithValue("$normalized", normalizedWord);
            cmd.Parameters.AddWithValue("$display", displayWord);
            cmd.Parameters.AddWithValue("$reading", word.Reading);
            cmd.Parameters.AddWithValue("$pos", word.PartOfSpeech);
            cmd.Parameters.AddWithValue("$def", word.Definition);
            cmd.Parameters.AddWithValue("$hook", word.MemoryHook);
            cmd.Parameters.AddWithValue("$updated", nowStr);
            cmd.Parameters.AddWithValue("$id", word.Id);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            try
            {
                await using var metaCmd = connection.CreateCommand();
                metaCmd.Transaction = transaction;
                metaCmd.CommandText = @"
UPDATE sync_records_meta
SET normalized_word = $normalized,
    display_word = $display,
    updated_at_utc = $updated,
    revision = revision + 1
WHERE word_id = $id;";
                metaCmd.Parameters.AddWithValue("$normalized", normalizedWord);
                metaCmd.Parameters.AddWithValue("$display", displayWord);
                metaCmd.Parameters.AddWithValue("$updated", nowStr);
                metaCmd.Parameters.AddWithValue("$id", word.Id);
                await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await AdvanceSyncVersionVectorsForWordAsync(connection, transaction, word.Id, ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (transaction.Connection != null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateOccurrenceEditsAsync(string id, LearningEdits edits, CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await UpdateOccurrenceEditsCoreAsync(id, edits, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task UpdateOccurrenceEditsCoreAsync(string id, LearningEdits edits, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(edits.Definition)) throw new ArgumentException("释义不能为空。");
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"UPDATE occurrences SET user_edits_json = $edits WHERE id = $id;
UPDATE sync_jobs SET status = 'Pending', attempts = 0, last_error = '', next_attempt_at_utc = NULL, revision = revision + 1 WHERE occurrence_id = $id;";
        cmd.Parameters.AddWithValue("$edits", JsonSerializer.Serialize(edits));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        try
        {
            await using var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = transaction;
            metaCmd.CommandText = @"
UPDATE sync_records_meta
SET updated_at_utc = $now,
    revision = revision + 1
WHERE occurrence_id = $id;";
            metaCmd.Parameters.AddWithValue("$now", nowStr);
            metaCmd.Parameters.AddWithValue("$id", id);
            await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await AdvanceSyncVersionVectorsForOccurrenceAsync(connection, transaction, id, ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteWordAsync(string wordId, CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DeleteWordCoreAsync(wordId, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task DeleteWordCoreAsync(string wordId, CancellationToken ct)
    {
        var nowStr = DateTimeOffset.UtcNow.ToString("o");
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureSyncSnapshotsAsync(connection, wordId, ct, transaction).ConfigureAwait(false);
            try
            {
                await using var metaCmd = connection.CreateCommand();
                metaCmd.Transaction = transaction;
                metaCmd.CommandText = @"
UPDATE sync_records_meta
SET deleted_at_utc = $now,
    updated_at_utc = $now,
    revision = revision + 1
WHERE word_id = $id;";
                metaCmd.Parameters.AddWithValue("$now", nowStr);
                metaCmd.Parameters.AddWithValue("$id", wordId);
                await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await AdvanceSyncVersionVectorsForWordAsync(connection, transaction, wordId, ct).ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "DELETE FROM words WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", wordId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (transaction.Connection != null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
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

    public async Task<int> ImportWordsJsonAsync(string json, CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ImportWordsJsonCoreAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task<int> ImportWordsJsonCoreAsync(string json, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("备份内容为空。", nameof(json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement itemsArray;
        if (root.ValueKind == JsonValueKind.Array)
        {
            itemsArray = root;
        }
        else if (root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "records", out var recordsEl) && recordsEl.ValueKind == JsonValueKind.Array)
        {
            itemsArray = recordsEl;
        }
        else if (root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "history", out var historyEl) && historyEl.ValueKind == JsonValueKind.Array)
        {
            itemsArray = historyEl;
        }
        else if (root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            itemsArray = wordsEl;
        }
        else
        {
            throw new InvalidOperationException("不支持的 JSON 备份结构。");
        }

        var importedCount = 0;

        foreach (var item in itemsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            // 1. 兼容统一协议与浏览器插件导出的 LearningRecord 结构 ({ id, card: { entry: ... }, edits, revision, ... })
            if (TryGetPropertyIgnoreCase(item, "card", out var cardEl) && cardEl.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(item, "deletedAt", out var delEl)
                    && delEl.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                {
                    continue;
                }

                var parsedRec = ParseCanonicalRecord(item, cardEl);
                if (parsedRec == null)
                    continue;

                var existingWordForRec = await FindWordAsync(parsedRec.Value.Translation.Word, ct).ConfigureAwait(false);
                var existingOccsForRec = existingWordForRec != null
                    ? await GetOccurrencesByWordIdAsync(existingWordForRec.Id, ct).ConfigureAwait(false)
                    : Array.Empty<Occurrence>();

                if (existingOccsForRec.Any(o =>
                        string.Equals(o.SelectedText, parsedRec.Value.Capture.SelectedText, StringComparison.Ordinal) &&
                        o.SelectionOffset == parsedRec.Value.Capture.SentenceOffset &&
                        string.Equals(o.Sentence, parsedRec.Value.Capture.Sentence, StringComparison.Ordinal) &&
                        string.Equals(o.SourceUri, parsedRec.Value.Capture.SourceUri, StringComparison.Ordinal)))
                {
                    continue;
                }

                string? validOccId = parsedRec.Value.RecordId != null && IsValidRecordId(parsedRec.Value.RecordId)
                    ? parsedRec.Value.RecordId
                    : null;
                if (validOccId != null && await GetOccurrenceByIdAsync(validOccId, ct).ConfigureAwait(false) != null)
                {
                    validOccId = null;
                }

                await SaveCoreAsync(new SaveCardCommand(
                    parsedRec.Value.Capture,
                    parsedRec.Value.Translation,
                    parsedRec.Value.Edits,
                    UpdateExisting: false,
                    OccurrenceId: validOccId), ct).ConfigureAwait(false);
                importedCount++;
                continue;
            }

            var displayWord = GetStringIgnoreCase(item, "DisplayWord", "word", "Word")?.Trim();
            var definition = GetStringIgnoreCase(item, "Definition", "definition")?.Trim();
            if (string.IsNullOrWhiteSpace(displayWord) || string.IsNullOrWhiteSpace(definition))
                continue;

            var reading = GetStringIgnoreCase(item, "Reading", "reading") ?? string.Empty;
            var pos = GetStringIgnoreCase(item, "PartOfSpeech", "partOfSpeech") ?? string.Empty;
            var memoryHook = GetStringIgnoreCase(item, "MemoryHook", "memoryHook");

            var existingWord = await FindWordAsync(displayWord, ct).ConfigureAwait(false);
            var existingOccurrences = existingWord != null
                ? await GetOccurrencesByWordIdAsync(existingWord.Id, ct).ConfigureAwait(false)
                : Array.Empty<Occurrence>();

            if (TryGetPropertyIgnoreCase(item, "Occurrences", out var occArray) && occArray.ValueKind == JsonValueKind.Array && occArray.GetArrayLength() > 0)
            {
                foreach (var occEl in occArray.EnumerateArray())
                {
                    if (occEl.ValueKind != JsonValueKind.Object)
                        continue;

                    var selectedText = GetStringIgnoreCase(occEl, "SelectedText", "selection") ?? displayWord;
                    var sentence = GetStringIgnoreCase(occEl, "Sentence", "sentence") ?? selectedText;
                    var offset = GetIntIgnoreCase(occEl, "SelectionOffset", "sentenceOffset") ?? -1;
                    var contextTrans = GetStringIgnoreCase(occEl, "ContextTranslation", "contextTranslation") ?? string.Empty;
                    var sourceProc = GetStringIgnoreCase(occEl, "SourceProcess") ?? "Import";
                    var sourceTitle = GetStringIgnoreCase(occEl, "SourceWindowTitle", "pageTitle") ?? string.Empty;
                    var sourceUri = GetStringIgnoreCase(occEl, "SourceUri", "pageUrl") ?? string.Empty;
                    var lookupSourceStr = GetStringIgnoreCase(occEl, "LookupSource", "source");
                    var capturedAt = GetDateTimeIgnoreCase(occEl, "CapturedAtUtc", "timestamp") ?? DateTimeOffset.UtcNow;

                    if (existingOccurrences.Any(o =>
                            string.Equals(o.SelectedText, selectedText, StringComparison.Ordinal) &&
                            o.SelectionOffset == offset &&
                            string.Equals(o.Sentence, sentence, StringComparison.Ordinal) &&
                            string.Equals(o.SourceUri, sourceUri, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var capture = new CaptureResult(
                        selectedText,
                        sourceProc,
                        sourceTitle,
                        new ScreenPoint(0, 0),
                        capturedAt,
                        Sentence: sentence,
                        SentenceOffset: offset,
                        SourceUri: sourceUri);

                    var trans = new TranslationResult(
                        displayWord,
                        reading,
                        pos,
                        definition,
                        contextTrans,
                        memoryHook,
                        ParseLookupSource(lookupSourceStr),
                        GetStringIgnoreCase(occEl, "SourceEntryId"),
                        GetStringIgnoreCase(occEl, "SourceSchemaVersion"));

                    if (TryGetPropertyIgnoreCase(occEl, "LearningResult", out var learningEl)
                        && learningEl.ValueKind == JsonValueKind.Object)
                    {
                        trans = JsonSerializer.Deserialize<TranslationResult>(learningEl.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? trans;
                    }
                    var edits = TryGetPropertyIgnoreCase(occEl, "Edits", out var editsEl)
                        && editsEl.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<LearningEdits>(editsEl.GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
                    await SaveCoreAsync(new SaveCardCommand(capture, trans, edits), ct).ConfigureAwait(false);
                    importedCount++;
                }
            }
            else
            {
                // Browser extension flat history item or word without occurrences
                var selectedText = GetStringIgnoreCase(item, "selection", "SelectedText") ?? displayWord;
                var sentence = GetStringIgnoreCase(item, "sentence", "Sentence") ?? selectedText;
                var offset = GetIntIgnoreCase(item, "sentenceOffset", "SelectionOffset") ?? -1;
                var contextTrans = GetStringIgnoreCase(item, "contextTranslation", "ContextTranslation") ?? string.Empty;
                var sourceTitle = GetStringIgnoreCase(item, "pageTitle", "SourceWindowTitle") ?? "Browser";
                var sourceUri = GetStringIgnoreCase(item, "pageUrl", "SourceUri") ?? string.Empty;
                var lookupSourceStr = GetStringIgnoreCase(item, "source", "LookupSource");
                var capturedAt = GetDateTimeIgnoreCase(item, "timestamp", "CapturedAtUtc") ?? DateTimeOffset.UtcNow;

                if (existingOccurrences.Any(o =>
                        string.Equals(o.SelectedText, selectedText, StringComparison.Ordinal) &&
                        o.SelectionOffset == offset &&
                        string.Equals(o.Sentence, sentence, StringComparison.Ordinal) &&
                        string.Equals(o.SourceUri, sourceUri, StringComparison.Ordinal)))
                {
                    continue;
                }

                var capture = new CaptureResult(
                    selectedText,
                    "BrowserExtension",
                    sourceTitle,
                    new ScreenPoint(0, 0),
                    capturedAt,
                    Sentence: sentence,
                    SentenceOffset: offset,
                    SourceUri: sourceUri);

                var trans = new TranslationResult(
                    displayWord,
                    reading,
                    pos,
                    definition,
                    contextTrans,
                    memoryHook,
                    ParseLookupSource(lookupSourceStr),
                    null,
                    null);

                    await SaveCoreAsync(new SaveCardCommand(capture, trans), ct).ConfigureAwait(false);
                importedCount++;
            }
        }

        return importedCount;
    }

    private sealed record CanonicalParsedRecord(
        string? RecordId,
        int Revision,
        Dictionary<string, long> VersionVector,
        DateTimeOffset SavedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? DeletedAtUtc,
        CaptureResult Capture,
        TranslationResult Translation,
        LearningEdits? Edits,
        Word? WordMeta);

    private static (string? RecordId, CaptureResult Capture, TranslationResult Translation, LearningEdits? Edits)? ParseCanonicalRecord(
        JsonElement item,
        JsonElement cardEl)
    {
        var full = ParseCanonicalRecordFull(item, cardEl);
        if (full == null) return null;
        return (full.RecordId, full.Capture, full.Translation, full.Edits);
    }

    private static CanonicalParsedRecord? ParseCanonicalRecordFull(JsonElement item, JsonElement cardEl)
    {
        if (!TryGetPropertyIgnoreCase(cardEl, "entry", out var entryEl) || entryEl.ValueKind != JsonValueKind.Object)
            return null;

        var word = GetStringIgnoreCase(entryEl, "word", "Word")?.Trim();
        var definition = GetStringIgnoreCase(entryEl, "definition", "Definition")?.Trim();
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(definition))
            return null;

        var reading = GetStringIgnoreCase(entryEl, "reading", "Reading") ?? string.Empty;
        var pos = GetStringIgnoreCase(entryEl, "partOfSpeech", "PartOfSpeech") ?? string.Empty;
        var contextTrans = GetStringIgnoreCase(entryEl, "contextTranslation", "ContextTranslation") ?? string.Empty;
        var memoryHook = GetStringIgnoreCase(entryEl, "memoryHook", "MemoryHook");

        LearningAnalysis? analysis = null;
        var inputKind = GetStringIgnoreCase(entryEl, "inputKind", "InputKind");
        var expression = GetStringIgnoreCase(entryEl, "expression", "Expression");
        var keyExplanation = GetStringIgnoreCase(entryEl, "keyExplanation", "KeyExplanation");
        var usageNote = GetStringIgnoreCase(entryEl, "usageNote", "UsageNote");
        var example = GetStringIgnoreCase(entryEl, "example", "Example");
        var exampleTranslation = GetStringIgnoreCase(entryEl, "exampleTranslation", "ExampleTranslation");
        if (!string.IsNullOrWhiteSpace(inputKind) || !string.IsNullOrWhiteSpace(expression)
            || !string.IsNullOrWhiteSpace(keyExplanation) || !string.IsNullOrWhiteSpace(usageNote)
            || !string.IsNullOrWhiteSpace(example) || !string.IsNullOrWhiteSpace(exampleTranslation))
        {
            analysis = new LearningAnalysis(
                inputKind ?? "word",
                expression ?? word,
                keyExplanation ?? string.Empty,
                usageNote ?? string.Empty,
                example ?? string.Empty,
                exampleTranslation ?? string.Empty);
        }

        GenerationInfo? generation = null;
        if (TryGetPropertyIgnoreCase(entryEl, "generation", out var genEl) && genEl.ValueKind == JsonValueKind.Object)
        {
            var model = GetStringIgnoreCase(genEl, "model", "Model") ?? string.Empty;
            var promptVer = GetStringIgnoreCase(genEl, "promptVersion", "PromptVersion") ?? string.Empty;
            var genAt = GetDateTimeIgnoreCase(genEl, "generatedAt", "GeneratedAtUtc") ?? DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(model))
            {
                generation = new GenerationInfo(model, promptVer, genAt);
            }
        }

        var selection = GetStringIgnoreCase(cardEl, "selection", "SelectedText");
        if (string.IsNullOrWhiteSpace(selection)) selection = word;
        var sentence = GetStringIgnoreCase(cardEl, "sentence", "Sentence");
        if (string.IsNullOrWhiteSpace(sentence)) sentence = selection;
        var offset = GetIntIgnoreCase(cardEl, "sentenceOffset", "SelectionOffset") ?? -1;
        var pageUrl = GetStringIgnoreCase(cardEl, "pageUrl", "SourceUri") ?? string.Empty;
        var pageTitle = GetStringIgnoreCase(cardEl, "pageTitle", "SourceWindowTitle") ?? string.Empty;
        var sourceProcess = GetStringIgnoreCase(cardEl, "sourceProcess", "SourceProcess") ?? "BrowserExtension";
        var lookupSourceStr = GetStringIgnoreCase(cardEl, "lookupSource", "LookupSource")
            ?? (analysis != null ? "Ai" : null);

        var createdAt = GetDateTimeIgnoreCase(cardEl, "createdAt", "CapturedAtUtc")
            ?? GetDateTimeIgnoreCase(item, "savedAt")
            ?? DateTimeOffset.UtcNow;
        var savedAt = GetDateTimeIgnoreCase(item, "savedAt") ?? createdAt;
        var updatedAt = GetDateTimeIgnoreCase(item, "updatedAt") ?? savedAt;
        var deletedAt = GetDateTimeIgnoreCase(item, "deletedAt");
        var revision = Math.Max(1, GetIntIgnoreCase(item, "revision") ?? GetIntIgnoreCase(cardEl, "recordRevision") ?? 1);
        var versionVector = TryGetPropertyIgnoreCase(item, "versionVector", out var vectorEl)
            && SyncVersionVector.TryParse(vectorEl, out var parsedVector)
                ? parsedVector
                : new Dictionary<string, long>(StringComparer.Ordinal);
        var recordId = GetStringIgnoreCase(item, "id", "Id") ?? GetStringIgnoreCase(cardEl, "recordId");

        LearningEdits? edits = null;
        if (TryGetPropertyIgnoreCase(item, "edits", out var editsEl) && editsEl.ValueKind == JsonValueKind.Object)
        {
            var editDef = GetStringIgnoreCase(editsEl, "definition", "Definition");
            var editTrans = GetStringIgnoreCase(editsEl, "contextTranslation", "ContextTranslation");
            var editNote = GetStringIgnoreCase(editsEl, "note", "Note") ?? string.Empty;
            edits = new LearningEdits(editDef, editTrans, editNote);
        }

        Word? wordMeta = null;
        if (TryGetPropertyIgnoreCase(item, "wordMeta", out var wmEl) && wmEl.ValueKind == JsonValueKind.Object)
        {
            var wmDisplay = GetStringIgnoreCase(wmEl, "displayWord", "DisplayWord") ?? word;
            var wmDef = GetStringIgnoreCase(wmEl, "definition", "Definition") ?? definition;
            wordMeta = new Word
            {
                Id = GetStringIgnoreCase(wmEl, "id", "Id") ?? string.Empty,
                NormalizedWord = wmDisplay.Trim().ToLowerInvariant(),
                DisplayWord = wmDisplay.Trim(),
                Reading = GetStringIgnoreCase(wmEl, "reading", "Reading") ?? reading,
                PartOfSpeech = GetStringIgnoreCase(wmEl, "partOfSpeech", "PartOfSpeech") ?? pos,
                Definition = wmDef.Trim(),
                MemoryHook = GetStringIgnoreCase(wmEl, "memoryHook", "MemoryHook") ?? memoryHook ?? string.Empty,
                UpdatedAtUtc = GetDateTimeIgnoreCase(wmEl, "updatedAt", "UpdatedAtUtc") ?? updatedAt
            };
        }

        var capture = new CaptureResult(
            selection!,
            sourceProcess,
            pageTitle,
            new ScreenPoint(0, 0),
            createdAt,
            Sentence: sentence!,
            SentenceOffset: offset,
            SourceUri: pageUrl);

        var translation = new TranslationResult(
            word,
            reading,
            pos,
            definition,
            contextTrans,
            memoryHook,
            ParseLookupSource(lookupSourceStr),
            null,
            analysis != null ? "learning-v1" : null,
            Analysis: analysis,
            Generation: generation);

        return new CanonicalParsedRecord(
            recordId,
            revision,
            versionVector,
            savedAt,
            updatedAt,
            deletedAt,
            capture,
            translation,
            edits,
            wordMeta);
    }

    private static object BuildRecordCardObject(Word word, Occurrence occ, int revision)
    {
        var lr = occ.LearningResult;
        var entryWord = word.DisplayWord;
        var entryReading = !string.IsNullOrWhiteSpace(lr?.Reading) ? lr!.Reading : word.Reading;
        var entryPos = !string.IsNullOrWhiteSpace(lr?.PartOfSpeech) ? lr!.PartOfSpeech : word.PartOfSpeech;
        var entryDef = !string.IsNullOrWhiteSpace(lr?.Definition) ? lr!.Definition : word.Definition;
        var entryTrans = !string.IsNullOrWhiteSpace(lr?.ContextTranslation) ? lr!.ContextTranslation : occ.ContextTranslation;
        var entryHook = !string.IsNullOrWhiteSpace(lr?.MemoryHook) ? lr!.MemoryHook : word.MemoryHook;

        var entryObj = new Dictionary<string, object?>
        {
            ["word"] = entryWord,
            ["reading"] = entryReading ?? string.Empty,
            ["partOfSpeech"] = entryPos ?? string.Empty,
            ["definition"] = entryDef,
            ["contextTranslation"] = entryTrans ?? string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(entryHook))
            entryObj["memoryHook"] = entryHook;

        if (lr?.Analysis is { } analysis)
        {
            if (!string.IsNullOrWhiteSpace(analysis.InputKind))
                entryObj["inputKind"] = analysis.InputKind;
            if (!string.IsNullOrWhiteSpace(analysis.Expression))
                entryObj["expression"] = analysis.Expression;
            if (!string.IsNullOrWhiteSpace(analysis.KeyExplanation))
                entryObj["keyExplanation"] = analysis.KeyExplanation;
            if (!string.IsNullOrWhiteSpace(analysis.UsageNote))
                entryObj["usageNote"] = analysis.UsageNote;
            if (!string.IsNullOrWhiteSpace(analysis.Example))
                entryObj["example"] = analysis.Example;
            if (!string.IsNullOrWhiteSpace(analysis.ExampleTranslation))
                entryObj["exampleTranslation"] = analysis.ExampleTranslation;
        }

        if (lr?.Generation is { } gen && !string.IsNullOrWhiteSpace(gen.Model))
        {
            entryObj["generation"] = new
            {
                model = gen.Model,
                promptVersion = gen.PromptVersion,
                generatedAt = gen.GeneratedAtUtc.ToString("o")
            };
        }

        return new
        {
            recordId = occ.Id,
            recordRevision = revision,
            entry = entryObj,
            selection = occ.SelectedText,
            sentence = occ.Sentence,
            sentenceOffset = occ.SelectionOffset,
            pageUrl = occ.SourceUri,
            pageTitle = occ.SourceWindowTitle,
            sourceProcess = occ.SourceProcess,
            lookupSource = occ.LookupSource,
            createdAt = occ.CapturedAtUtc.ToUnixTimeMilliseconds()
        };
    }

    private static string BuildRecordCardJson(Word word, Occurrence occ)
    {
        return JsonSerializer.Serialize(BuildRecordCardObject(word, occ, 1), new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    public async Task<string> ExportSyncJsonAsync(CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ExportSyncJsonCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    public async Task ResolveSyncConflictAsync(WebDavConflict conflict, bool keepLocal, CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = JsonNode.Parse(await ExportSyncJsonCoreAsync(ct).ConfigureAwait(false))!;
            var current = snapshot["records"]!.AsArray().FirstOrDefault(r => r?["id"]?.GetValue<string>() == conflict.RecordId);
            var expected = JsonNode.Parse(conflict.LocalRecordJson)!;
            var remote = JsonNode.Parse(conflict.RemoteRecordJson)!;
            if (current == null || !JsonNode.DeepEquals(current, expected))
                throw new InvalidOperationException("本地记录在查看冲突后又有修改，请重新同步并查看最新差异。");
            if (remote["id"]?.GetValue<string>() != conflict.RecordId)
                throw new InvalidOperationException("冲突记录标识不一致，请重新同步。");

            string deviceId;
            await using (var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false))
            await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                deviceId = await GetOrCreateSyncDeviceIdAsync(connection, transaction, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            var chosen = (keepLocal ? current : remote).DeepClone();
            var vector = SyncVersionVector.Increment(SyncVersionVector.Merge(
                SyncVersionVector.Parse(current["versionVector"]!.ToJsonString()),
                SyncVersionVector.Parse(remote["versionVector"]!.ToJsonString())), deviceId);
            chosen["versionVector"] = JsonNode.Parse(SyncVersionVector.Serialize(vector));
            var revision = checked(Math.Max(current["revision"]!.GetValue<int>(), remote["revision"]!.GetValue<int>()) + 1);
            chosen["revision"] = revision;
            chosen["card"]!["recordRevision"] = revision;
            var timestamp = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Math.Max(
                current["wordMeta"]?["updatedAt"]?.GetValue<long>() ?? 0,
                remote["wordMeta"]?["updatedAt"]?.GetValue<long>() ?? 0)) + 1;
            chosen["updatedAt"] = timestamp;
            if (chosen["wordMeta"] is JsonObject meta) meta["updatedAt"] = timestamp;
            var envelope = new JsonObject { ["format"] = "word-catcher-learning", ["version"] = 3, ["records"] = new JsonArray(chosen) };
            await MergeSyncJsonCoreAsync(envelope.ToJsonString(), ct).ConfigureAwait(false);
        }
        finally { DatabaseMutationLock.Release(); }
    }

    private async Task<string> ExportSyncJsonCoreAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var deviceId = await GetOrCreateSyncDeviceIdAsync(connection, transaction, ct).ConfigureAwait(false);
        await EnsureSyncVersionVectorsAsync(connection, transaction, deviceId, ct).ConfigureAwait(false);
        var metaMap = new Dictionary<string, (int Revision, DateTimeOffset SavedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt, string SnapshotJson, string VersionVectorJson)>(StringComparer.Ordinal);

        try
        {
            await using var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = transaction;
            metaCmd.CommandText = @"
SELECT occurrence_id, revision, saved_at_utc, updated_at_utc, deleted_at_utc, snapshot_json, version_vector_json
FROM sync_records_meta;";
            await using var reader = await metaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var occId = reader.GetString(0);
                var rev = reader.GetInt32(1);
                var savedAt = DateTimeOffset.Parse(reader.GetString(2));
                var updatedAt = DateTimeOffset.Parse(reader.GetString(3));
                var deletedAt = reader.IsDBNull(4) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(4));
                var snapshot = reader.GetString(5);
                var vector = reader.GetString(6);
                metaMap[occId] = (rev, savedAt, updatedAt, deletedAt, snapshot, vector);
            }
        }
        catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }

        // Read all words (including soft-deleted in trash) so tombstones are exported
        var allWords = new List<(Word Word, DateTimeOffset? DeletedAtUtc)>();
        await using (var wordCmd = connection.CreateCommand())
        {
            wordCmd.Transaction = transaction;
            wordCmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc, deleted_at_utc
FROM words
ORDER BY updated_at_utc DESC;";
            await using var reader = await wordCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var w = ReadWord(reader);
                var del = reader.IsDBNull(10) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(10));
                allWords.Add((w, del));
            }
        }

        var records = new List<object>();
        var exportedOccIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (word, wordDeletedAt) in allWords)
        {
            var occs = await ReadOccurrencesByWordIdAsync(connection, transaction, word.Id, ct).ConfigureAwait(false);
            foreach (var occ in occs)
            {
                exportedOccIds.Add(occ.Id);
                var hasMeta = metaMap.TryGetValue(occ.Id, out var meta);
                var revision = hasMeta ? Math.Max(1, meta.Revision) : 1;
                var versionVector = hasMeta ? SyncVersionVector.Parse(meta.VersionVectorJson) : new Dictionary<string, long> { [deviceId] = 1 };
                var savedAtMs = (hasMeta ? meta.SavedAt : occ.CapturedAtUtc).ToUnixTimeMilliseconds();
                var updatedAtMs = (hasMeta ? meta.UpdatedAt : word.UpdatedAtUtc).ToUnixTimeMilliseconds();
                var effectiveDeletedAt = (hasMeta ? meta.DeletedAt : null) ?? wordDeletedAt;

                object? editsObj = null;
                if (occ.Edits is { } edits)
                {
                    var d = new Dictionary<string, string>();
                    if (edits.Definition != null) d["definition"] = edits.Definition;
                    if (edits.ContextTranslation != null) d["contextTranslation"] = edits.ContextTranslation;
                    if (!string.IsNullOrWhiteSpace(edits.Note)) d["note"] = edits.Note!;
                    editsObj = d;
                }

                var recDict = new Dictionary<string, object?>
                {
                    ["id"] = occ.Id,
                    ["revision"] = revision,
                    ["versionVector"] = versionVector,
                    ["savedAt"] = savedAtMs,
                    ["updatedAt"] = updatedAtMs,
                    ["card"] = BuildRecordCardObject(word, occ, revision),
                    ["wordMeta"] = new
                    {
                        id = word.Id,
                        displayWord = word.DisplayWord,
                        reading = word.Reading,
                        partOfSpeech = word.PartOfSpeech,
                        definition = word.Definition,
                        memoryHook = word.MemoryHook,
                        updatedAt = word.UpdatedAtUtc.ToUnixTimeMilliseconds(),
                        deletedAt = wordDeletedAt?.ToUnixTimeMilliseconds()
                    }
                };
                if (editsObj != null)
                    recDict["edits"] = editsObj;
                if (effectiveDeletedAt.HasValue)
                    recDict["deletedAt"] = effectiveDeletedAt.Value.ToUnixTimeMilliseconds();

                records.Add(recDict);
            }
        }

        // Export any hard-deleted tombstones that remain in sync_records_meta
        foreach (var (occId, meta) in metaMap)
        {
            if (exportedOccIds.Contains(occId) || !meta.DeletedAt.HasValue || string.IsNullOrWhiteSpace(meta.SnapshotJson))
                continue;

            try
            {
                var cardElement = JsonSerializer.Deserialize<JsonElement>(meta.SnapshotJson);
                records.Add(new Dictionary<string, object?>
                {
                    ["id"] = occId,
                    ["revision"] = Math.Max(1, meta.Revision),
                    ["versionVector"] = SyncVersionVector.Parse(meta.VersionVectorJson),
                    ["savedAt"] = meta.SavedAt.ToUnixTimeMilliseconds(),
                    ["updatedAt"] = meta.UpdatedAt.ToUnixTimeMilliseconds(),
                    ["deletedAt"] = meta.DeletedAt.Value.ToUnixTimeMilliseconds(),
                    ["card"] = cardElement
                });
            }
            catch (JsonException) { }
        }

        var envelope = new
        {
            format = "word-catcher-learning",
            version = 3,
            exportedAt = DateTimeOffset.UtcNow.ToString("o"),
            records
        };

        var result = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<(int Added, int Updated, int Removed, int Total, bool NeedsPush)> MergeSyncJsonAsync(
        string json,
        CancellationToken ct = default)
    {
        await DatabaseMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await MergeSyncJsonCoreAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            DatabaseMutationLock.Release();
        }
    }

    private async Task<(int Added, int Updated, int Removed, int Total, bool NeedsPush)> MergeSyncJsonCoreAsync(
        string json,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            var currentCount = (await GetWordsAsync(limit: -1, ct: ct).ConfigureAwait(false)).Count;
            return (0, 0, 0, currentCount, currentCount > 0);
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var incoming = new List<CanonicalParsedRecord>();

        if (root.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(root, "records", out var recordsEl) && recordsEl.ValueKind == JsonValueKind.Array)
        {
            if (!TryGetPropertyIgnoreCase(root, "version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 3
                || !TryGetPropertyIgnoreCase(root, "format", out var format)
                || format.ValueKind != JsonValueKind.String || format.GetString() != "word-catcher-learning")
                throw new InvalidOperationException("远端同步文件格式不受支持，已停止同步以保留远端数据。");
            var incomingIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in recordsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && TryGetPropertyIgnoreCase(item, "card", out var cardEl) && cardEl.ValueKind == JsonValueKind.Object)
                {
                    var parsed = ParseCanonicalRecordFull(item, cardEl);
                    if (parsed == null || !IsValidRecordId(parsed.RecordId)
                        || !incomingIds.Add(parsed.RecordId!)
                        || GetIntIgnoreCase(item, "revision") is not > 0
                        || !TryGetPropertyIgnoreCase(item, "versionVector", out var incomingVectorEl)
                        || !SyncVersionVector.TryParse(incomingVectorEl, out _)
                        || !IsSyncTimestamp(item, "savedAt", required: true)
                        || !IsSyncTimestamp(item, "updatedAt", required: true)
                        || !IsSyncTimestamp(item, "deletedAt", required: false)
                        || string.IsNullOrWhiteSpace(GetStringIgnoreCase(cardEl, "selection"))
                        || string.IsNullOrWhiteSpace(GetStringIgnoreCase(cardEl, "sentence"))
                        || GetStringIgnoreCase(cardEl, "pageUrl") == null
                        || GetIntIgnoreCase(cardEl, "sentenceOffset") == null)
                        throw new InvalidOperationException("远端同步记录缺少有效的表达或释义，已停止同步以保留远端数据。");
                    incoming.Add(parsed);
                }
                else
                    throw new InvalidOperationException("远端同步记录缺少有效的卡片，已停止同步以保留远端数据。");
            }
        }
        else
        {
            throw new InvalidOperationException("不支持的同步 JSON 格式。");
        }

        int addedCount = 0;
        int updatedCount = 0;
        int removedCount = 0;
        bool needsPush = false;
        var seenOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);
        var existingOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var deviceId = await GetOrCreateSyncDeviceIdAsync(connection, transaction, ct).ConfigureAwait(false);
            await EnsureSyncVersionVectorsAsync(connection, transaction, deviceId, ct).ConfigureAwait(false);

            // 在写入前检查整批因果关系和标识冲突，避免部分应用后才发现并发编辑。
            foreach (var rem in incoming)
            {
                var local = await FindLocalSyncStateAsync(connection, rem, ct, transaction).ConfigureAwait(false);
                var localWord = local.OccurrenceId != null && local.WordId != null
                    ? await GetWordByIdAsync(connection, transaction, local.WordId, ct).ConfigureAwait(false)
                    : rem.WordMeta != null
                        ? await FindWordAsync(connection, transaction, GetRemoteDisplayWord(rem), ct).ConfigureAwait(false)
                        : null;

                if (local.OccurrenceId != null)
                {
                    existingOccurrenceIds.Add(local.OccurrenceId);
                    var order = SyncVersionVector.Compare(local.VersionVector, rem.VersionVector);
                    var localDeleted = local.DeletedAt.HasValue;
                    var remoteDeleted = rem.DeletedAtUtc.HasValue;
                    var sameContent = localDeleted == remoteDeleted;
                    if (sameContent && !localDeleted && localWord != null)
                    {
                        var occurrence = await GetOccurrenceByIdAsync(connection, transaction, local.OccurrenceId, ct).ConfigureAwait(false);
                        sameContent = occurrence != null && SameLearningContent(localWord, occurrence, rem);
                    }

                    if (order == VersionVectorOrder.Equal && !sameContent)
                        throw new SyncConflictException(rem.RecordId!, "同一记录在两端具有相同因果版本但内容不同，请选择保留的内容。");
                    if (order == VersionVectorOrder.Concurrent && !sameContent)
                        throw new SyncConflictException(rem.RecordId!, "两端离线期间修改了同一语境，请选择保留的内容。");
                }

                if (localWord != null && rem.WordMeta != null
                    && localWord.UpdatedAtUtc.ToUnixTimeMilliseconds() == rem.WordMeta.UpdatedAtUtc.ToUnixTimeMilliseconds()
                    && !SameWordMeta(localWord, rem.WordMeta))
                    throw new SyncConflictException(rem.RecordId!, "同一词条摘要在两端具有相同时间戳但内容不同，请选择保留的内容。");

                if (local.OccurrenceId != null && localWord != null && rem.WordMeta != null
                    && rem.WordMeta.UpdatedAtUtc.ToUnixTimeMilliseconds() > localWord.UpdatedAtUtc.ToUnixTimeMilliseconds()
                    && !string.Equals(localWord.NormalizedWord, GetRemoteDisplayWord(rem).ToLowerInvariant(), StringComparison.Ordinal))
                {
                    await using var duplicateCheck = connection.CreateCommand();
                    duplicateCheck.Transaction = transaction;
                    duplicateCheck.CommandText = "SELECT id FROM words WHERE normalized_word = $normalized AND id <> $id LIMIT 1;";
                    duplicateCheck.Parameters.AddWithValue("$normalized", GetRemoteDisplayWord(rem).ToLowerInvariant());
                    duplicateCheck.Parameters.AddWithValue("$id", localWord.Id);
                    if (await duplicateCheck.ExecuteScalarAsync(ct).ConfigureAwait(false) is string conflictingWordId)
                        throw new InvalidOperationException($"远端将单词改名为“{GetRemoteDisplayWord(rem)}”，但本地已有同名词条（{conflictingWordId}），已停止合并以保留两条记录。");
                }
            }

            // 先合并已有记录，让远端改名先作用到本地词条，再接纳该词条的新语境。
            foreach (var rem in incoming.OrderByDescending(record => existingOccurrenceIds.Contains(record.RecordId!)))
            {
                var local = await FindLocalSyncStateAsync(connection, rem, ct, transaction).ConfigureAwait(false);
                var localOccId = local.OccurrenceId;
                var localWordId = local.WordId;
                if (localOccId != null) seenOccurrenceIds.Add(localOccId);

                if (localOccId == null)
                {
                    if (rem.DeletedAtUtc.HasValue)
                    {
                        await RecordRemoteTombstoneAsync(connection, transaction, rem, ct).ConfigureAwait(false);
                        seenOccurrenceIds.Add(rem.RecordId!);
                        continue;
                    }

                    var remoteDisplayWord = GetRemoteDisplayWord(rem);
                    var previousWord = await FindWordAsync(connection, transaction, remoteDisplayWord, ct).ConfigureAwait(false);
                    var remoteTranslation = rem.Translation with { Word = remoteDisplayWord };
                    var (savedWord, savedOcc, _) = await SaveCoreAsync(new SaveCardCommand(
                        rem.Capture, remoteTranslation, rem.Edits, UpdateExisting: true,
                        OccurrenceId: rem.RecordId, ReplaceEdits: true), ct, connection, transaction).ConfigureAwait(false);
                    await ApplyRemoteWordMetaAsync(connection, transaction, savedWord.Id, previousWord, rem.WordMeta, ct).ConfigureAwait(false);
                    seenOccurrenceIds.Add(savedOcc.Id);
                    await SyncOverrideMetaTimestampAsync(connection, transaction, savedOcc.Id, rem.Revision,
                        rem.SavedAtUtc, rem.UpdatedAtUtc, null, rem.VersionVector, ct).ConfigureAwait(false);
                    addedCount++;
                    continue;
                }

                var order = SyncVersionVector.Compare(local.VersionVector, rem.VersionVector);
                if (order == VersionVectorOrder.LocalDominates)
                {
                    needsPush = true;
                    continue;
                }
                if (order == VersionVectorOrder.Equal)
                    continue;
                if (order == VersionVectorOrder.Concurrent)
                {
                    var mergedVector = SyncVersionVector.Merge(local.VersionVector, rem.VersionVector);
                    await SyncOverrideMetaTimestampAsync(connection, transaction, localOccId, Math.Max(local.Revision, rem.Revision),
                        rem.SavedAtUtc, local.UpdatedAt >= rem.UpdatedAtUtc ? local.UpdatedAt : rem.UpdatedAtUtc,
                        local.DeletedAt, mergedVector, ct).ConfigureAwait(false);
                    needsPush = true;
                    continue;
                }

                var wasDeleted = local.DeletedAt.HasValue;
                if (rem.DeletedAtUtc.HasValue)
                {
                    if (!wasDeleted && localWordId != null)
                    {
                        await DeleteOccurrenceOrMoveWordToTrashAsync(connection, transaction, localWordId, localOccId, rem, ct).ConfigureAwait(false);
                        removedCount++;
                    }
                    else
                    {
                        await SyncOverrideMetaTimestampAsync(connection, transaction, localOccId, rem.Revision,
                            rem.SavedAtUtc, rem.UpdatedAtUtc, rem.DeletedAtUtc, rem.VersionVector, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    var previousWord = localWordId == null ? null : await GetWordByIdAsync(connection, transaction, localWordId, ct).ConfigureAwait(false);
                    var effectiveDisplayWord = await ApplyRemoteWordMetaAsync(connection, transaction, localWordId ?? string.Empty,
                        previousWord, rem.WordMeta, ct).ConfigureAwait(false);
                    var remoteTranslation = rem.Translation with
                    {
                        Word = string.IsNullOrWhiteSpace(effectiveDisplayWord) ? GetRemoteDisplayWord(rem) : effectiveDisplayWord
                    };
                    var (savedWord, savedOcc, _) = await SaveCoreAsync(new SaveCardCommand(
                        rem.Capture, remoteTranslation, rem.Edits, UpdateExisting: true,
                        OccurrenceId: localOccId, ReplaceEdits: true), ct, connection, transaction).ConfigureAwait(false);
                    await RestoreWordMetaTimestampAsync(connection, transaction, savedWord.Id, previousWord, rem.WordMeta, ct).ConfigureAwait(false);
                    await SyncOverrideMetaTimestampAsync(connection, transaction, savedOcc.Id, rem.Revision,
                        rem.SavedAtUtc, rem.UpdatedAtUtc, null, rem.VersionVector, ct).ConfigureAwait(false);
                    if (wasDeleted) addedCount++;
                    else updatedCount++;
                }
            }

            await using (var checkLocalCmd = connection.CreateCommand())
            {
                checkLocalCmd.Transaction = transaction;
                checkLocalCmd.CommandText = "SELECT id FROM occurrences UNION SELECT occurrence_id FROM sync_records_meta;";
                await using var reader = await checkLocalCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!seenOccurrenceIds.Contains(reader.GetString(0)))
                    {
                        needsPush = true;
                        break;
                    }
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (transaction.Connection != null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var activeWords = await GetWordsAsync(limit: -1, ct: ct).ConfigureAwait(false);
        return (addedCount, updatedCount, removedCount, activeWords.Count, needsPush);
    }

    private static async Task<string?> ApplyRemoteWordMetaAsync(
        SqliteConnection connection, SqliteTransaction transaction, string wordId, Word? local, Word? remote, CancellationToken ct)
    {
        var localUpdatedAt = local?.UpdatedAtUtc;
        if (remote == null || (localUpdatedAt.HasValue &&
            remote.UpdatedAtUtc.ToUnixTimeMilliseconds() <= localUpdatedAt.Value.ToUnixTimeMilliseconds()))
        {
            if (remote != null && local != null
                && remote.UpdatedAtUtc.ToUnixTimeMilliseconds() == localUpdatedAt!.Value.ToUnixTimeMilliseconds()
                && !SameWordMeta(local, remote))
                throw new InvalidOperationException("同一词条摘要在两端具有相同时间戳但内容不同，已停止同步并保留两端内容。");

            if (localUpdatedAt.HasValue)
            {
                await using var restore = connection.CreateCommand();
                restore.Transaction = transaction;
                restore.CommandText = "UPDATE words SET updated_at_utc = $updated WHERE id = $id;";
                restore.Parameters.AddWithValue("$updated", localUpdatedAt.Value.ToString("o"));
                restore.Parameters.AddWithValue("$id", wordId);
                await restore.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return local?.DisplayWord;
        }

        var remoteDisplayWord = remote.DisplayWord.Trim();
        var normalizedWord = remoteDisplayWord.ToLowerInvariant();
        await using (var duplicateCheck = connection.CreateCommand())
        {
            duplicateCheck.Transaction = transaction;
            duplicateCheck.CommandText = "SELECT id FROM words WHERE normalized_word = $normalized AND id <> $id LIMIT 1;";
            duplicateCheck.Parameters.AddWithValue("$normalized", normalizedWord);
            duplicateCheck.Parameters.AddWithValue("$id", wordId);
            if (await duplicateCheck.ExecuteScalarAsync(ct).ConfigureAwait(false) is string conflictingWordId)
                throw new InvalidOperationException($"远端将单词改名为“{remoteDisplayWord}”，但本地已有同名词条（{conflictingWordId}），已停止合并以保留两条记录。");
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
UPDATE words SET normalized_word = $normalized, display_word = $display, reading = $reading, part_of_speech = $pos, definition = $definition,
    memory_hook = $hook, updated_at_utc = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$normalized", normalizedWord);
        cmd.Parameters.AddWithValue("$display", remoteDisplayWord);
        cmd.Parameters.AddWithValue("$reading", remote.Reading);
        cmd.Parameters.AddWithValue("$pos", remote.PartOfSpeech);
        cmd.Parameters.AddWithValue("$definition", remote.Definition);
        cmd.Parameters.AddWithValue("$hook", remote.MemoryHook);
        cmd.Parameters.AddWithValue("$updated", remote.UpdatedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$id", wordId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using (var metaCmd = connection.CreateCommand())
        {
            metaCmd.Transaction = transaction;
            metaCmd.CommandText = @"
UPDATE sync_records_meta
SET normalized_word = $normalized, display_word = $display
WHERE word_id = $id;";
            metaCmd.Parameters.AddWithValue("$normalized", normalizedWord);
            metaCmd.Parameters.AddWithValue("$display", remoteDisplayWord);
            metaCmd.Parameters.AddWithValue("$id", wordId);
            await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return remoteDisplayWord;
    }

    private static async Task RestoreWordMetaTimestampAsync(
        SqliteConnection connection, SqliteTransaction transaction, string wordId, Word? local, Word? remote, CancellationToken ct)
    {
        var updatedAt = local?.UpdatedAtUtc;
        if (remote != null && (!updatedAt.HasValue
            || remote.UpdatedAtUtc.ToUnixTimeMilliseconds() > updatedAt.Value.ToUnixTimeMilliseconds()))
        {
            updatedAt = remote.UpdatedAtUtc;
        }
        if (!updatedAt.HasValue) return;

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE words SET updated_at_utc = $updated WHERE id = $id;";
        cmd.Parameters.AddWithValue("$updated", updatedAt.Value.ToString("o"));
        cmd.Parameters.AddWithValue("$id", wordId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static bool SameWordMeta(Word left, Word right)
        => left.DisplayWord == right.DisplayWord
            && left.Reading == right.Reading
            && left.PartOfSpeech == right.PartOfSpeech
            && left.Definition == right.Definition
            && left.MemoryHook == right.MemoryHook;

    private static string GetRemoteDisplayWord(CanonicalParsedRecord record)
        => string.IsNullOrWhiteSpace(record.WordMeta?.DisplayWord)
            ? record.Translation.Word.Trim()
            : record.WordMeta.DisplayWord.Trim();

    private static bool SameLearningContent(Word word, Occurrence occurrence, CanonicalParsedRecord remote)
    {
        var original = occurrence.LearningResult;
        var local = original is null
            ? new TranslationResult(word.DisplayWord, word.Reading, word.PartOfSpeech, word.Definition,
                occurrence.ContextTranslation, word.MemoryHook, default, null, null)
            : original with
            {
                Word = word.DisplayWord,
                Reading = !string.IsNullOrWhiteSpace(original.Reading) ? original.Reading : word.Reading,
                PartOfSpeech = !string.IsNullOrWhiteSpace(original.PartOfSpeech) ? original.PartOfSpeech : word.PartOfSpeech,
                Definition = !string.IsNullOrWhiteSpace(original.Definition) ? original.Definition : word.Definition,
                ContextTranslation = !string.IsNullOrWhiteSpace(original.ContextTranslation) ? original.ContextTranslation : occurrence.ContextTranslation,
                MemoryHook = !string.IsNullOrWhiteSpace(original.MemoryHook) ? original.MemoryHook : word.MemoryHook
            };
        var other = remote.Translation with { Word = GetRemoteDisplayWord(remote) };
        return local.Word == other.Word && local.Reading == other.Reading
            && local.PartOfSpeech == other.PartOfSpeech && local.Definition == other.Definition
            && local.ContextTranslation == other.ContextTranslation
            && (local.MemoryHook ?? "") == (other.MemoryHook ?? "")
            && local.Analysis == other.Analysis && local.Generation == other.Generation
            && (occurrence.Edits?.Definition) == remote.Edits?.Definition
            && (occurrence.Edits?.ContextTranslation) == remote.Edits?.ContextTranslation
            && (occurrence.Edits?.Note ?? "") == (remote.Edits?.Note ?? "");
    }

    private static async Task<(string? OccurrenceId, string? WordId, int Revision, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt, Dictionary<string, long> VersionVector)> FindLocalSyncStateAsync(
        SqliteConnection connection,
        CanonicalParsedRecord rem,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        // 1. Check sync_records_meta by occurrence_id
        try
        {
            await using var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = transaction;
            metaCmd.CommandText = @"
SELECT occurrence_id, word_id, revision, updated_at_utc, deleted_at_utc, version_vector_json,
       selection, sentence, selection_offset, source_uri
FROM sync_records_meta
WHERE occurrence_id = $id
LIMIT 1;";
            metaCmd.Parameters.AddWithValue("$id", rem.RecordId ?? string.Empty);

            await using var reader = await metaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(6) != rem.Capture.SelectedText
                    || reader.GetString(7) != rem.Capture.Sentence || reader.GetInt32(8) != rem.Capture.SentenceOffset
                    || reader.GetString(9) != rem.Capture.SourceUri)
                    throw new InvalidOperationException("同一同步记录标识对应了不同语境，已停止同步，请检查冲突记录。");
                var occId = reader.GetString(0);
                var wordId = reader.GetString(1);
                var rev = reader.GetInt32(2);
                var updated = DateTimeOffset.Parse(reader.GetString(3));
                var deleted = reader.IsDBNull(4) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(4));
                var vector = SyncVersionVector.Parse(reader.GetString(5));
                return (occId, wordId, rev, updated, deleted, vector);
            }
        }
        catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }

        // 2. Fallback to occurrences + words tables directly
        await using var occCmd = connection.CreateCommand();
        occCmd.Transaction = transaction;
        occCmd.CommandText = @"
SELECT o.id, o.word_id, w.updated_at_utc, w.deleted_at_utc
FROM occurrences o
INNER JOIN words w ON w.id = o.word_id
WHERE o.id = $id
LIMIT 1;";
        occCmd.Parameters.AddWithValue("$id", rem.RecordId ?? string.Empty);

        await using var occReader = await occCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await occReader.ReadAsync(ct).ConfigureAwait(false))
        {
            var occId = occReader.GetString(0);
            var wordId = occReader.GetString(1);
            var updated = DateTimeOffset.Parse(occReader.GetString(2));
            var deleted = occReader.IsDBNull(3) ? (DateTimeOffset?)null : DateTimeOffset.Parse(occReader.GetString(3));
            return (occId, wordId, 1, updated, deleted, new Dictionary<string, long>(StringComparer.Ordinal));
        }

        return (null, null, 0, DateTimeOffset.MinValue, null, new Dictionary<string, long>(StringComparer.Ordinal));
    }

    private static async Task RecordRemoteTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CanonicalParsedRecord rem,
        CancellationToken ct)
    {
        try
        {
            var occId = rem.RecordId!;
            var remoteDisplayWord = GetRemoteDisplayWord(rem);
            var remoteTranslation = rem.Translation with { Word = remoteDisplayWord };
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT OR REPLACE INTO sync_records_meta (
  occurrence_id, word_id, normalized_word, display_word, selection, sentence, selection_offset, source_uri,
  revision, saved_at_utc, updated_at_utc, deleted_at_utc, snapshot_json, version_vector_json
)
VALUES (
  $occId, '', $norm, $display, $sel, $sentence, $offset, $uri,
  $rev, $saved, $updated, $deleted, $snapshot, $vector
);";
            cmd.Parameters.AddWithValue("$occId", occId);
            cmd.Parameters.AddWithValue("$norm", remoteDisplayWord.ToLowerInvariant());
            cmd.Parameters.AddWithValue("$display", remoteDisplayWord);
            cmd.Parameters.AddWithValue("$sel", rem.Capture.SelectedText);
            cmd.Parameters.AddWithValue("$sentence", rem.Capture.Sentence);
            cmd.Parameters.AddWithValue("$offset", rem.Capture.SentenceOffset);
            cmd.Parameters.AddWithValue("$uri", rem.Capture.SourceUri);
            cmd.Parameters.AddWithValue("$rev", rem.Revision);
            cmd.Parameters.AddWithValue("$saved", rem.SavedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$updated", rem.UpdatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$deleted", rem.DeletedAtUtc!.Value.ToString("o"));
            cmd.Parameters.AddWithValue("$snapshot", BuildRecordCardJson(
                new Word { DisplayWord = remoteDisplayWord, Definition = remoteTranslation.Definition },
                new Occurrence
                {
                    Id = occId, LearningResult = remoteTranslation, SelectedText = rem.Capture.SelectedText,
                    Sentence = rem.Capture.Sentence, SelectionOffset = rem.Capture.SentenceOffset,
                    SourceUri = rem.Capture.SourceUri, SourceWindowTitle = rem.Capture.SourceWindowTitle,
                    SourceProcess = rem.Capture.SourceProcess, CapturedAtUtc = rem.Capture.CapturedAt
                }));
            cmd.Parameters.AddWithValue("$vector", SyncVersionVector.Serialize(rem.VersionVector));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }
    }

    private async Task EnsureSyncSnapshotsAsync(
        SqliteConnection connection, string wordId, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        await using var wordCmd = connection.CreateCommand();
        wordCmd.Transaction = transaction;
        wordCmd.CommandText = @"
SELECT id, normalized_word, display_word, language, reading, part_of_speech, definition, memory_hook, created_at_utc, updated_at_utc
FROM words WHERE id = $id;";
        wordCmd.Parameters.AddWithValue("$id", wordId);
        Word? word;
        await using (var reader = await wordCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            word = await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadWord(reader) : null;
        if (word == null) return;

        foreach (var occurrence in await ReadOccurrencesByWordIdAsync(connection, transaction, wordId, ct).ConfigureAwait(false))
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "UPDATE sync_records_meta SET snapshot_json = $snapshot WHERE occurrence_id = $id;";
            cmd.Parameters.AddWithValue("$snapshot", BuildRecordCardJson(word, occurrence));
            cmd.Parameters.AddWithValue("$id", occurrence.Id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task DeleteOccurrenceOrMoveWordToTrashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string wordId,
        string occurrenceId,
        CanonicalParsedRecord remote,
        CancellationToken ct)
    {
        var occs = await ReadOccurrencesByWordIdAsync(connection, transaction, wordId, ct).ConfigureAwait(false);
        await EnsureSyncSnapshotsAsync(connection, wordId, ct, transaction).ConfigureAwait(false);
        if (occs.Count <= 1)
        {
            await using var trash = connection.CreateCommand();
            trash.Transaction = transaction;
            trash.CommandText = "UPDATE words SET deleted_at_utc = $deleted, updated_at_utc = $updated WHERE id = $id;";
            trash.Parameters.AddWithValue("$deleted", remote.DeletedAtUtc!.Value.ToString("o"));
            trash.Parameters.AddWithValue("$updated", remote.UpdatedAtUtc.ToString("o"));
            trash.Parameters.AddWithValue("$id", wordId);
            await trash.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using var delCmd = connection.CreateCommand();
            delCmd.Transaction = transaction;
            delCmd.CommandText = "DELETE FROM occurrences WHERE id = $id;";
            delCmd.Parameters.AddWithValue("$id", occurrenceId);
            await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await SyncOverrideMetaTimestampAsync(connection, transaction, occurrenceId, remote.Revision,
            remote.SavedAtUtc, remote.UpdatedAtUtc, remote.DeletedAtUtc, remote.VersionVector, ct).ConfigureAwait(false);
    }

    private static async Task SyncOverrideMetaTimestampAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string occurrenceId,
        int revision,
        DateTimeOffset savedAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? deletedAtUtc,
        IReadOnlyDictionary<string, long> versionVector,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
UPDATE sync_records_meta
SET revision = $rev,
    saved_at_utc = $saved,
    updated_at_utc = $updated,
    deleted_at_utc = $deleted,
    version_vector_json = $vector
WHERE occurrence_id = $id;";
            cmd.Parameters.AddWithValue("$rev", Math.Max(1, revision));
            cmd.Parameters.AddWithValue("$saved", savedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$updated", updatedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("$deleted", (object?)deletedAtUtc?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$vector", SyncVersionVector.Serialize(versionVector));
            cmd.Parameters.AddWithValue("$id", occurrenceId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsMissingSyncTable(ex)) { }
    }

    private static bool IsMissingSyncTable(SqliteException ex)
        => ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table: sync_records_meta", StringComparison.Ordinal);

    private static bool IsValidRecordId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 128
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsSyncTimestamp(JsonElement item, string property, bool required)
        => !TryGetPropertyIgnoreCase(item, property, out var value) ? !required
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var milliseconds)
                && milliseconds > 0 && milliseconds <= 253402300799999;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetStringIgnoreCase(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
        }
        return null;
    }

    private static int? GetIntIgnoreCase(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
                return val;
        }
        return null;
    }

    private static DateTimeOffset? GetDateTimeIgnoreCase(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyIgnoreCase(element, name, out var prop))
                continue;

            if (prop.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(prop.GetString(), out var dt))
                return dt;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var unixMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        return null;
    }

    private static LookupSource ParseLookupSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return LookupSource.OpenDictionary;

        if (Enum.TryParse<LookupSource>(source, ignoreCase: true, out var parsed))
            return parsed;

        return source.ToLowerInvariant() switch
        {
            "ai" => LookupSource.Ai,
            "mt" => LookupSource.MachineTranslation,
            _ => LookupSource.OpenDictionary
        };
    }

    public async Task<IReadOnlyList<SyncJob>> GetSyncJobsAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT j.id, j.word_id, j.occurrence_id, j.target, j.status, j.attempts, j.last_error, j.next_attempt_at_utc, j.created_at_utc, j.updated_at_utc,
       w.id, w.normalized_word, w.display_word, w.language, w.reading, w.part_of_speech, w.definition, w.memory_hook, w.created_at_utc, w.updated_at_utc,
       o.id, o.word_id, o.selected_text, o.sentence, o.selection_offset, o.context_translation, o.source_process, o.source_window_title, o.source_uri, o.lookup_source, o.source_entry_id, o.source_schema_version, o.captured_at_utc, o.learning_json, o.user_edits_json, j.revision
FROM sync_jobs j
INNER JOIN words w ON j.word_id = w.id
INNER JOIN occurrences o ON j.word_id = o.word_id AND j.occurrence_id = o.id
WHERE w.deleted_at_utc IS NULL
  AND NOT EXISTS (SELECT 1 FROM sync_records_meta m WHERE m.occurrence_id = o.id AND m.deleted_at_utc IS NOT NULL)
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
                Revision = reader.GetInt32(35),
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
                    CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(32)),
                    LearningResult = DeserializeResult(reader.GetString(33)),
                    Edits = JsonSerializer.Deserialize<LearningEdits>(reader.GetString(34))
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
SELECT id, word_id, occurrence_id, target, status, attempts, last_error, next_attempt_at_utc, created_at_utc, updated_at_utc, revision
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
                Revision = reader.GetInt32(10),
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
WHERE id = $id AND revision = $revision;";
        cmd.Parameters.AddWithValue("$revision", job.Revision);
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

    private static TranslationResult? DeserializeResult(string json)
        => json == "" || json == "null" ? null : JsonSerializer.Deserialize<TranslationResult>(json);

    private static async Task<string> FindSyncJobIdAsync(SqliteConnection connection, SqliteTransaction transaction, string occurrenceId, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT id FROM sync_jobs WHERE occurrence_id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", occurrenceId);
        return (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? Guid.NewGuid().ToString("N");
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

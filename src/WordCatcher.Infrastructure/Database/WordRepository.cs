using System;
using System.Collections.Generic;
using System.Linq;
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
        if (string.IsNullOrWhiteSpace(trans.Word) || string.IsNullOrWhiteSpace(trans.Definition))
            throw new ArgumentException("表达和释义不能为空。");

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

            // 同一选区、原句、位置和来源只保存一条；更新必须由用户明确选择。
            string? existingId = null;
            string? existingEdits = null;
            await using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT id, user_edits_json FROM occurrences WHERE word_id = $word AND selected_text = $sel AND sentence = $sentence AND selection_offset = $offset AND source_uri = $uri LIMIT 1;";
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
                await transaction.CommitAsync(ct).ConfigureAwait(false);
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
                if (!Guid.TryParse(command.OccurrenceId, out _))
                    throw new ArgumentException("语境标识不是有效的 GUID。", nameof(command));
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
                Edits = command.Edits ?? preservedEdits,
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
SELECT id, word_id, selected_text, sentence, selection_offset, context_translation, source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc, learning_json, user_edits_json
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
        await using var cmd = connection.CreateCommand();
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

    public async Task UpdateOccurrenceEditsAsync(string id, LearningEdits edits, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(edits.Definition)) throw new ArgumentException("释义不能为空。");
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"UPDATE occurrences SET user_edits_json = $edits WHERE id = $id;
UPDATE sync_jobs SET status = 'Pending', attempts = 0, last_error = '', next_attempt_at_utc = NULL, revision = revision + 1 WHERE occurrence_id = $id;";
        cmd.Parameters.AddWithValue("$edits", JsonSerializer.Serialize(edits));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
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

    public async Task<int> ImportWordsJsonAsync(string json, CancellationToken ct = default)
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
                    await SaveAsync(new SaveCardCommand(capture, trans, edits), ct).ConfigureAwait(false);
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

                await SaveAsync(new SaveCardCommand(capture, trans), ct).ConfigureAwait(false);
                importedCount++;
            }
        }

        return importedCount;
    }

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

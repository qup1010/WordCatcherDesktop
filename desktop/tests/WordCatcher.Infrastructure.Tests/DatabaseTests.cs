using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Database;
using Xunit;

namespace WordCatcher.Infrastructure.Tests;

public class DatabaseTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMigrationRunner _runner;
    private readonly WordRepository _repo;

    public DatabaseTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"wordcatcher_test_{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_tempDbPath);
        _runner = new SqliteMigrationRunner(_factory);
        _repo = new WordRepository(_factory);
    }

    public void Dispose()
    {
        if (File.Exists(_tempDbPath))
        {
            try
            {
                File.Delete(_tempDbPath);
            }
            catch
            {
                // ignore in cleanup
            }
        }
    }

    [Fact]
    public async Task MigrationRunner_CreatesAllTables()
    {
        await _runner.MigrateAsync();
        var words = await _repo.GetWordsAsync();
        Assert.Empty(words);
    }

    [Fact]
    public async Task SaveAsync_CreatesWordOccurrenceAndSyncJob()
    {
        await _runner.MigrateAsync();

        var capture = new CaptureResult(
            "devastated",
            "notepad.exe",
            "Untitled - Notepad",
            new ScreenPoint(100, 200),
            DateTimeOffset.UtcNow,
            Sentence: "The news devastated the entire town.",
            SentenceOffset: 9,
            SourceUri: "https://example.test/article");

        var trans = new TranslationResult(
            "devastate",
            "/ˈdevəsteɪt/",
            "v.",
            "使极度震惊、悲痛",
            "消息让他极为震惊。",
            "词根 de(向下) + vast(空旷/荒废)",
            LookupSource.OpenDictionary,
            "entry_devastate",
            "distribution_entry_v5");

        var cmd = new SaveCardCommand(capture, trans);
        var (word, occurrence, syncJob) = await _repo.SaveAsync(cmd);

        Assert.Equal("devastate", word.NormalizedWord);
        Assert.Equal("devastate", word.DisplayWord);
        Assert.Equal(word.Id, occurrence.WordId);
        Assert.Equal("The news devastated the entire town.", occurrence.Sentence);
        Assert.Equal(9, capture.SentenceOffset);
        Assert.Equal("https://example.test/article", occurrence.SourceUri);
        Assert.Equal(word.Id, syncJob.WordId);
        Assert.Equal(occurrence.Id, syncJob.OccurrenceId);
        Assert.Equal(SyncStatus.Pending, syncJob.Status);

        var allWords = await _repo.GetWordsAsync();
        Assert.Single(allWords);

        var occurrences = await _repo.GetOccurrencesByWordIdAsync(word.Id);
        Assert.Single(occurrences);

        var jobs = await _repo.GetSyncJobsAsync();
        Assert.Single(jobs);
        Assert.Equal(word.Id, jobs[0].WordId);
    }

    [Fact]
    public async Task SaveAsync_SameWord_UpdatesDefinitionAndAppendsOccurrenceWithoutDuplicateWord()
    {
        await _runner.MigrateAsync();

        var capture1 = new CaptureResult("devastated", "notepad.exe", "Note 1", new ScreenPoint(10, 10), DateTimeOffset.UtcNow);
        var trans1 = new TranslationResult("devastate", "/d/", "v.", "释义 1", "语境 1", null, LookupSource.OpenDictionary, "e1", "v5");
        await _repo.SaveAsync(new SaveCardCommand(capture1, trans1));

        var capture2 = new CaptureResult("devastating", "edge.exe", "Article", new ScreenPoint(20, 20), DateTimeOffset.UtcNow);
        var trans2 = new TranslationResult("devastate", "/d/", "v.", "释义 2 (更新)", "语境 2", "线索", LookupSource.Ai, "e2", "v5");
        var (word2, occ2, job2) = await _repo.SaveAsync(new SaveCardCommand(capture2, trans2));

        var allWords = await _repo.GetWordsAsync();
        Assert.Single(allWords);
        Assert.Equal("释义 2 (更新)", allWords[0].Definition);
        Assert.Equal("线索", allWords[0].MemoryHook);

        var occurrences = await _repo.GetOccurrencesByWordIdAsync(word2.Id);
        Assert.Equal(2, occurrences.Count);

        var jobs = await _repo.GetSyncJobsAsync();
        Assert.Equal(2, jobs.Count);
    }

    [Fact]
    public async Task DeleteWordAsync_CascadesOccurrencesAndSyncJobs()
    {
        await _runner.MigrateAsync();

        var capture = new CaptureResult("cat", "notepad.exe", "Win", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        var trans = new TranslationResult("cat", "/kæt/", "n.", "猫", "", null, LookupSource.OpenDictionary, "e_cat", "v5");
        var (word, _, _) = await _repo.SaveAsync(new SaveCardCommand(capture, trans));

        await _repo.DeleteWordAsync(word.Id);

        var allWords = await _repo.GetWordsAsync();
        Assert.Empty(allWords);

        var occurrences = await _repo.GetOccurrencesByWordIdAsync(word.Id);
        Assert.Empty(occurrences);

        var jobs = await _repo.GetSyncJobsAsync();
        Assert.Empty(jobs);
    }

    [Fact]
    public async Task ExportWordsJsonAsync_GeneratesValidJson()
    {
        await _runner.MigrateAsync();

        var capture = new CaptureResult("cat", "notepad.exe", "Win", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        var trans = new TranslationResult("cat", "/kæt/", "n.", "猫", "", null, LookupSource.OpenDictionary, "e_cat", "v5");
        await _repo.SaveAsync(new SaveCardCommand(capture, trans));

        var json = await _repo.ExportWordsJsonAsync();
        Assert.False(string.IsNullOrWhiteSpace(json));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ExportWordsJsonAsync_ExportsOverTenThousandWordsWithAllContexts()
    {
        await _runner.MigrateAsync();
        const int wordCount = 10005;
        await using (var connection = await _factory.CreateConnectionAsync())
        {
            await using var transaction = connection.BeginTransaction();
            await using var seed = connection.CreateCommand();
            seed.Transaction = transaction;
            seed.CommandText = @"
WITH RECURSIVE numbers(n) AS (
    SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < $count
)
INSERT INTO words (id, normalized_word, display_word, language, definition, created_at_utc, updated_at_utc)
SELECT 'word' || n, 'word' || n, 'word' || n, 'en', '释义' || n, $time, $time FROM numbers;

INSERT INTO occurrences (id, word_id, selected_text, sentence, selection_offset, context_translation,
    source_process, source_window_title, source_uri, lookup_source, source_entry_id, source_schema_version, captured_at_utc)
SELECT w.id || '-' || c.n, w.id, w.display_word, 'First ' || w.display_word || ' then ' || w.display_word || '.',
    6, '语境' || c.n, 'reader.exe', 'Test book', 'https://example.test/' || w.id,
    'OpenDictionary', w.id, 'distribution_entry_v5', $time
FROM words w CROSS JOIN (SELECT 1 AS n UNION ALL SELECT 2) c;";
            seed.Parameters.AddWithValue("$count", wordCount);
            seed.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("o"));
            await seed.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        var capture = new CaptureResult("deleted", "reader", "book", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        var translation = new TranslationResult("deleted", "", "", "已删除", "", null, LookupSource.OpenDictionary, null, null);
        var (deletedWord, _, _) = await _repo.SaveAsync(new(capture, translation));
        await _repo.MoveWordToTrashAsync(deletedWord.Id);

        using var document = JsonDocument.Parse(await _repo.ExportWordsJsonAsync());
        Assert.Equal(wordCount, document.RootElement.GetArrayLength());
        var exportedIds = new HashSet<string>();
        foreach (var word in document.RootElement.EnumerateArray())
        {
            var id = word.GetProperty("Id").GetString()!;
            Assert.True(exportedIds.Add(id), $"Duplicate word: {id}");
            Assert.Equal(id, word.GetProperty("NormalizedWord").GetString());
            Assert.Equal("释义" + id[4..], word.GetProperty("Definition").GetString());
            var contexts = word.GetProperty("Occurrences");
            Assert.Equal(2, contexts.GetArrayLength());
            var contextIds = new HashSet<string>();
            foreach (var context in contexts.EnumerateArray())
            {
                var contextId = context.GetProperty("Id").GetString()!;
                Assert.True(contextIds.Add(contextId));
                Assert.Equal(id, context.GetProperty("WordId").GetString());
                Assert.Equal(id, context.GetProperty("SelectedText").GetString());
                Assert.Equal($"First {id} then {id}.", context.GetProperty("Sentence").GetString());
                Assert.Equal(6, context.GetProperty("SelectionOffset").GetInt32());
                Assert.Equal("语境" + contextId[^1], context.GetProperty("ContextTranslation").GetString());
                Assert.Equal("reader.exe", context.GetProperty("SourceProcess").GetString());
                Assert.Equal("Test book", context.GetProperty("SourceWindowTitle").GetString());
                Assert.Equal($"https://example.test/{id}", context.GetProperty("SourceUri").GetString());
                Assert.Equal(id, context.GetProperty("SourceEntryId").GetString());
                Assert.Equal("distribution_entry_v5", context.GetProperty("SourceSchemaVersion").GetString());
            }
            Assert.Contains(id + "-1", contextIds);
            Assert.Contains(id + "-2", contextIds);
        }
        for (var index = 1; index <= wordCount; index++)
            Assert.Contains($"word{index}", exportedIds);
        Assert.DoesNotContain(deletedWord.Id, exportedIds);
    }

    [Fact]
    public async Task UndoRestoresAllContextsAndOriginalSyncState()
    {
        await _runner.MigrateAsync();
        var capture = new CaptureResult("cat", "reader", "book", new ScreenPoint(0, 0), DateTimeOffset.UtcNow,
            Sentence: "The cat crossed the garden.", SentenceOffset: 4);
        var translation = new TranslationResult("cat", "/kæt/", "n.", "猫", "猫穿过花园。", null, LookupSource.OpenDictionary, "cat", "v5");
        var (word, occurrence, job) = await _repo.SaveAsync(new(capture, translation));
        job.Status = SyncStatus.Synced;
        await _repo.UpdateSyncJobAsync(job);
        await _repo.SaveAsync(new(capture with { Sentence = "Another cat." }, translation));

        await _repo.MoveWordToTrashAsync(word.Id);
        Assert.Empty(await _repo.GetWordsAsync());
        Assert.Null(await _repo.FindWordAsync("cat"));
        Assert.Empty(await _repo.GetSyncJobsAsync());
        Assert.Null(await _repo.GetSyncJobByIdAsync(job.Id));
        Assert.Equal("[]", await _repo.ExportWordsJsonAsync());

        await _repo.RestoreWordAsync(word.Id);
        Assert.Equal(word.Id, (await _repo.FindWordAsync(" CAT "))!.Id);
        var contexts = await _repo.GetOccurrencesByWordIdAsync(word.Id);
        Assert.Equal(2, contexts.Count);
        Assert.Contains(contexts, item => item.Id == occurrence.Id && item.SelectionOffset == 4 && item.Sentence == capture.Sentence);
        Assert.Equal(SyncStatus.Synced, (await _repo.GetSyncJobByIdAsync(job.Id))!.Status);
        Assert.Equal(2, (await _repo.GetSyncJobsAsync()).Count);
    }

    [Fact]
    public async Task FilterAndContextSearchApplyBeforePagination()
    {
        await _runner.MigrateAsync();
        for (var index = 0; index < 105; index++)
        {
            var capture = new CaptureResult($"word{index}", "reader", "book", new ScreenPoint(0, 0), DateTimeOffset.UtcNow,
                Sentence: "A shared context needle.");
            var translation = new TranslationResult($"word{index}", "", "", "释义", "", null, LookupSource.OpenDictionary, null, null);
            var (_, _, job) = await _repo.SaveAsync(new(capture, translation));
            if (index % 2 == 0)
            {
                job.Status = SyncStatus.Failed;
                await _repo.UpdateSyncJobAsync(job);
            }
        }
        var first = await _repo.GetFilteredWordsAsync("needle", WordFilter.Failed, 30);
        var second = await _repo.GetFilteredWordsAsync("needle", WordFilter.Failed, 30, 30);
        Assert.Equal(30, first.Count);
        Assert.Equal(23, second.Count);
        Assert.Equal(52, (await _repo.GetFilteredWordsAsync(null, WordFilter.Pending)).Count);
        Assert.Equal(105, (await _repo.GetWordsAsync("needle", 200)).Count);
    }

    [Fact]
    public async Task SavingDeletedWordRestoresIdentityWithoutLosingOldContext()
    {
        await _runner.MigrateAsync();
        var capture = new CaptureResult("cat", "reader", "book", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        var translation = new TranslationResult("cat", "", "n.", "猫", "", null, LookupSource.OpenDictionary, null, null);
        var (word, _, _) = await _repo.SaveAsync(new(capture, translation));
        await _repo.MoveWordToTrashAsync(word.Id);
        var (saved, _, _) = await _repo.SaveAsync(new(capture, translation));
        Assert.Equal(word.Id, saved.Id);
        Assert.Single(await _repo.GetWordsAsync());
        Assert.Equal(2, (await _repo.GetOccurrencesByWordIdAsync(word.Id)).Count);
    }
}

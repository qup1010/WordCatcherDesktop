using System;
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
}

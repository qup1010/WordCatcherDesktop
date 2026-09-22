using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Anki;
using WordCatcher.Infrastructure.Database;
using Xunit;

namespace WordCatcher.Infrastructure.Tests;

public class AnkiClientAndSyncWorkerTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMigrationRunner _runner;
    private readonly WordRepository _repo;

    public AnkiClientAndSyncWorkerTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"anki_test_{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_tempDbPath);
        _runner = new SqliteMigrationRunner(_factory);
        _repo = new WordRepository(_factory);
    }

    public void Dispose()
    {
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public event Action<AppSettings>? SettingsChanged;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(Current);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AnkiConnectClient_AddNote_TreatsDuplicateAsSuccess()
    {
        var handler = new MockHttpMessageHandler(async req =>
        {
            var content = await req.Content!.ReadAsStringAsync();
            if (content.Contains("\"action\":\"deckNames\""))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":[\"Word Catcher\"],\"error\":null}") };
            }
            if (content.Contains("\"action\":\"modelNames\""))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":[\"Word Catcher\"],\"error\":null}") };
            }
            if (content.Contains("\"action\":\"modelFieldNames\""))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":[\"Word\",\"Reading\",\"PartOfSpeech\",\"Definition\",\"MemoryHook\",\"Sentence\",\"SentencePlain\",\"SentenceTranslation\",\"Source\"],\"error\":null}") };
            }
            if (content.Contains("\"action\":\"updateModelTemplates\"") || content.Contains("\"action\":\"updateModelStyling\""))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":null,\"error\":null}") };
            }
            if (content.Contains("\"action\":\"addNote\""))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"result\":null,\"error\":\"cannot create note because it is a duplicate\"}")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":null,\"error\":null}") };
        });

        var client = new HttpClient(handler);
        var settings = new MemorySettingsService();
        var anki = new AnkiConnectClient(client, settings);

        var word = new Word { DisplayWord = "test", Reading = "/test/", Definition = "测试" };
        var occ = new Occurrence { Sentence = "This is a test.", SelectedText = "test" };

        var noteId = await anki.AddNoteAsync(word, occ);
        Assert.Equal(0, noteId); // duplicate returns 0 and succeeds
    }

    [Fact]
    public void BuildNoteFields_NormalizesReadingForAnkiTemplate()
    {
        var word = new Word { DisplayWord = "quick", Reading = "/kwɪk/", Definition = "快" };
        var occurrence = new Occurrence { Sentence = "A quick test.", SelectedText = "quick" };

        var fields = AnkiConnectClient.BuildNoteFields(word, occurrence, clozeContext: false);

        Assert.Equal("/kwɪk/", fields["Reading"]);
    }

    [Fact]
    public async Task AnkiSyncWorker_SetsRetryable_WhenAnkiIsOffline()
    {
        await _runner.MigrateAsync();

        // 1. Save a card to repository
        var capture = new CaptureResult("cat", "notepad.exe", "Notes", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        var trans = new TranslationResult("cat", "/kæt/", "n.", "猫", "", null, LookupSource.OpenDictionary, "e1", "v5");
        var (word, occ, job) = await _repo.SaveAsync(new SaveCardCommand(capture, trans));

        // 2. Anki client throws connection exception
        var handler = new MockHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler);
        var settings = new MemorySettingsService();
        var anki = new AnkiConnectClient(httpClient, settings);

        using var worker = new AnkiSyncWorker(_repo, anki, settings);
        await worker.SyncNowAsync();

        // 3. Verify job status is Retryable and Attempts incremented
        var updatedJob = await _repo.GetSyncJobByIdAsync(job.Id);
        Assert.NotNull(updatedJob);
        Assert.Equal(SyncStatus.Retryable, updatedJob.Status);
        Assert.Equal(1, updatedJob.Attempts);
        Assert.NotNull(updatedJob.NextAttemptAtUtc);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Anki;
using WordCatcher.Infrastructure.Database;
using WordCatcher.Infrastructure.Translation;

namespace WordCatcher.Infrastructure.Tests;

public sealed class LearningRecordTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"learning-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _factory;
    private readonly WordRepository _repo;
    public LearningRecordTests() { _factory = new(_path); _repo = new(_factory); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (File.Exists(_path)) File.Delete(_path); }
    private async Task Init() => await new SqliteMigrationRunner(_factory).MigrateAsync();
    private static CaptureResult Capture(string sentence = "We sat on the bank.") => new(
        "bank", "reader", "Book", new ScreenPoint(0, 0), DateTimeOffset.UtcNow, sentence, sentence.IndexOf("bank"), "https://example.test");
    private static TranslationResult Result(string definition = "河岸") => new(
        "bank", "/bæŋk/", "n.", definition, "我们坐在河岸边。", null, LookupSource.Ai, null, "learning-v1",
        Analysis: new("word", "bank", "这里描述地理位置。", "", "", ""),
        Generation: new("test-model", "learning-v1", DateTimeOffset.UtcNow));

    [Fact]
    public async Task DifferentSensesRemainIndependentAndUserWordEditsArePreserved()
    {
        await Init();
        var (word, river, _) = await _repo.SaveAsync(new(Capture(), Result()));
        word.Definition = "我的词条摘要"; await _repo.UpdateWordAsync(word);
        var (_, money, _) = await _repo.SaveAsync(new(Capture("I went to the bank."), Result("银行")));
        Assert.Equal("我的词条摘要", (await _repo.GetWordByIdAsync(word.Id))!.Definition);
        Assert.Equal("河岸", (await _repo.GetOccurrenceByIdAsync(river.Id))!.Definition);
        Assert.Equal("银行", (await _repo.GetOccurrenceByIdAsync(money.Id))!.Definition);
        Assert.Equal("河岸", AnkiConnectClient.BuildNoteFields(word, river, true)["Definition"]);
        Assert.Equal("银行", AnkiConnectClient.BuildNoteFields(word, money, true)["Definition"]);
    }

    [Fact]
    public async Task RepeatSaveRequiresExplicitUpdateAndKeepsEditsAndIdentity()
    {
        await Init();
        var (word, first, job) = await _repo.SaveAsync(new(Capture(), Result(), new("用户释义", null, "个人笔记")));
        var (_, repeat, _) = await _repo.SaveAsync(new(Capture(), Result("新的分析")));
        Assert.Equal(first.Id, repeat.Id);
        Assert.Equal("河岸", repeat.LearningResult!.Definition);
        await _repo.SaveAsync(new(Capture(), Result("新的分析"), UpdateExisting: true));
        var updated = (await _repo.GetOccurrenceByIdAsync(first.Id))!;
        Assert.Equal("新的分析", updated.LearningResult!.Definition);
        Assert.Equal("用户释义", updated.Definition);
        Assert.Equal("个人笔记", updated.Note);
        Assert.Single(await _repo.GetOccurrencesByWordIdAsync(word.Id));
        Assert.Single(await _repo.GetSyncJobsAsync());
        // 旧同步请求完成后不能把新编辑错误标为已同步。
        job.Status = SyncStatus.Synced;
        await _repo.UpdateSyncJobAsync(job);
        Assert.Equal(SyncStatus.Pending, (await _repo.GetSyncJobByIdAsync(job.Id))!.Status);
        using var export = JsonDocument.Parse(await _repo.ExportWordsJsonAsync());
        var occurrence = export.RootElement[0].GetProperty("Occurrences")[0];
        Assert.Equal("用户释义", occurrence.GetProperty("Edits").GetProperty("Definition").GetString());
        Assert.Equal("test-model", occurrence.GetProperty("LearningResult").GetProperty("Generation").GetProperty("Model").GetString());
    }

    [Fact]
    public async Task HistoryDoesNotCreateFavoritesAndKeepsContext()
    {
        await Init();
        var history = new QueryHistory(_factory);
        await history.RecordAsync(Capture(), Result());
        await history.RecordAsync(Capture("I went to the bank."), Result("银行"));
        Assert.Equal(2, (await history.ReadAsync()).Count);
        Assert.Empty(await _repo.GetWordsAsync());
    }

    [Fact]
    public async Task MigrationPreservesLegacyContextAndSyncState()
    {
        await Init();
        var (word, occurrence, job) = await _repo.SaveAsync(new(Capture(), Result()));
        job.Status = SyncStatus.Synced; await _repo.UpdateSyncJobAsync(job);
        await using (var connection = await _factory.CreateConnectionAsync())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"DROP TABLE lookup_history; ALTER TABLE occurrences DROP COLUMN learning_json;
ALTER TABLE occurrences DROP COLUMN user_edits_json; ALTER TABLE sync_jobs DROP COLUMN revision;
UPDATE app_meta SET value = '3' WHERE key = 'schema_version';";
            await cmd.ExecuteNonQueryAsync();
        }
        await Init();
        var restored = (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!;
        Assert.Equal(occurrence.SelectedText, restored.SelectedText);
        Assert.Equal(occurrence.SelectionOffset, restored.SelectionOffset);
        Assert.Equal(word.Definition, restored.Definition);
        Assert.Equal(SyncStatus.Synced, (await _repo.GetSyncJobByIdAsync(job.Id))!.Status);
        await Init();
        Assert.Single(await _repo.GetWordsAsync());
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event Action<AppSettings>? SettingsChanged { add { } remove { } }
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task AnkiUsesContextIdentityToCreateDifferentSensesAndUpdateSameContext()
    {
        var ids = new Dictionary<string, long>();
        var creates = 0; var updates = 0;
        var handler = new Handler(async (request, ct) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var action = doc.RootElement.GetProperty("action").GetString();
            var args = doc.RootElement.GetProperty("params");
            object? result = action switch {
                "deckNames" => new[] { "Word Catcher", "Word Catcher::Context", "Word Catcher::Recognition", "Word Catcher::Production", "Word Catcher::Listening" },
                "modelNames" => new[] { "Word Catcher" },
                "modelFieldNames" => new[] { "Word", "Reading", "PartOfSpeech", "Definition", "MemoryHook", "Sentence", "SentencePlain", "SentenceTranslation", "Source" },
                "findCards" => Array.Empty<long>(),
                _ => null
            };
            if (action == "findNotes")
            {
                var tag = args.GetProperty("query").GetString()!.Split(' ')[0]["tag:".Length..];
                result = ids.TryGetValue(tag, out var id) ? new[] { id } : Array.Empty<long>();
            }
            if (action == "addNote")
            {
                var note = args.GetProperty("note");
                Assert.True(note.GetProperty("options").GetProperty("allowDuplicate").GetBoolean());
                var tag = note.GetProperty("tags")[1].GetString()!;
                result = ids[tag] = ++creates;
            }
            if (action == "updateNoteFields")
            {
                updates++;
                Assert.Equal("河岸", args.GetProperty("note").GetProperty("fields").GetProperty("Definition").GetString());
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { result, error = (string?)null })) };
        });
        var client = new AnkiConnectClient(new HttpClient(handler), new Settings());
        var word = new Word { DisplayWord = "bank", Definition = "全局摘要" };
        var river = new Occurrence { Id = "river", LearningResult = Result(), Sentence = "We sat on the bank.", SelectedText = "bank", SelectionOffset = 14 };
        var money = new Occurrence { Id = "money", LearningResult = Result("银行"), Sentence = "I went to the bank.", SelectedText = "bank" };
        var riverId = await client.AddNoteAsync(word, river);
        await client.AddNoteAsync(word, money);
        Assert.Equal(riverId, await client.AddNoteAsync(word, river));
        Assert.Equal(2, creates); Assert.Equal(1, updates);
    }
    private sealed class Secret : ISecretStore
    {
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult("test");
        public Task SetApiKeyAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => run(request, ct);
    }
    private sealed class Progress(Action<TranslationResult> callback) : IProgress<TranslationResult>
    {
        public void Report(TranslationResult result) => callback(result);
    }

    [Fact]
    public async Task SseReportsUsablePartialContentAndCacheCanBeBypassed()
    {
        var calls = 0;
        var content = JsonSerializer.Serialize(new { word = "bank", reading = "bæŋk", partOfSpeech = "n.",
            definition = "河岸", contextTranslation = "我们坐在河岸边。", inputKind = "word", expression = "bank",
            keyExplanation = "这里描述地理位置。", usageNote = "", example = "", exampleTranslation = "" },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var handler = new Handler(async (request, ct) => {
            calls++;
            var requestBody = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("\"stream\":true", requestBody);
            Assert.Contains("learning", OpenAiTranslationService.PromptVersion);
            var first = content[..(content.IndexOf("河岸", StringComparison.Ordinal) + 1)];
            string Chunk(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n";
            return new(HttpStatusCode.OK) { Content = new StringContent(Chunk(first) + Chunk(content[first.Length..]) + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        });
        var service = new OpenAiTranslationService(new HttpClient(handler), new Settings(), new Secret());
        var partials = new List<TranslationResult>();
        var progress = new Progress(partials.Add);
        var result = await service.TranslateStreamingAsync(Capture(), progress);
        Assert.Contains(partials, p => p.Definition == "河");
        Assert.Equal("河岸", result.Definition);
        Assert.Equal("test-model", Result().Generation!.Model);
        Assert.NotNull(result.Generation);
        await service.TranslateStreamingAsync(Capture(), progress);
        Assert.Equal(1, calls);
        await service.TranslateStreamingAsync(Capture(), progress, bypassCache: true);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelStopsNetworkAndIncompleteOutputIsRejected()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
        var service = new OpenAiTranslationService(new HttpClient(handler), new Settings(), new Secret());
        using var cts = new CancellationTokenSource();
        var pending = service.TranslateStreamingAsync(Capture(), new Progress(_ => { }), ct: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var empty = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{\"word\":\"bank\",\"definition\":\"\"}" } } } });
        Assert.Throws<InvalidOperationException>(() => OpenAiTranslationService.ParseChatCompletion(empty, "bank"));
    }
}

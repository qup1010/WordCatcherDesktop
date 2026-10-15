using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;
using WordCatcher.Infrastructure.Database;

namespace WordCatcher.App.Tests;

public sealed class LocalBridgeServerTests
{
    [Fact]
    public async Task BrowserPayloadPreservesLearningSnapshotSourceAndSharedContextIdentity()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bridge-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(path);
            await new SqliteMigrationRunner(factory).MigrateAsync();
            var repository = new WordRepository(factory);
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var prefix = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
            port.Stop();
            using var bridge = new LocalBridgeServer(repository, new SyncQueue(), new WordCollectionEvents(), prefix: prefix);
            bridge.Start();
            using var client = new HttpClient { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(5) };
            var id = Guid.NewGuid().ToString();
            var generatedAt = DateTimeOffset.Parse("2026-10-15T00:00:00Z");
            var payload = new
            {
                word = "bank", selection = "bank", sentence = "We sat on the bank.", sentenceOffset = 14,
                definition = "河岸", contextTranslation = "我们坐在河岸边。", createdAt = 1792022400000L,
                sourceUri = "https://example.test/book", sourceTitle = "Book", recordId = id, updateExisting = true,
                analysis = new { inputKind = "word", expression = "bank", keyExplanation = "地理位置", usageNote = "", example = "", exampleTranslation = "" },
                generation = new { model = "test", promptVersion = "learning-v1", generatedAtUtc = generatedAt },
                edits = new { definition = "我的释义", note = "个人笔记" }
            };
            for (var i = 0; i < 2; i++)
            {
                using var response = await client.PostAsync("api/v1/words",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                response.EnsureSuccessStatusCode();
            }
            var word = (await repository.FindWordAsync("bank"))!;
            var occurrence = Assert.Single(await repository.GetOccurrencesByWordIdAsync(word.Id));
            Assert.Equal(id, occurrence.Id);
            Assert.Equal(payload.sourceUri, occurrence.SourceUri);
            Assert.Equal("Book", occurrence.SourceWindowTitle);
            Assert.Equal(payload.createdAt, occurrence.CapturedAtUtc.ToUnixTimeMilliseconds());
            Assert.Equal("河岸", occurrence.LearningResult!.Definition);
            Assert.Equal("我的释义", occurrence.Definition);
            Assert.Equal("个人笔记", occurrence.Note);
            Assert.Equal("地理位置", occurrence.KeyExplanation);
            Assert.Equal(generatedAt, occurrence.LearningResult.Generation!.GeneratedAtUtc);
            Assert.Single(await repository.GetSyncJobsAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }

    private sealed class SyncQueue : IAnkiSyncQueue
    {
        public void Enqueue(string syncJobId) { }
        public void TriggerSync() { }
        public Task SyncNowAsync() => Task.CompletedTask;
    }
}

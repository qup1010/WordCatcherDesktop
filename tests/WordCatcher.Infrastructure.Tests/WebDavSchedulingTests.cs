using System.Net;
using WordCatcher.Infrastructure.Database;
using WordCatcher.Infrastructure.Settings;
using WordCatcher.Infrastructure.Sync;

namespace WordCatcher.Infrastructure.Tests;

public sealed class WebDavSchedulingTests
{
    [Theory]
    [InlineData("enabled")]
    [InlineData("automatic")]
    [InlineData("target")]
    public async Task QueuedAutomaticSyncDoesNotSendRequestsAfterConfigurationChanges(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dav-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new JsonSettingsService(Path.Combine(directory, "settings.json"));
            settings.Current.WebDav = new() { Enabled = true, AutoSync = true, Url = "https://example.test/dav" };
            var factory = new SqliteConnectionFactory(Path.Combine(directory, "words.db"));
            await new SqliteMigrationRunner(factory).MigrateAsync();
            var handler = new Handler();
            using var http = new HttpClient(handler);
            using var sync = new WebDavSyncService(http, new WordRepository(factory), settings, null!);
            sync.ScheduleAutoSync();
            if (change == "enabled") settings.Current.WebDav.Enabled = false;
            else if (change == "automatic") settings.Current.WebDav.AutoSync = false;
            else settings.Current.WebDav.Url = "https://other.test/dav";
            await settings.SaveSettingsAsync(settings.Current);
            await Task.Delay(3200);
            Assert.Equal(0, handler.Requests);
            Assert.Null(sync.LastSyncedAtUtc);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.Created));
        }
    }
}

using Microsoft.Extensions.Logging;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Logging;
using WordCatcher.Infrastructure.Security;
using WordCatcher.Infrastructure.Settings;

namespace WordCatcher.Infrastructure.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PersistenceRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "persistence-" + Guid.NewGuid().ToString("N"));
    public PersistenceRegressionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task SettingsRecoverTheLastValidBackupAndKeepItWhenSavingAfterRecovery()
    {
        var path = Path.Combine(_directory, "settings.json");
        var service = new JsonSettingsService(path);
        await service.SaveSettingsAsync(new() { ExplainLanguage = "English" });
        await service.SaveSettingsAsync(new() { ExplainLanguage = "Deutsch" });
        await File.WriteAllTextAsync(path, "{interrupted");
        service = new JsonSettingsService(path);
        Assert.Equal("English", service.Current.ExplainLanguage);
        await service.SaveSettingsAsync(new() { ExplainLanguage = "日本語" });
        await File.WriteAllTextAsync(path, "{another interruption");
        Assert.Equal("English", (await service.LoadSettingsAsync()).ExplainLanguage);
    }

    [Fact]
    public async Task FailedOrCancelledSaveLeavesSettingsAndSecretsReadable()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new JsonSettingsService(path);
        await settings.SaveSettingsAsync(new() { ExplainLanguage = "English" });
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => settings.SaveSettingsAsync(new() { ExplainLanguage = "Deutsch" }));
        Assert.Equal("English", new JsonSettingsService(path).Current.ExplainLanguage);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings.SaveSettingsAsync(new(), cancelled.Token));
        Assert.Equal("English", settings.Current.ExplainLanguage);
        var secretPath = Path.Combine(_directory, "secrets.dat");
        var secrets = new DpapiSecretService(secretPath);
        await secrets.SetApiKeyAsync("default", "synthetic-key");
        using (var locked = new FileStream(secretPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => secrets.SetApiKeyAsync("default", "replacement"));
        Assert.Equal("synthetic-key", await secrets.GetApiKeyAsync("default"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SecretStoreSerializesUpdatesAndRecoversEncryptedBackup()
    {
        var path = Path.Combine(_directory, "secrets.dat");
        var secrets = new DpapiSecretService(path);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => secrets.SetApiKeyAsync("profile-" + i, "key-" + i)));
        for (var i = 0; i < 8; i++) Assert.Equal("key-" + i, await secrets.GetApiKeyAsync("profile-" + i));
        await secrets.SetApiKeyAsync("extra", "extra-key");
        await File.WriteAllTextAsync(path, "corrupted encrypted content");
        secrets = new DpapiSecretService(path);
        for (var i = 0; i < 8; i++) Assert.Equal("key-" + i, await secrets.GetApiKeyAsync("profile-" + i));
        await secrets.SetApiKeyAsync("recovered", "recovered-key");
        Assert.Equal("recovered-key", await secrets.GetApiKeyAsync("recovered"));
        Assert.DoesNotContain("recovered-key", await File.ReadAllTextAsync(path));
        for (var i = 0; i < 8; i++) await secrets.SetApiKeyAsync("profile-" + i, "");
        await secrets.SetApiKeyAsync("recovered", "");
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void LogSanitizesMessagesOuterExceptionsAndInnerExceptions()
    {
        using var provider = new FileLoggerProvider(_directory);
        provider.CreateLogger("Regression").LogError(
            new InvalidOperationException("Bearer outer-synthetic-token",
                new Exception("api_key=inner-synthetic-key")), "apiKey=message-synthetic-key");
        var log = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.log")));
        Assert.DoesNotContain("outer-synthetic-token", log);
        Assert.DoesNotContain("inner-synthetic-key", log);
        Assert.DoesNotContain("message-synthetic-key", log);
        Assert.Contains("InvalidOperationException", log);
        Assert.Contains("[REDACTED]", log);
    }
}

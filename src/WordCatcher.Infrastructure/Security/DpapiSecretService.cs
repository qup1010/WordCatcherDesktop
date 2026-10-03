using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;

namespace WordCatcher.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretService : IProfileSecretStore
{
    private readonly string _secretsPath;
    private readonly ILogger<DpapiSecretService>? _logger;

    public DpapiSecretService(string? customPath = null, ILogger<DpapiSecretService>? logger = null)
    {
        _logger = logger;
        if (string.IsNullOrEmpty(customPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "WordCatcher");
            Directory.CreateDirectory(dir);
            _secretsPath = Path.Combine(dir, "secrets.dat");
        }
        else
        {
            var dir = Path.GetDirectoryName(customPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _secretsPath = customPath;
        }
    }

    public async Task<string> GetApiKeyAsync(CancellationToken ct = default)
    {
        return await GetApiKeyAsync("default", ct).ConfigureAwait(false);
    }

    public async Task SetApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        await SetApiKeyAsync("default", apiKey, ct).ConfigureAwait(false);
    }

    public async Task<string> GetApiKeyAsync(string profileId, CancellationToken ct = default)
    {
        var secrets = await ReadSecretsAsync(ct).ConfigureAwait(false);
        if (secrets != null)
            return secrets.TryGetValue(profileId, out var key) ? key : string.Empty;

        // Backward compatibility: versions before profile support encrypted
        // one raw string in secrets.dat.
        return profileId == "default" ? await ReadLegacySecretAsync(ct).ConfigureAwait(false) : string.Empty;
    }

    public async Task SetApiKeyAsync(string profileId, string apiKey, CancellationToken ct = default)
    {
        var secrets = await ReadSecretsAsync(ct).ConfigureAwait(false)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (secrets.Count == 0 && File.Exists(_secretsPath))
        {
            var legacy = await ReadLegacySecretAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(legacy))
                secrets["default"] = legacy;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            secrets.Remove(profileId);
        else
            secrets[profileId] = apiKey;

        if (secrets.Count == 0)
        {
            try
            {
                if (File.Exists(_secretsPath)) File.Delete(_secretsPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to remove empty API key store at {Path}", _secretsPath);
            }
            return;
        }

        var serialized = JsonSerializer.Serialize(secrets);
        var encryptedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(serialized),
            null,
            DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_secretsPath, encryptedBytes, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>?> ReadSecretsAsync(CancellationToken ct)
    {
        var plain = await ReadPlainTextAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(plain))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
            return parsed == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> ReadLegacySecretAsync(CancellationToken ct)
    {
        var plain = await ReadPlainTextAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(plain))
            return string.Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
            return parsed != null && parsed.TryGetValue("default", out var key) ? key : string.Empty;
        }
        catch (JsonException)
        {
            return plain;
        }
    }

    private async Task<string> ReadPlainTextAsync(CancellationToken ct)
    {
        if (!File.Exists(_secretsPath))
            return string.Empty;

        try
        {
            var encryptedBytes = await File.ReadAllBytesAsync(_secretsPath, ct).ConfigureAwait(false);
            if (encryptedBytes.Length == 0)
                return string.Empty;

            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to decrypt API Key from {Path}", _secretsPath);
            return string.Empty;
        }
    }
}

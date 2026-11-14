using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Infrastructure.Storage;

namespace WordCatcher.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretService : IProfileSecretStore
{
    private readonly string _secretsPath;
    private readonly ILogger<DpapiSecretService>? _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private bool _recoveredFromBackup;
    private bool _readFailed;

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
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await GetApiKeyCoreAsync(profileId, ct).ConfigureAwait(false); }
        finally { _ioGate.Release(); }
    }

    private async Task<string> GetApiKeyCoreAsync(string profileId, CancellationToken ct)
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
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try { await SetApiKeyCoreAsync(profileId, apiKey, ct).ConfigureAwait(false); }
        finally { _ioGate.Release(); }
    }

    private async Task SetApiKeyCoreAsync(string profileId, string apiKey, CancellationToken ct)
    {
        var secrets = await ReadSecretsAsync(ct).ConfigureAwait(false)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (_readFailed)
            throw new InvalidOperationException("密钥文件与备份均无法读取，已停止保存以保留原文件。");

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
            File.Delete(_secretsPath + ".bak");
            File.Delete(_secretsPath);
            return;
        }

        var serialized = JsonSerializer.Serialize(secrets);
        var encryptedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(serialized),
            null,
            DataProtectionScope.CurrentUser);
        await AtomicFileWriter.WriteBytesAsync(_secretsPath, encryptedBytes, ct, !_recoveredFromBackup).ConfigureAwait(false);
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
        _readFailed = false;
        _recoveredFromBackup = false;
        foreach (var path in new[] { _secretsPath, _secretsPath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var encryptedBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                _recoveredFromBackup = path != _secretsPath;
                _readFailed = false;
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                _readFailed = true;
                _logger?.LogWarning(ex, "Failed to decrypt API Key from {Path}, trying backup", path);
            }
        }
        return string.Empty;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Dictionary;

public sealed class DictionaryInstaller : IDictionaryInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _dictionaryDir;
    private readonly string _targetSqlitePath;
    private readonly string _metaJsonPath;
    private readonly ILogger<DictionaryInstaller>? _logger;
    private readonly Action? _onDictionaryUpdated;

    public DictionaryInstaller(
        HttpClient httpClient,
        string? customDir = null,
        Action? onDictionaryUpdated = null,
        ILogger<DictionaryInstaller>? logger = null)
    {
        _httpClient = httpClient;
        _onDictionaryUpdated = onDictionaryUpdated;
        _logger = logger;

        if (string.IsNullOrEmpty(customDir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _dictionaryDir = Path.Combine(appData, "WordCatcher", "dictionary");
        }
        else
        {
            _dictionaryDir = customDir;
        }

        Directory.CreateDirectory(_dictionaryDir);
        _targetSqlitePath = Path.Combine(_dictionaryDir, "distribution.sqlite");
        _metaJsonPath = Path.Combine(_dictionaryDir, "dictionary-meta.json");
    }

    public string TargetSqlitePath => _targetSqlitePath;
    public string MetaJsonPath => _metaJsonPath;

    public async Task InstallFromUrlAsync(Uri url, IProgress<DictionaryInstallProgress> progress, CancellationToken ct = default)
    {
        var downloadTemp = Path.Combine(_dictionaryDir, $"{Guid.NewGuid():N}.download");
        try
        {
            progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Downloading, 0, null, "正在连接下载地址..."));
            using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;

                await using var sourceStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(downloadTemp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

                var buffer = new byte[65536];
                long totalRead = 0;
                int read;
                while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    totalRead += read;
                    progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Downloading, totalRead, totalBytes, $"已下载 {totalRead / 1024 / 1024.0:F1} MB"));
                }
            }

            await ProcessInstallFileAsync(downloadTemp, url.ToString(), progress, ct).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(downloadTemp))
            {
                try { File.Delete(downloadTemp); } catch { /* ignore */ }
            }
        }
    }

    public async Task InstallFromFileAsync(string path, IProgress<DictionaryInstallProgress> progress, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("指定的词典安装文件不存在", path);

        await ProcessInstallFileAsync(path, $"file://{path}", progress, ct).ConfigureAwait(false);
    }

    public Task RemoveAsync(CancellationToken ct = default)
    {
        // Release the application-owned connection before touching the file.
        _onDictionaryUpdated?.Invoke();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_targetSqlitePath))
        {
            try { File.Delete(_targetSqlitePath); } catch { /* ignore */ }
        }
        if (File.Exists(_metaJsonPath))
        {
            try { File.Delete(_metaJsonPath); } catch { /* ignore */ }
        }
        return Task.CompletedTask;
    }

    private async Task ProcessInstallFileAsync(
        string sourceFilePath,
        string sourceUrl,
        IProgress<DictionaryInstallProgress> progress,
        CancellationToken ct)
    {
        var tempSqlite = Path.Combine(_dictionaryDir, $"{Guid.NewGuid():N}.sqlite.tmp");
        try
        {
            string? sha256Hex = null;

            // 1. Decompress if GZip archive (check magic bytes 0x1F 0x8B or extension)
            var isGz = IsGZipFile(sourceFilePath) || sourceFilePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            if (isGz)
            {
                progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Decompressing, 0, null, "正在解压缩词典文件..."));
                await using var srcFile = File.OpenRead(sourceFilePath);
                await using var gz = new GZipStream(srcFile, CompressionMode.Decompress);
                await using var dstFile = File.Create(tempSqlite);
                await gz.CopyToAsync(dstFile, ct).ConfigureAwait(false);
            }
            else
            {
                progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Decompressing, 0, null, "正在复制临时词典文件..."));
                File.Copy(sourceFilePath, tempSqlite, overwrite: true);
            }

            // Compute SHA256 of the extracted sqlite
            using (var sha = SHA256.Create())
            await using (var stream = File.OpenRead(tempSqlite))
            {
                var hashBytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
                sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            // 2. Verification
            progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Verifying, 0, null, "正在校验词典数据库完整性..."));
            var (entryCount, schemaVer, sqliteVer, headLang, defLang) = await ValidateSqliteAsync(tempSqlite, ct).ConfigureAwait(false);

            // 3. Finalizing (Atomic move)
            progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Finalizing, 0, null, "正在安装更新正式词典..."));
            // Release the service's long-lived read-only connection before
            // replacing the file. ClearAllPools alone does not dispose an
            // application-owned connection on Windows.
            _onDictionaryUpdated?.Invoke();
            SqliteConnection.ClearAllPools();

            File.Move(tempSqlite, _targetSqlitePath, overwrite: true);

            var meta = new DictionaryMetadata
            {
                InstalledAtUtc = DateTimeOffset.UtcNow,
                EntryCount = entryCount,
                SchemaVersion = schemaVer,
                SqliteSchemaVersion = sqliteVer,
                HeadwordLanguage = headLang,
                DefinitionLanguage = defLang,
                SourceUrl = sourceUrl,
                Sha256 = sha256Hex
            };

            var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_metaJsonPath, metaJson, ct).ConfigureAwait(false);

            progress.Report(new DictionaryInstallProgress(DictionaryInstallPhase.Completed, 100, 100, $"安装完成，共 {entryCount:N0} 词条"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempSqlite))
            {
                try { File.Delete(tempSqlite); } catch { /* ignore */ }
            }
        }
    }

    private static async Task<(long EntryCount, string SchemaVer, string SqliteVer, string HeadLang, string DefLang)> ValidateSqliteAsync(
        string sqlitePath,
        CancellationToken ct)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Check integrity
        await using (var integrityCmd = conn.CreateCommand())
        {
            integrityCmd.CommandText = "PRAGMA query_only = ON; PRAGMA integrity_check;";
            var checkRes = await integrityCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (checkRes == null || !string.Equals(checkRes.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"词典数据库完整性校验失败: {checkRes}");
            }
        }

        // Check required tables
        var requiredTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "metadata", "entries", "pos_groups", "meanings", "meaning_examples"
        };
        await using (var tablesCmd = conn.CreateCommand())
        {
            tablesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            await using var reader = await tablesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                requiredTables.Remove(reader.GetString(0));
            }
        }

        if (requiredTables.Count > 0)
        {
            throw new InvalidOperationException($"词典数据库缺少必要数据表: {string.Join(", ", requiredTables)}");
        }

        // Read metadata
        var metaDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "SELECT key, value_json FROM metadata;";
            await using var reader = await metaCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                var rawJson = reader.GetString(1);
                // Parse JSON string
                try
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.String)
                    {
                        metaDict[key] = doc.RootElement.GetString() ?? string.Empty;
                    }
                    else
                    {
                        metaDict[key] = rawJson;
                    }
                }
                catch
                {
                    metaDict[key] = rawJson.Trim('"');
                }
            }
        }

        metaDict.TryGetValue("distribution_schema_version", out var distVer);
        metaDict.TryGetValue("sqlite_schema_version", out var sqlVer);
        metaDict.TryGetValue("headword_language", out var headLang);
        metaDict.TryGetValue("definition_language", out var defLang);

        // Language metadata has appeared both as a JSON string ("en") and
        // as an object ({"code":"en","name":"English"}). Keep only the
        // code because entries.indexes use headword_language_code.
        headLang = ExtractLanguageCode(headLang, "en");
        defLang = ExtractLanguageCode(defLang, "zh-Hans");

        if (!string.Equals(distVer, "distribution_entry_v5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不支持的词典内容契约版本: '{distVer}'，要求 'distribution_entry_v5'");
        }

        if (!string.Equals(sqlVer, "distribution_sqlite_v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不支持的词典包装契约版本: '{sqlVer}'，要求 'distribution_sqlite_v1'");
        }

        // Check entry count
        long count = 0;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM entries;";
            var res = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (res != null)
            {
                count = Convert.ToInt64(res);
            }
        }

        if (count == 0)
        {
            throw new InvalidOperationException("词典数据库中未找到任何词条");
        }

        return (count, distVer ?? "distribution_entry_v5", sqlVer ?? "distribution_sqlite_v1", headLang ?? "en", defLang ?? "zh-Hans");
    }

    private static string ExtractLanguageCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{'))
            return trimmed.Trim('"');

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(code.GetString()))
            {
                return code.GetString()!;
            }
        }
        catch (JsonException)
        {
            // The caller will use the conservative default for malformed
            // optional language metadata after version validation.
        }

        return fallback;
    }

    private static bool IsGZipFile(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < 2) return false;
            return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
        }
        catch
        {
            return false;
        }
    }
}

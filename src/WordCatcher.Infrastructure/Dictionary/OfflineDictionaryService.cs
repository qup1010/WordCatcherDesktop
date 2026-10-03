using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Algorithms;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Dictionary.Models;

namespace WordCatcher.Infrastructure.Dictionary;

public sealed class OfflineDictionaryService : IOfflineDictionaryService, IDisposable
{
    private readonly string _sqlitePath;
    private readonly string _metaJsonPath;
    private readonly ILogger<OfflineDictionaryService>? _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _queryGate = new(1, 1);

    private DictionaryMetadata? _cachedMeta;
    private bool _metaLoaded;
    private SqliteConnection? _readOnlyConnection;

    public OfflineDictionaryService(string? customDir = null, ILogger<OfflineDictionaryService>? logger = null)
    {
        _logger = logger;
        if (string.IsNullOrEmpty(customDir))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "WordCatcher", "dictionary");
            _sqlitePath = Path.Combine(dir, "distribution.sqlite");
            _metaJsonPath = Path.Combine(dir, "dictionary-meta.json");
        }
        else
        {
            _sqlitePath = Path.Combine(customDir, "distribution.sqlite");
            _metaJsonPath = Path.Combine(customDir, "dictionary-meta.json");
        }
    }

    public bool IsInstalled => File.Exists(_sqlitePath) && new FileInfo(_sqlitePath).Length > 0;

    public DictionaryMetadata? Metadata
    {
        get
        {
            lock (_lock)
            {
                if (!_metaLoaded)
                {
                    LoadMetadata();
                }
                return _cachedMeta;
            }
        }
    }

    public void InvalidateConnection()
    {
        // Installation/removal replaces the SQLite file on Windows. Serialize
        // invalidation with the complete lookup so no reader still holds the
        // old file when File.Move/File.Delete is attempted.
        _queryGate.Wait();
        try
        {
            lock (_lock)
            {
                _readOnlyConnection?.Dispose();
                _readOnlyConnection = null;
                _cachedMeta = null;
                _metaLoaded = false;
            }
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            _queryGate.Release();
        }
    }

    private void LoadMetadata()
    {
        _metaLoaded = true;
        if (File.Exists(_metaJsonPath))
        {
            try
            {
                var json = File.ReadAllText(_metaJsonPath);
                _cachedMeta = JsonSerializer.Deserialize<DictionaryMetadata>(json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to load dictionary metadata from {Path}", _metaJsonPath);
                _cachedMeta = null;
            }
        }
        else
        {
            _cachedMeta = null;
        }
    }

    private async Task<SqliteConnection?> GetConnectionAsync(CancellationToken ct)
    {
        if (!IsInstalled)
            return null;

        lock (_lock)
        {
            if (_readOnlyConnection != null)
                return _readOnlyConnection;
        }

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _sqlitePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString();

            var conn = new SqliteConnection(connStr);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA query_only = ON; PRAGMA busy_timeout = 3000;";
                await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            lock (_lock)
            {
                _readOnlyConnection = conn;
                return _readOnlyConnection;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open read-only connection to dictionary {Path}", _sqlitePath);
            return null;
        }
    }

    public async Task<TranslationResult?> LookupAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 2)
            return null; // Long texts go to AI translation

        if (!IsInstalled)
            return null;

        await _queryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
            if (conn == null)
            {
                return null;
            }

            var candidates = new List<string> { trimmed.ToLowerInvariant() };
            foreach (var c in Lemmatizer.GetCandidates(trimmed))
            {
                if (!candidates.Contains(c, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(c);
                }
            }

            var headLang = NormalizeLanguageCode(Metadata?.HeadwordLanguage, "en");

            foreach (var candidate in candidates)
            {
                DistributionEntryV5? entry = null;

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT document_json
FROM entries
WHERE headword_language_code = $lang
  AND normalized_headword = $word
LIMIT 1;";
                    cmd.Parameters.AddWithValue("$lang", headLang);
                    cmd.Parameters.AddWithValue("$word", candidate.ToLowerInvariant());

                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var docJson = reader.GetString(0);
                        entry = JsonSerializer.Deserialize<DistributionEntryV5>(docJson);
                    }
                }

                if (entry != null)
                {
                    return FormatEntry(entry);
                }
            }

            return null;
        }
        finally
        {
            _queryGate.Release();
        }
    }

    public static TranslationResult FormatEntry(DistributionEntryV5 entry)
    {
        // 1. Reading: US > UK > any IPA
        var reading = PickReading(entry);

        // 2. Keep the complete contract; build concise saved definitions per POS.
        var mappedGroups = new List<DictPosGroup>();
        var definitions = new List<string>();
        string primaryPos = string.Empty;

        foreach (var pg in entry.PosGroups)
        {
            var formattedPos = FormatPosLabel(pg.Pos);
            if (pg.Meanings.Count > 0 && string.IsNullOrEmpty(primaryPos))
            {
                primaryPos = formattedPos;
            }

            var dictMeanings = new List<DictMeaning>();
            foreach (var m in pg.Meanings)
            {
                var examples = new List<DictExample>();
                if (m.Examples != null)
                {
                    foreach (var ex in m.Examples)
                    {
                        examples.Add(new DictExample(ex.Text, ex.Translation));
                    }
                }

                dictMeanings.Add(new DictMeaning(
                    m.SenseId ?? string.Empty,
                    m.Priority ?? "common",
                    m.ShortGloss ?? string.Empty,
                    m.LearnerExplanation,
                    examples,
                    m.UsageNote));
            }

            // Preserve ALL senses. Priority controls presentation, never data retention.
            var ordered = dictMeanings.OrderBy(m => m.PriorityRank).ToList();
            var group = new DictPosGroup(formattedPos, pg.Summary, ordered);
            mappedGroups.Add(group);
            var usual = ordered.Where(m => m.PriorityRank < 2).ToList();
            var savedMeanings = usual.Count > 0 ? usual : ordered;
            var glosses = savedMeanings.Select(m => m.Heading)
                .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct();
            var summary = string.Join("；", glosses);
            if (string.IsNullOrWhiteSpace(summary)) summary = pg.Summary;
            if (!string.IsNullOrWhiteSpace(summary))
                definitions.Add(string.IsNullOrEmpty(formattedPos) ? summary : $"{formattedPos} {summary}");
        }

        var combinedDefinition = string.Join("\n", definitions);
        if (string.IsNullOrWhiteSpace(combinedDefinition))
        {
            combinedDefinition = entry.HeadwordSummary ?? entry.Headword;
        }

        return new TranslationResult(
            Word: entry.Headword,
            Reading: reading,
            PartOfSpeech: primaryPos,
            Definition: combinedDefinition,
            ContextTranslation: string.Empty,
            MemoryHook: entry.MemoryHook,
            Source: LookupSource.OpenDictionary,
            SourceEntryId: entry.EntryId,
            SourceSchemaVersion: entry.SchemaVersion ?? "distribution_entry_v5",
            PosGroups: mappedGroups);
    }

    private static string NormalizeLanguageCode(string? value, string fallback)
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
            // Fall back to English for an old or malformed optional meta file.
        }

        return fallback;
    }

    private static string PickReading(DistributionEntryV5 entry)
    {
        string? raw = null;

        // Entry-level pronunciations
        if (entry.Pronunciations != null)
        {
            raw = entry.Pronunciations.Us
                ?? entry.Pronunciations.Uk
                ?? entry.Pronunciations.Ipa;
        }

        // Pos-level pronunciations fallback
        if (string.IsNullOrEmpty(raw))
        {
            foreach (var pg in entry.PosGroups)
            {
                if (pg.Pronunciations != null)
                {
                    raw = pg.Pronunciations.Us ?? pg.Pronunciations.Uk ?? pg.Pronunciations.Ipa;
                    if (!string.IsNullOrEmpty(raw)) break;
                }
            }
        }

        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var trimmed = raw.Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? string.Empty : $"/{trimmed}/";
    }

    public static string FormatPosLabel(string pos)
    {
        var p = pos.Trim().ToLowerInvariant();
        return p switch
        {
            "noun" or "n" => "n.",
            "verb" or "v" => "v.",
            "adjective" or "adj" => "adj.",
            "adverb" or "adv" => "adv.",
            "preposition" or "prep" => "prep.",
            "conjunction" or "conj" => "conj.",
            "pronoun" or "pron" => "pron.",
            "interjection" or "int" => "int.",
            "phrase" or "phr" => "phr.",
            _ => p.EndsWith('.') ? p : $"{p}."
        };
    }

    public void Dispose()
    {
        InvalidateConnection();
    }
}

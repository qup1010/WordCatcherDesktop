using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace WordCatcher.Infrastructure.Database;

public sealed class SqliteMigrationRunner
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly ILogger<SqliteMigrationRunner>? _logger;

    public SqliteMigrationRunner(SqliteConnectionFactory connectionFactory, ILogger<SqliteMigrationRunner>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private static readonly IReadOnlyList<(int Version, string Description, string Sql)> Migrations = new[]
    {
        (
            1,
            "Initial schema",
            @"
CREATE TABLE IF NOT EXISTS app_meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS words (
  id                TEXT PRIMARY KEY,
  normalized_word   TEXT NOT NULL,
  display_word      TEXT NOT NULL,
  language          TEXT NOT NULL DEFAULT '',
  reading           TEXT NOT NULL DEFAULT '',
  part_of_speech    TEXT NOT NULL DEFAULT '',
  definition        TEXT NOT NULL,
  memory_hook       TEXT NOT NULL DEFAULT '',
  created_at_utc    TEXT NOT NULL,
  updated_at_utc    TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_words_identity
ON words(language, normalized_word);

CREATE TABLE IF NOT EXISTS occurrences (
  id                    TEXT PRIMARY KEY,
  word_id               TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
  selected_text         TEXT NOT NULL,
  sentence              TEXT NOT NULL DEFAULT '',
  context_translation   TEXT NOT NULL DEFAULT '',
  source_process        TEXT NOT NULL DEFAULT '',
  source_window_title   TEXT NOT NULL DEFAULT '',
  source_uri            TEXT NOT NULL DEFAULT '',
  lookup_source         TEXT NOT NULL DEFAULT '',
  source_entry_id       TEXT NOT NULL DEFAULT '',
  source_schema_version TEXT NOT NULL DEFAULT '',
  captured_at_utc       TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_occurrences_word_id ON occurrences(word_id);

CREATE TABLE IF NOT EXISTS sync_jobs (
  id                  TEXT PRIMARY KEY,
  word_id             TEXT NOT NULL REFERENCES words(id) ON DELETE CASCADE,
  occurrence_id       TEXT NOT NULL REFERENCES occurrences(id) ON DELETE CASCADE,
  target              TEXT NOT NULL DEFAULT 'anki',
  status              TEXT NOT NULL,
  attempts            INTEGER NOT NULL DEFAULT 0,
  last_error          TEXT NOT NULL DEFAULT '',
  next_attempt_at_utc TEXT NULL,
  created_at_utc      TEXT NOT NULL,
  updated_at_utc      TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_sync_jobs_status ON sync_jobs(status, next_attempt_at_utc);
"
        )
    };

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);

        // Ensure app_meta exists first
        await using (var initMetaCmd = connection.CreateCommand())
        {
            initMetaCmd.CommandText = @"
CREATE TABLE IF NOT EXISTS app_meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);";
            await initMetaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int currentVersion = 0;
        await using (var getVersionCmd = connection.CreateCommand())
        {
            getVersionCmd.CommandText = "SELECT value FROM app_meta WHERE key = 'schema_version';";
            var result = await getVersionCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result != null && int.TryParse(result.ToString(), out var v))
            {
                currentVersion = v;
            }
        }

        foreach (var (version, description, sql) in Migrations)
        {
            if (version > currentVersion)
            {
                _logger?.LogInformation("Applying migration {Version}: {Description}", version, description);
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                try
                {
                    await using (var cmd = connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText = sql;
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await using (var updateCmd = connection.CreateCommand())
                    {
                        updateCmd.Transaction = transaction;
                        updateCmd.CommandText = @"
INSERT INTO app_meta (key, value) VALUES ('schema_version', $v)
ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
                        updateCmd.Parameters.AddWithValue("$v", version.ToString());
                        await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    currentVersion = version;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Migration {Version} failed", version);
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    throw new InvalidOperationException($"Migration {version} failed: {ex.Message}", ex);
                }
            }
        }
    }
}

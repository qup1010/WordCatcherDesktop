using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Database;

public sealed class QueryHistory(SqliteConnectionFactory factory) : IQueryHistory
{
    public async Task RecordAsync(CaptureResult capture, TranslationResult result, CancellationToken ct = default)
    {
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"INSERT INTO lookup_history(id, capture_json, result_json, queried_at_utc) VALUES ($id, $capture, $result, $time)
ON CONFLICT(id) DO UPDATE SET capture_json = excluded.capture_json, result_json = excluded.result_json, queried_at_utc = excluded.queried_at_utc;
DELETE FROM lookup_history WHERE id NOT IN (SELECT id FROM lookup_history ORDER BY queried_at_utc DESC LIMIT 100);";
        cmd.Parameters.AddWithValue("$id", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { capture.SelectedText, capture.Sentence, capture.SentenceOffset, capture.SourceUri })))));
        cmd.Parameters.AddWithValue("$capture", JsonSerializer.Serialize(capture));
        cmd.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result));
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<QueryHistoryItem>> ReadAsync(CancellationToken ct = default)
    {
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT capture_json, result_json FROM lookup_history ORDER BY queried_at_utc DESC LIMIT 100;";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<QueryHistoryItem>();
        while (await reader.ReadAsync(ct))
            list.Add(new(JsonSerializer.Deserialize<CaptureResult>(reader.GetString(0))!,
                JsonSerializer.Deserialize<TranslationResult>(reader.GetString(1))!));
        return list;
    }
}

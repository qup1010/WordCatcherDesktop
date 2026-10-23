using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Database;

namespace WordCatcher.Infrastructure.Tests;

public sealed class SyncRegressionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sync-regression-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _factory;
    private readonly WordRepository _repo;

    public SyncRegressionTests()
    {
        _factory = new(_path);
        _repo = new(_factory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private async Task<(Word Word, Occurrence Occurrence, SyncJob Job)> Save(LearningEdits? edits = null)
    {
        await new SqliteMigrationRunner(_factory).MigrateAsync();
        return await _repo.SaveAsync(new(
            new CaptureResult("bank", "reader", "Book", new(0, 0), DateTimeOffset.UtcNow,
                "We sat on the bank.", 14, "https://example.test"),
            new TranslationResult("bank", "", "n.", "河岸", "我们坐在河岸边。", null, LookupSource.Ai, null, null), edits));
    }

    private async Task<JsonObject> Payload() => JsonNode.Parse(await _repo.ExportSyncJsonAsync())!.AsObject();
    private static JsonObject Record(JsonObject payload) => payload["records"]![0]!.AsObject();
    private static void AdvanceRemoteVector(JsonObject record, string device = "remote-device")
    {
        var vector = record["versionVector"]!.AsObject();
        vector[device] = (vector[device]?.GetValue<long>() ?? 0) + 1;
    }

    [Fact]
    public async Task UndoNewCollectionKeepsTombstoneAndRemovesPendingJob()
    {
        var (word, occurrence, job) = await Save();
        Assert.True(occurrence.WasCreated);
        await _repo.UndoNewOccurrenceAsync(occurrence.Id);
        Assert.Null(await _repo.GetWordByIdAsync(word.Id));
        Assert.Null(await _repo.GetSyncJobByIdAsync(job.Id));
        var record = Record(await Payload());
        Assert.NotNull(record["deletedAt"]);
        Assert.Equal(2, record["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task UndoRejectsLaterEditsAndDuplicateSaveIsNotNew()
    {
        var (_, occurrence, _) = await Save();
        var (_, duplicate, _) = await Save();
        Assert.False(duplicate.WasCreated);
        await _repo.UpdateOccurrenceEditsAsync(occurrence.Id, new("修改后的释义", null, "笔记"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.UndoNewOccurrenceAsync(occurrence.Id));
        Assert.Equal("修改后的释义", (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!.Definition);
    }

    [Fact]
    public async Task UndoOnlyRemovesTheNewContextOfAnExistingWord()
    {
        var (word, first, _) = await Save();
        var (_, second, _) = await _repo.SaveAsync(new(
            new CaptureResult("bank", "reader", "Book", new(0, 0), DateTimeOffset.UtcNow,
                "I went to the bank.", 14, "https://example.test"),
            new TranslationResult("bank", "", "n.", "银行", "我去了银行。", null, LookupSource.Ai, null, null)));
        await _repo.UndoNewOccurrenceAsync(second.Id);
        Assert.NotNull(await _repo.GetWordByIdAsync(word.Id));
        Assert.Equal(first.Id, Assert.Single(await _repo.GetOccurrencesByWordIdAsync(word.Id)).Id);
        Assert.Equal(2, (await Payload())["records"]!.AsArray().Count);
    }

    [Fact]
    public async Task SharedProtocolFixturePreservesIdsEmptyEditsAndDeletionAcrossRoundTrips()
    {
        await new SqliteMigrationRunner(_factory).MigrateAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "webdav-v3.json"));
        Assert.Equal(2, (await _repo.MergeSyncJsonAsync(json)).Added);
        var word = (await _repo.FindWordAsync("bank"))!;
        Assert.Equal(2, (await _repo.GetOccurrencesByWordIdAsync(word.Id)).Count);
        Assert.Equal("", (await _repo.GetOccurrenceByIdAsync("context-two"))!.Translation);
        var payload = await Payload();
        Assert.Equal(3, payload["records"]!.AsArray().Count);
        Assert.Contains(payload["records"]!.AsArray(), r => r!["id"]!.GetValue<string>() == "context-deleted");
        var roundTrip = await _repo.MergeSyncJsonAsync(payload.ToJsonString());
        Assert.False(roundTrip.NeedsPush);
        Assert.Equal(0, roundTrip.Added + roundTrip.Updated + roundTrip.Removed);
    }

    [Fact]
    public async Task SameRevisionConflictingEditsAreKeptOnBothSides()
    {
        var (_, occurrence, _) = await Save(new("本地释义", null, "本地备注"));
        var payload = await Payload();
        Record(payload)["edits"]!["definition"] = "远端独立编辑";
        Record(payload)["updatedAt"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await Assert.ThrowsAsync<SyncConflictException>(() => _repo.MergeSyncJsonAsync(payload.ToJsonString()));
        Assert.Equal("本地释义", (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!.Definition);
    }

    [Fact]
    public async Task ConcurrentOfflineEditsFromSameAncestorAreRejected()
    {
        var (_, occurrence, _) = await Save();
        var remote = await Payload();
        var remoteRecord = Record(remote);
        await _repo.UpdateOccurrenceEditsAsync(occurrence.Id, new("本地离线编辑", null, ""));
        remoteRecord["edits"] = new JsonObject { ["definition"] = "远端离线编辑" };
        AdvanceRemoteVector(remoteRecord);

        await Assert.ThrowsAsync<SyncConflictException>(() => _repo.MergeSyncJsonAsync(remote.ToJsonString()));
        Assert.Equal("本地离线编辑", (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!.Definition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictChoiceMergesBothHistoriesAndSurvivesRemoteReplay(bool keepLocal)
    {
        var (_, occurrence, _) = await Save();
        var remote = await Payload();
        Record(remote)["edits"] = new JsonObject { ["definition"] = "远端编辑", ["note"] = "" };
        AdvanceRemoteVector(Record(remote));
        await _repo.UpdateOccurrenceEditsAsync(occurrence.Id, new("本地编辑", null, ""));
        var local = Record(await Payload());
        await _repo.ResolveSyncConflictAsync(new(occurrence.Id, local.ToJsonString(), Record(remote).ToJsonString()), keepLocal);
        await _repo.MergeSyncJsonAsync(remote.ToJsonString());
        Assert.Equal(keepLocal ? "本地编辑" : "远端编辑", (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!.Definition);
        Assert.Equal(1, Record(await Payload())["versionVector"]!["remote-device"]!.GetValue<long>());
        Assert.False((await _repo.MergeSyncJsonAsync(await _repo.ExportSyncJsonAsync())).NeedsPush);
    }

    [Fact]
    public async Task ConflictChoiceRejectsEditsMadeAfterPreview()
    {
        var (_, occurrence, _) = await Save();
        var local = Record(await Payload());
        var remote = local.DeepClone();
        remote["edits"] = new JsonObject { ["definition"] = "远端编辑" };
        await _repo.UpdateOccurrenceEditsAsync(occurrence.Id, new("刚刚保存", null, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.ResolveSyncConflictAsync(
            new(occurrence.Id, local.ToJsonString(), remote.ToJsonString()), false));
        Assert.Equal("刚刚保存", (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!.Definition);
    }

    [Fact]
    public async Task FailedRecordWriteRollsBackTheEntireDesktopMergeBatch()
    {
        var (_, first, _) = await Save();
        await _repo.SaveAsync(new(
            new CaptureResult("bank", "reader", "Book", new(0, 0), DateTimeOffset.UtcNow,
                "The bank was closed.", 4, "https://example.test"),
            new TranslationResult("bank", "", "n.", "河岸", "银行关门了。", null, LookupSource.Ai, null, null)));
        var payload = await Payload();
        var records = payload["records"]!.AsArray().Select(item => item!.AsObject()).ToList();
        Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            record["edits"] = new JsonObject { ["definition"] = "远端释义 " + record["id"]!.GetValue<string>() };
            AdvanceRemoteVector(record);
        }
        var failingId = records[^1]["id"]!.GetValue<string>();
        await using (var connection = await _factory.CreateConnectionAsync())
        {
            await using var trigger = connection.CreateCommand();
            trigger.CommandText = $"CREATE TRIGGER fail_second_sync_update BEFORE UPDATE ON occurrences WHEN OLD.id = '{failingId}' BEGIN SELECT RAISE(ABORT, 'forced sync failure'); END;";
            await trigger.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => _repo.MergeSyncJsonAsync(payload.ToJsonString()));
        Assert.Equal("河岸", (await _repo.GetOccurrenceByIdAsync(first.Id))!.Definition);
        Assert.Equal("河岸", (await _repo.GetOccurrenceByIdAsync(failingId == first.Id
            ? records[0]["id"]!.GetValue<string>()
            : records[1]["id"]!.GetValue<string>()))!.Definition);
    }

    [Fact]
    public async Task RestoringOneRemoteContextDoesNotResurrectItsDeletedSiblings()
    {
        var (word, first, _) = await Save();
        await _repo.SaveAsync(new(new CaptureResult("bank", "reader", "Book", new(0, 0), DateTimeOffset.UtcNow,
            "I went to the bank.", 14, "https://example.test"),
            new TranslationResult("bank", "", "n.", "银行", "我去了银行。", null, LookupSource.Ai, null, null)));
        await _repo.MoveWordToTrashAsync(word.Id);
        var payload = await Payload();
        var record = payload["records"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == first.Id)!.AsObject();
        payload["records"] = new JsonArray(record.DeepClone());
        record = Record(payload);
        var staleDeletion = payload.DeepClone().ToJsonString();
        var deletedAt = record["deletedAt"]!.GetValue<long>();
        record.Remove("deletedAt");
        record["revision"] = 3;
        record["updatedAt"] = deletedAt;
        AdvanceRemoteVector(record);

        await _repo.MergeSyncJsonAsync(payload.ToJsonString());
        await _repo.MergeSyncJsonAsync(staleDeletion);
        Assert.Equal(first.Id, Assert.Single(await _repo.GetOccurrencesByWordIdAsync(word.Id)).Id);
        Assert.Single(await _repo.GetSyncJobsAsync());
        Assert.Equal(2, (await Payload())["records"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportNormalizesMissingOrNullNote(bool explicitNull)
    {
        await Save();
        var payload = await Payload();
        var record = Record(payload);
        record["id"] = Guid.NewGuid().ToString();
        record["card"]!["sentence"] = "Another bank.";
        var edits = new JsonObject { ["definition"] = "自定义释义" };
        if (explicitNull) edits["note"] = null;
        record["edits"] = edits;

        Assert.Equal(1, await _repo.ImportWordsJsonAsync(payload.ToJsonString()));
        var occurrence = await _repo.GetOccurrenceByIdAsync(record["id"]!.GetValue<string>());
        Assert.Equal(string.Empty, occurrence!.Edits!.Note);
    }

    [Fact]
    public async Task ExportRetainsExplicitlyClearedTranslation()
    {
        await Save(new("自定义释义", "", ""));
        Assert.Equal("", Record(await Payload())["edits"]!["contextTranslation"]!.GetValue<string>());
    }

    [Fact]
    public async Task MergingOwnExportDoesNotRequestAnotherPush()
    {
        await Save();
        var merged = await _repo.MergeSyncJsonAsync(await _repo.ExportSyncJsonAsync());
        Assert.False(merged.NeedsPush);
        Assert.Equal(0, merged.Added + merged.Updated + merged.Removed);
    }

    [Fact]
    public async Task NewerRemoteRecordCanRemoveAllLocalEdits()
    {
        var (_, occurrence, _) = await Save(new("旧释义", "旧译文", "旧备注"));
        var payload = await Payload();
        var record = Record(payload);
        record["revision"] = 2;
        AdvanceRemoteVector(record);
        record.Remove("edits");

        Assert.Equal(1, (await _repo.MergeSyncJsonAsync(payload.ToJsonString())).Updated);
        var updated = (await _repo.GetOccurrenceByIdAsync(occurrence.Id))!;
        Assert.Null(updated.Edits);
        Assert.Equal("河岸", updated.Definition);
        Assert.Equal("", updated.Note);
    }

    [Fact]
    public async Task RemoteWordSummaryUsesTimestampBeforeSavingTheRecord()
    {
        var (word, _, _) = await Save();
        var payload = await Payload();
        var record = Record(payload);
        record["revision"] = 2;
        AdvanceRemoteVector(record);
        var remoteTime = DateTimeOffset.UtcNow.AddDays(-1);
        record["wordMeta"]!["definition"] = "远端编辑的词条摘要";
        record["wordMeta"]!["updatedAt"] = remoteTime.ToUnixTimeMilliseconds();
        await using (var connection = await _factory.CreateConnectionAsync())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE words SET updated_at_utc = $updated;";
            cmd.Parameters.AddWithValue("$updated", remoteTime.AddDays(-1).ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        await _repo.MergeSyncJsonAsync(payload.ToJsonString());
        Assert.Equal("远端编辑的词条摘要", (await _repo.GetWordByIdAsync(word.Id))!.Definition);
        Assert.Equal(remoteTime.ToUnixTimeMilliseconds(),
            (await _repo.GetWordByIdAsync(word.Id))!.UpdatedAtUtc.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task RemoteWordSummaryDoesNotAdvanceSiblingContextVectors()
    {
        var (word, first, _) = await Save();
        var secondCapture = new CaptureResult("bank", "reader", "Book", new(0, 0), DateTimeOffset.UtcNow,
            "I went to the bank.", 14, "https://example.test");
        var (_, second, _) = await _repo.SaveAsync(new(secondCapture,
            new TranslationResult("bank", "", "n.", "银行", "我去了银行。", null, LookupSource.Ai, null, null)));
        var payload = await Payload();
        var firstRecord = payload["records"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == first.Id)!.AsObject();
        var secondRecord = payload["records"]!.AsArray().Single(item => item!["id"]!.GetValue<string>() == second.Id)!.AsObject();
        var remoteTime = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        foreach (var (record, counter) in new[] { (firstRecord, 1L), (secondRecord, 2L) })
        {
            record["versionVector"]!["remote-device"] = counter;
            record["revision"] = (int)counter + 1;
            record["updatedAt"] = remoteTime;
            record["wordMeta"]!["definition"] = "遠端詞條摘要";
            record["wordMeta"]!["updatedAt"] = remoteTime;
        }
        secondRecord["edits"] = new JsonObject { ["definition"] = "远端银行语境义" };
        payload["records"] = new JsonArray(firstRecord.DeepClone(), secondRecord.DeepClone());

        Assert.Equal(2, (await _repo.MergeSyncJsonAsync(payload.ToJsonString())).Updated);
        Assert.Equal("远端银行语境义", (await _repo.GetOccurrenceByIdAsync(second.Id))!.Definition);
        Assert.Equal("遠端詞條摘要", (await _repo.GetWordByIdAsync(word.Id))!.Definition);
    }

    [Fact]
    public async Task UnknownRemoteTombstoneSurvivesExportAndBlocksStaleResurrection()
    {
        var (word, _, _) = await Save();
        var payload = await Payload();
        var record = Record(payload);
        record["id"] = Guid.NewGuid().ToString();
        record["card"]!["sentence"] = "Another bank.";
        record["revision"] = 3;
        var staleActive = payload.DeepClone().ToJsonString();
        record["deletedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        AdvanceRemoteVector(record);
        await _repo.DeleteWordAsync(word.Id);

        await _repo.MergeSyncJsonAsync(payload.ToJsonString());
        var exported = (await Payload())["records"]!.AsArray();
        Assert.Contains(exported, r => r!["id"]!.GetValue<string>() == record["id"]!.GetValue<string>());
        await _repo.MergeSyncJsonAsync(staleActive);
        Assert.Empty(await _repo.GetWordsAsync());
    }

    [Fact]
    public async Task HardDeletingMigratedRecordKeepsAnExportableTombstone()
    {
        var (word, occurrence, _) = await Save();
        await using (var connection = await _factory.CreateConnectionAsync())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE sync_records_meta SET snapshot_json = '';";
            await cmd.ExecuteNonQueryAsync();
        }
        await _repo.MoveWordToTrashAsync(word.Id);
        await _repo.DeleteWordAsync(word.Id);
        var record = Record(await Payload());
        Assert.Equal(occurrence.Id, record["id"]!.GetValue<string>());
        Assert.NotNull(record["deletedAt"]);
        var merged = await _repo.MergeSyncJsonAsync("{\"format\":\"word-catcher-learning\",\"version\":3,\"records\":[]}");
        Assert.True(merged.NeedsPush);
    }

    [Fact]
    public async Task InvalidRemoteRecordStopsMergeBeforeApplyingAnyChanges()
    {
        await Save();
        var payload = await Payload();
        var record = Record(payload);
        record["id"] = Guid.NewGuid().ToString();
        record["card"]!["sentence"] = "Another bank.";
        payload["records"]!.AsArray().Add(new JsonObject { ["card"] = new JsonObject() });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repo.MergeSyncJsonAsync(payload.ToJsonString()));
        var word = (await _repo.FindWordAsync("bank"))!;
        Assert.Single(await _repo.GetOccurrencesByWordIdAsync(word.Id));
    }

    [Fact]
    public async Task SyncMetadataWriteFailureRollsBackTheSave()
    {
        await new SqliteMigrationRunner(_factory).MigrateAsync();
        await using (var connection = await _factory.CreateConnectionAsync())
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE sync_records_meta RENAME COLUMN snapshot_json TO broken_snapshot;";
            await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => Save());
        Assert.Empty(await _repo.GetWordsAsync());
    }

    [Fact]
    public async Task NewerRemoteDeletionUpdatesAnExistingTombstone()
    {
        var (word, _, _) = await Save();
        await _repo.MoveWordToTrashAsync(word.Id);
        var payload = await Payload();
        var record = Record(payload);
        record["revision"] = 10;
        AdvanceRemoteVector(record);
        var deletedAt = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        record["deletedAt"] = deletedAt;
        record["updatedAt"] = deletedAt;
        await _repo.MergeSyncJsonAsync(payload.ToJsonString());
        Assert.Equal(10, Record(await Payload())["revision"]!.GetValue<int>());
        Assert.Equal(deletedAt, Record(await Payload())["deletedAt"]!.GetValue<long>());
    }
}

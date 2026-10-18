using System;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface IWebDavSyncService
{
    DateTimeOffset? LastSyncedAtUtc { get; }
    string LastError { get; }
    event Action? SyncCompleted;
    event Action<string>? SyncFailed;

    Task<(bool Exists, string FileUrl)> TestConnectionAsync(
        string url,
        string remotePath,
        string username,
        string password,
        CancellationToken ct = default);

    Task<WebDavSyncResult> SyncAsync(bool forcePush = false, CancellationToken ct = default);

    void ScheduleAutoSync();
}

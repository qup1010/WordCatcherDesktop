using System;

namespace WordCatcher.Core.Models;

public sealed record WebDavSyncResult(
    int Added,
    int Updated,
    int Removed,
    int Total,
    bool Pushed,
    DateTimeOffset SyncedAtUtc);

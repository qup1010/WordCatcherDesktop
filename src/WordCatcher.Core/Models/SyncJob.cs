using System;
using WordCatcher.Core.Enums;

namespace WordCatcher.Core.Models;

public sealed class SyncJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WordId { get; set; } = string.Empty;
    public string OccurrenceId { get; set; } = string.Empty;
    public string Target { get; set; } = "anki";
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public int Attempts { get; set; }
    public string LastError { get; set; } = string.Empty;
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    // Joined navigation properties for UI display
    public Word? Word { get; set; }
    public Occurrence? Occurrence { get; set; }
}

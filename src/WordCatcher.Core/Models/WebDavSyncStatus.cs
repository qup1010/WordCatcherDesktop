namespace WordCatcher.Core.Models;

public enum WebDavFailureKind { None, Network, Authentication, Conflict, Configuration, Other }

public sealed record WebDavConflict(string RecordId, string LocalRecordJson, string RemoteRecordJson);

public sealed class SyncConflictException(string recordId, string message) : InvalidOperationException(message)
{
    public string RecordId { get; } = recordId;
}

using System;

namespace WordCatcher.Core.Models;

public enum DictionaryInstallPhase
{
    Idle,
    Downloading,
    Decompressing,
    Verifying,
    Finalizing,
    Completed,
    Failed
}

public sealed record DictionaryInstallProgress(
    DictionaryInstallPhase Phase,
    long BytesRead,
    long? TotalBytes,
    string Message);

public sealed class DictionaryMetadata
{
    public DateTimeOffset InstalledAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public long EntryCount { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string SqliteSchemaVersion { get; set; } = string.Empty;
    public string HeadwordLanguage { get; set; } = string.Empty;
    public string DefinitionLanguage { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
}

using System;

namespace WordCatcher.Core.Models;

public sealed class Word
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NormalizedWord { get; set; } = string.Empty;
    public string DisplayWord { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;
    public string DefinitionSummary => string.Join(" ", Definition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public string MemoryHook { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

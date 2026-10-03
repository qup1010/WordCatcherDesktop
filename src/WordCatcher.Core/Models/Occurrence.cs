using System;

namespace WordCatcher.Core.Models;

public sealed class Occurrence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string WordId { get; set; } = string.Empty;
    public string SelectedText { get; set; } = string.Empty;
    public string Sentence { get; set; } = string.Empty;
    public int SelectionOffset { get; set; } = -1;
    public string ContextTranslation { get; set; } = string.Empty;
    public string SourceProcess { get; set; } = string.Empty;
    public string SourceWindowTitle { get; set; } = string.Empty;
    public string SourceUri { get; set; } = string.Empty;
    public string LookupSource { get; set; } = string.Empty;
    public string SourceEntryId { get; set; } = string.Empty;
    public string SourceSchemaVersion { get; set; } = string.Empty;
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

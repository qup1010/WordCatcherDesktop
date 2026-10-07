using System;

namespace WordCatcher.Core.Models;

public sealed class Occurrence
{
    public TranslationResult? LearningResult { get; set; }
    public LearningEdits? Edits { get; set; }
    public string Definition => Edits?.Definition ?? LearningResult?.Definition ?? string.Empty;
    public string Translation => Edits?.ContextTranslation ?? ContextTranslation;
    public string Expression => LearningResult?.Analysis?.Expression ?? LearningResult?.Word ?? SelectedText;
    public string KeyExplanation => LearningResult?.Analysis?.KeyExplanation ?? string.Empty;
    public string UsageNote => LearningResult?.Analysis?.UsageNote ?? string.Empty;
    public string Example => LearningResult?.Analysis?.Example ?? string.Empty;
    public string ExampleTranslation => LearningResult?.Analysis?.ExampleTranslation ?? string.Empty;
    public string Note => Edits?.Note ?? string.Empty;
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

using System;

namespace WordCatcher.Core.Models;

public sealed class Occurrence
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool WasCreated { get; set; }
    public TranslationResult? LearningResult { get; set; }
    public LearningEdits? Edits { get; set; }
    public string Definition => Edits?.Definition ?? LearningResult?.Definition ?? string.Empty;
    [System.Text.Json.Serialization.JsonIgnore] public string WordDefinition { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore] public string AdditionalDefinition =>
        string.Join(" ", Definition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) == string.Join(" ", WordDefinition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) ? string.Empty : Definition;
    [System.Text.Json.Serialization.JsonIgnore] public string DefinitionLabel => Edits?.Definition != null ? "我的释义"
        : LearningResult?.Source switch
        {
            Enums.LookupSource.OpenDictionary => "词典释义",
            Enums.LookupSource.MachineTranslation => "在线翻译",
            Enums.LookupSource.Ai => "AI 释义",
            _ => "收藏释义"
        };
    [System.Text.Json.Serialization.JsonIgnore] public bool HasExplanation =>
        !string.IsNullOrWhiteSpace(KeyExplanation) || !string.IsNullOrWhiteSpace(UsageNote) || !string.IsNullOrWhiteSpace(Example);
    [System.Text.Json.Serialization.JsonIgnore] public string ExplanationLabel => LearningResult?.Source == Enums.LookupSource.Ai ? "AI 原句解析" : "用法与例句";
    public string Translation => Edits?.ContextTranslation ?? ContextTranslation;
    public string Expression => LearningResult?.Analysis?.Expression ?? LearningResult?.Word ?? SelectedText;
    [System.Text.Json.Serialization.JsonIgnore]
    public string SupplementaryExpression => string.Equals(Expression.Trim(), (LearningResult?.Word ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(Expression.Trim(), SelectedText.Trim(), StringComparison.OrdinalIgnoreCase) ? string.Empty : Expression;
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

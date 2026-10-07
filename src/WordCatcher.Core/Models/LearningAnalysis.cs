namespace WordCatcher.Core.Models;

/// <summary>与原始选区分开保存的语境分析。所有解释使用用户的释义语言。</summary>
public sealed record LearningAnalysis(
    string InputKind,
    string Expression,
    string KeyExplanation,
    string UsageNote,
    string Example,
    string ExampleTranslation);

public sealed record GenerationInfo(string Model, string PromptVersion, System.DateTimeOffset GeneratedAtUtc);

public sealed record LearningEdits(string? Definition = null, string? ContextTranslation = null, string Note = "");

using System.Collections.Generic;
using System.Linq;

namespace WordCatcher.Core.Models;

public sealed record DictExample(
    string Text,
    string? Translation);

public sealed record DictMeaning(
    string SenseId,
    string Priority,
    string ShortGloss,
    string? LearnerExplanation,
    IReadOnlyList<DictExample> Examples,
    string? UsageNote = null)
{
    public int PriorityRank => Priority?.Trim().ToLowerInvariant() switch
    {
        "core" => 0,
        "rare" => 2,
        _ => 1
    };
    public string PriorityLabel => PriorityRank switch { 0 => "核心", 2 => "少见", _ => "常用" };
    public int DisplayNumber { get; init; }
    public string DisplayHeading => DisplayNumber > 0 ? $"{DisplayNumber}. {Heading}" : Heading;
    public string? DetailExplanation => string.IsNullOrWhiteSpace(LearnerExplanation)
        || string.Equals(Heading.Trim(), LearnerExplanation.Trim(), System.StringComparison.Ordinal)
        ? null : LearnerExplanation;
    public IReadOnlyList<DictExample> FirstExamples => Examples.Take(1).ToList();
    public IReadOnlyList<DictExample> AdditionalExamples => Examples.Skip(1).ToList();
    public bool HasAdditionalExamples => Examples.Count > 1;
    public string AdditionalExamplesLabel => $"更多例句（{System.Math.Max(0, Examples.Count - 1)}）";
    public string Heading => !string.IsNullOrWhiteSpace(ShortGloss) ? ShortGloss : LearnerExplanation ?? string.Empty;
}

public sealed record DictPosGroup(
    string Pos,
    string? Summary,
    IReadOnlyList<DictMeaning> Meanings,
    bool IsProperName = false)
{
    public string DisplayPos => IsProperName ? $"{Pos} · 专名" : Pos;
    // A quick scan covers every part of speech; detailed senses remain intact.
    public string QuickSummary
    {
        get
        {
            var readable = Meanings.Where(m => !string.IsNullOrWhiteSpace(m.Heading)).ToList();
            var usual = readable.Where(m => m.PriorityRank < 2).ToList();
            var candidates = usual.Count > 0 ? usual : readable;
            var glosses = candidates.OrderBy(m => m.PriorityRank).Select(m => m.Heading)
                .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct().Take(3);
            var summary = string.Join("；", glosses);
            return string.IsNullOrWhiteSpace(summary) ? Summary ?? string.Empty : summary;
        }
    }
}

public sealed record DictPronunciations(
    string? Us,
    string? Uk,
    string? Ipa);

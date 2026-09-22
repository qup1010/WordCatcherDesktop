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
    public string Heading => !string.IsNullOrWhiteSpace(ShortGloss) ? ShortGloss : LearnerExplanation ?? string.Empty;
}

public sealed record DictPosGroup(
    string Pos,
    string? Summary,
    IReadOnlyList<DictMeaning> Meanings)
{
    // A quick scan covers every part of speech; detailed senses remain intact.
    public string QuickSummary
    {
        get
        {
            var usual = Meanings.Where(m => m.PriorityRank < 2).ToList();
            var candidates = usual.Count > 0 ? usual : Meanings;
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

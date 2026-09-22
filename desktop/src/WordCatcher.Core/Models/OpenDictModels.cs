using System.Collections.Generic;

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
    string? UsageNote = null);

public sealed record DictPosGroup(
    string Pos,
    string? Summary,
    IReadOnlyList<DictMeaning> Meanings);

public sealed record DictPronunciations(
    string? Us,
    string? Uk,
    string? Ipa);

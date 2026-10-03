using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WordCatcher.Core.Algorithms;

public static partial class Lemmatizer
{
    private static readonly Dictionary<string, string> IrregularMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["am"] = "be",
        ["is"] = "be",
        ["are"] = "be",
        ["was"] = "be",
        ["were"] = "be",
        ["been"] = "be",
        ["being"] = "be",
        ["has"] = "have",
        ["had"] = "have",
        ["having"] = "have",
        ["does"] = "do",
        ["did"] = "do",
        ["done"] = "do",
        ["doing"] = "do",
        ["went"] = "go",
        ["gone"] = "go",
        ["goes"] = "go",
        ["going"] = "go",
        ["came"] = "come",
        ["comes"] = "come",
        ["coming"] = "come",
        ["took"] = "take",
        ["taken"] = "take",
        ["takes"] = "take",
        ["taking"] = "take",
        ["saw"] = "see",
        ["seen"] = "see",
        ["sees"] = "see",
        ["seeing"] = "see",
        ["got"] = "get",
        ["gotten"] = "get",
        ["gets"] = "get",
        ["getting"] = "get",
        ["knew"] = "know",
        ["known"] = "know",
        ["knows"] = "know",
        ["knowing"] = "know",
        ["made"] = "make",
        ["makes"] = "make",
        ["making"] = "make",
        ["thought"] = "think",
        ["thinks"] = "think",
        ["thinking"] = "think",
        ["told"] = "tell",
        ["tells"] = "tell",
        ["telling"] = "tell",
        ["felt"] = "feel",
        ["feels"] = "feel",
        ["feeling"] = "feel",
        ["found"] = "find",
        ["finds"] = "find",
        ["finding"] = "find",
        ["gave"] = "give",
        ["given"] = "give",
        ["gives"] = "give",
        ["giving"] = "give",
        ["wrote"] = "write",
        ["written"] = "write",
        ["writes"] = "write",
        ["writing"] = "write",
        ["better"] = "good",
        ["best"] = "good",
        ["worse"] = "bad",
        ["worst"] = "bad",
        ["less"] = "little",
        ["least"] = "little",
        ["more"] = "much",
        ["most"] = "much",
        ["children"] = "child",
        ["men"] = "man",
        ["women"] = "woman",
        ["feet"] = "foot",
        ["teeth"] = "tooth",
        ["mice"] = "mouse",
        ["geese"] = "goose",
        ["people"] = "person",
        ["leaves"] = "leaf",
        ["wolves"] = "wolf",
        ["halves"] = "half",
        ["knives"] = "knife",
        ["lives"] = "life",
        ["wives"] = "wife",
        ["shelves"] = "shelf",
        ["thieves"] = "thief",
        ["crises"] = "crisis",
        ["analyses"] = "analysis",
    };

    [GeneratedRegex(@"^[^a-z0-9'-]+|[^a-z0-9'-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex BoundaryNonAlphaRegex();

    public static IReadOnlyList<string> GetCandidates(string rawWord)
    {
        if (string.IsNullOrWhiteSpace(rawWord))
            return Array.Empty<string>();

        var trimmed = rawWord.Trim().ToLowerInvariant();
        var word = BoundaryNonAlphaRegex().Replace(trimmed, string.Empty);

        if (string.IsNullOrEmpty(word) || word.Length < 2)
            return string.IsNullOrEmpty(word) ? Array.Empty<string>() : [word];

        var candidates = new List<string> { word };

        void AddCandidate(string c)
        {
            if (!string.IsNullOrEmpty(c) && c.Length >= 2 && !candidates.Contains(c))
            {
                candidates.Add(c);
            }
        }

        // 1. 不规则变形
        if (IrregularMap.TryGetValue(word, out var irregular))
        {
            AddCandidate(irregular);
        }

        var len = word.Length;

        // 2. 复数 / 第三人称单数 (-ies, -ves, -es, -s)
        if (word.EndsWith("ies", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "y"));
        }
        else if (word.EndsWith("ves", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "f"));
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "fe"));
        }
        else if (word.EndsWith("es", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(word[..^2]);
            AddCandidate(word[..^1]);
        }
        else if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(word[..^1]);
        }

        // 3. 过去式 / 过去分词 (-ied, -ed)
        if (word.EndsWith("ied", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "y"));
        }
        else if (word.EndsWith("ed", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(word[..^1]);
            AddCandidate(word[..^2]);

            // 双写辅音还原 (stopped -> stop, planned -> plan)
            if (len > 4 && word[len - 3] == word[len - 4] && !"lsz".Contains(word[len - 3]))
            {
                AddCandidate(word[..^3]);
            }
        }

        // 4. 现在分词 / 动名词 (-ying, -ing)
        if (word.EndsWith("ying", StringComparison.Ordinal) && len > 4)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 4), "ie"));
        }
        else if (word.EndsWith("ing", StringComparison.Ordinal) && len > 4)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "e"));
            AddCandidate(word[..^3]);

            // 双写辅音还原 (running -> run, swimming -> swim)
            if (len > 5 && word[len - 4] == word[len - 5] && !"lsz".Contains(word[len - 4]))
            {
                AddCandidate(word[..^4]);
            }
        }

        // 5. 比较级 / 最高级 (-ier/-iest, -er/-est)
        if (word.EndsWith("ier", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "y"));
        }
        else if (word.EndsWith("iest", StringComparison.Ordinal) && len > 4)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 4), "y"));
        }
        else if (word.EndsWith("er", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(word[..^1]);
            AddCandidate(word[..^2]);
            if (len > 4 && word[len - 3] == word[len - 4])
            {
                AddCandidate(word[..^3]);
            }
        }
        else if (word.EndsWith("est", StringComparison.Ordinal) && len > 4)
        {
            AddCandidate(word[..^2]);
            AddCandidate(word[..^3]);
            if (len > 5 && word[len - 4] == word[len - 5])
            {
                AddCandidate(word[..^4]);
            }
        }

        // 6. 副词 (-ily, -ly)
        if (word.EndsWith("ily", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(string.Concat(word.AsSpan(0, len - 3), "y"));
        }
        else if (word.EndsWith("ly", StringComparison.Ordinal) && len > 3)
        {
            AddCandidate(word[..^2]);
        }

        return candidates;
    }
}

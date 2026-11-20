using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WordCatcher.Core.Services;

public sealed record OcrWordToken(string Text, int Offset, int Length);

public static partial class OcrTokenExtractor
{
    private const int MaxTokens = 40;
    private const string LatinLetter = @"(?=\p{L})[A-Za-z\u00C0-\u024F\u1D00-\u1DBF\u1E00-\u1EFF\u2C60-\u2C7F\uA720-\uA7FF\uAB30-\uAB6F\uFF21-\uFF3A\uFF41-\uFF5A]";
    private const string WordPart = "(?:" + LatinLetter + @"\p{M}*)+";

    [GeneratedRegex(WordPart + @"(?:[-'’\u2010\u2011]" + WordPart + ")*")]
    private static partial Regex WordPattern();

    public static IReadOnlyList<OcrWordToken> ExtractWords(string text)
    {
        var words = new List<OcrWordToken>();
        foreach (Match match in WordPattern().Matches(text))
        {
            words.Add(new OcrWordToken(match.Value, match.Index, match.Length));
            if (words.Count == MaxTokens)
                break;
        }

        return words;
    }
}

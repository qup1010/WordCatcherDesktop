using System;

namespace WordCatcher.Core.Algorithms;

public static class ClozeSentenceBuilder
{
    public static string EscapeHtml(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    public static int ResolveOffset(string sentence, string selection, int hint)
    {
        return ResolveRange(sentence, selection, hint).Offset;
    }

    public static string BuildClozeSentence(string sentence, string selection, int offset = -1)
    {
        if (string.IsNullOrEmpty(sentence))
            return string.Empty;

        var match = ResolveRange(sentence, selection, offset);
        if (match.Offset < 0)
            return EscapeHtml(sentence);

        var before = EscapeHtml(sentence[..match.Offset]);
        var after = EscapeHtml(sentence[(match.Offset + match.Length)..]);

        return $"{before}<span class=\"wc-blank\">[&nbsp;?&nbsp;]</span>{after}";
    }

    private static (int Offset, int Length) ResolveRange(string sentence, string selection, int hint)
    {
        var needle = selection.Trim();
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(sentence))
            return (-1, 0);

        if (hint >= 0 && TryMatchAt(sentence, needle, hint, out var hintedLength))
            return (hint, hintedLength);

        for (var start = 0; start < sentence.Length; start++)
        {
            if (TryMatchAt(sentence, needle, start, out var matchLength))
                return (start, matchLength);
        }

        return (-1, 0);
    }

    private static bool TryMatchAt(string sentence, string needle, int start, out int matchLength)
    {
        matchLength = 0;
        if (start < 0 || start >= sentence.Length)
            return false;

        var sentenceIndex = start;
        var needleIndex = 0;
        while (needleIndex < needle.Length)
        {
            if (char.IsWhiteSpace(needle[needleIndex]))
            {
                while (needleIndex < needle.Length && char.IsWhiteSpace(needle[needleIndex]))
                    needleIndex++;
                if (sentenceIndex >= sentence.Length || !char.IsWhiteSpace(sentence[sentenceIndex]))
                    return false;
                while (sentenceIndex < sentence.Length && char.IsWhiteSpace(sentence[sentenceIndex]))
                    sentenceIndex++;
                continue;
            }

            if (sentenceIndex >= sentence.Length
                || char.ToUpperInvariant(sentence[sentenceIndex]) != char.ToUpperInvariant(needle[needleIndex]))
            {
                return false;
            }

            sentenceIndex++;
            needleIndex++;
        }

        matchLength = sentenceIndex - start;
        return true;
    }
}

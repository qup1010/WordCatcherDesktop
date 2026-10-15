using System;
using System.Collections.Generic;

namespace WordCatcher.Core.Algorithms;

/// <summary>
/// Extracts a clean context sentence around a selected span while avoiding
/// false sentence breaks on common academic/general abbreviations and decimals.
/// </summary>
public static class SentenceExtractor
{
    private static readonly HashSet<string> CommonAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "st", "inc", "ltd", "jr", "sr",
        "vs", "etc", "fig", "figs", "eq", "eqs", "vol", "no", "nos", "pp",
        "al", "ch", "sec", "ref", "refs", "approx", "dept", "univ"
    };

    public static (string Text, string Sentence, int Offset) Extract(
        string paragraph,
        int selectionOffset,
        int selectionLength)
    {
        if (string.IsNullOrEmpty(paragraph)
            || selectionOffset < 0
            || selectionLength <= 0
            || selectionOffset + selectionLength > paragraph.Length)
        {
            var fallback = paragraph?.Trim() ?? string.Empty;
            return (fallback, fallback, 0);
        }

        var from = selectionOffset;
        while (from > 0 && !IsSentenceBoundary(paragraph, from - 1))
            from--;

        var to = selectionOffset + selectionLength;
        while (to < paragraph.Length && !IsSentenceBoundary(paragraph, to))
            to++;
        if (to < paragraph.Length)
            to++;

        var rawBefore = paragraph[from..selectionOffset];
        var rawSel = paragraph.Substring(selectionOffset, selectionLength);
        var rawAfter = paragraph[(selectionOffset + selectionLength)..to];

        var before = NormalizeWhitespace(rawBefore);
        var middle = NormalizeWhitespace(rawSel);
        var after = NormalizeWhitespace(rawAfter);

        var gapBefore = before.Length > 0 && middle.Length > 0 && HasTrailingWhitespace(rawBefore) ? " " : string.Empty;
        var gapAfter = middle.Length > 0 && after.Length > 0 && HasLeadingWhitespace(rawAfter) ? " " : string.Empty;

        var sentence = string.Concat(before, gapBefore, middle, gapAfter, after);
        if (string.IsNullOrEmpty(sentence))
        {
            var sel = rawSel.Trim();
            return (sel, sel, 0);
        }

        var offset = before.Length + gapBefore.Length;
        return (middle.Length > 0 ? middle : rawSel.Trim(), sentence, offset);
    }

    public static bool IsSentenceBoundary(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return false;

        var ch = text[index];
        if (ch is '!' or '?' or '。' or '！' or '？' or '；' or ';')
            return true;

        if (ch == '\n')
            return IsBlankLine(text, index);

        if (ch == '.')
            return IsRealPeriod(text, index);

        return false;
    }

    private static bool IsRealPeriod(string text, int index)
    {
        // If followed by a non-whitespace character (e.g. 3.14, e.g., example.com), not a sentence end
        if (index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]))
            return false;

        // Extract token preceding the period
        var wordEnd = index;
        var wordStart = index - 1;
        while (wordStart >= 0 && (char.IsLetterOrDigit(text[wordStart]) || text[wordStart] == '.'))
            wordStart--;
        wordStart++;

        if (wordStart < wordEnd)
        {
            var token = text[wordStart..wordEnd];

            // Single uppercase initial (e.g., "J. R. R. Tolkien")
            if (token.Length == 1 && char.IsUpper(token[0]))
                return false;

            // Dotted abbreviation like "e.g" or "i.e" or "U.S"
            if (token.Contains('.'))
                return false;

            if (CommonAbbreviations.Contains(token))
                return false;

            // "Fig. 1" or "No. 5" when followed by a digit
            var nextNonSpace = index + 1;
            while (nextNonSpace < text.Length && char.IsWhiteSpace(text[nextNonSpace]))
                nextNonSpace++;
            if (nextNonSpace < text.Length && char.IsDigit(text[nextNonSpace])
                && token.Length <= 4 && char.IsUpper(token[0]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBlankLine(string text, int index)
    {
        var before = index - 1;
        while (before >= 0 && text[before] is ' ' or '\t' or '\r')
            before--;
        if (before >= 0 && text[before] == '\n')
            return true;

        var after = index + 1;
        while (after < text.Length && text[after] is ' ' or '\t' or '\r')
            after++;
        return after < text.Length && text[after] == '\n';
    }

    private static bool HasTrailingWhitespace(string text)
        => text.Length > 0 && char.IsWhiteSpace(text[^1]);

    private static bool HasLeadingWhitespace(string text)
        => text.Length > 0 && char.IsWhiteSpace(text[0]);

    private static string NormalizeWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

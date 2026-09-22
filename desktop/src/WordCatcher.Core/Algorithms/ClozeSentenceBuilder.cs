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
        var needle = selection.Trim();
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(sentence))
            return -1;

        if (hint >= 0 && hint + needle.Length <= sentence.Length)
        {
            if (string.Equals(sentence.Substring(hint, needle.Length), needle, StringComparison.Ordinal))
            {
                return hint;
            }
        }

        return sentence.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildClozeSentence(string sentence, string selection, int offset = -1)
    {
        if (string.IsNullOrEmpty(sentence))
            return string.Empty;

        var needle = selection.Trim();
        if (string.IsNullOrEmpty(needle))
            return EscapeHtml(sentence);

        var at = ResolveOffset(sentence, needle, offset);
        if (at < 0)
            return EscapeHtml(sentence);

        var before = EscapeHtml(sentence[..at]);
        var after = EscapeHtml(sentence[(at + needle.Length)..]);

        return $"{before}<span class=\"wc-blank\">[&nbsp;?&nbsp;]</span>{after}";
    }
}

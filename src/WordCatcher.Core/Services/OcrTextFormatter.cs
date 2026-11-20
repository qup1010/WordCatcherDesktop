using System.Collections.Generic;
using System.Text;

namespace WordCatcher.Core.Services;

public static class OcrTextFormatter
{
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        Rune? lastRune = null;
        var pendingParagraphBreak = false;

        foreach (var raw in lines)
        {
            var line = (raw ?? string.Empty).Trim();
            if (line.Length == 0)
            {
                if (sb.Length > 0) pendingParagraphBreak = true;
                continue;
            }

            if (sb.Length == 0)
            {
                sb.Append(line);
                lastRune = LastRuneOf(line);
                continue;
            }

            if (pendingParagraphBreak)
            {
                sb.Append('\n');
                sb.Append(line);
                lastRune = LastRuneOf(line);
                pendingParagraphBreak = false;
                continue;
            }

            var firstRune = FirstRuneOf(line);
            if (lastRune is { Value: '-' } && firstRune is { } r && Rune.IsLetter(r) && r.IsAscii)
            {
                // Preserve hyphen without inserting an extra space (keeps "well-known" intact).
                sb.Append(line);
                lastRune = LastRuneOf(line);
                continue;
            }

            if (lastRune is { } left && firstRune is { } right && ShouldInsertSpace(left, right))
                sb.Append(' ');

            sb.Append(line);
            lastRune = LastRuneOf(line);
        }

        return sb.ToString().Trim();
    }

    private static Rune? FirstRuneOf(string text)
    {
        foreach (var rune in text.EnumerateRunes()) return rune;
        return null;
    }

    private static Rune? LastRuneOf(string text)
    {
        Rune? last = null;
        foreach (var rune in text.EnumerateRunes()) last = rune;
        return last;
    }

    private static bool ShouldInsertSpace(Rune left, Rune right)
    {
        if (Rune.IsWhiteSpace(left) || Rune.IsWhiteSpace(right)) return false;
        if (IsCjk(left) && IsCjk(right)) return false;
        return true;
    }

    private static bool IsCjk(Rune r)
    {
        var v = r.Value;
        return (v >= 0x4E00 && v <= 0x9FFF)
            || (v >= 0x3400 && v <= 0x4DBF)
            || (v >= 0x20000 && v <= 0x2FA1F)
            || (v >= 0x3040 && v <= 0x30FF)
            || (v >= 0xAC00 && v <= 0xD7AF)
            || (v >= 0x3000 && v <= 0x303F)
            || (v >= 0xFF00 && v <= 0xFFEF);
    }
}

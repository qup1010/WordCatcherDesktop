using System;
using System.Collections.Generic;

namespace WordCatcher.Core.Algorithms;

public readonly record struct HighlightedSentenceParts(string Prefix, string Highlight, string Suffix)
{
    public bool HasHighlight => !string.IsNullOrEmpty(Highlight);
}

public static class SentenceHighlightResolver
{
    public static HighlightedSentenceParts Split(
        string? sentence,
        IEnumerable<string?>? candidates,
        int preferredOffset = -1)
    {
        if (string.IsNullOrEmpty(sentence))
            return new HighlightedSentenceParts(string.Empty, string.Empty, string.Empty);

        var normalizedCandidates = NormalizeCandidates(candidates);
        if (normalizedCandidates.Count == 0)
            return new HighlightedSentenceParts(sentence, string.Empty, string.Empty);

        // Priority 1: explicit selection offset hint
        if (preferredOffset >= 0 && preferredOffset < sentence.Length)
        {
            foreach (var candidate in normalizedCandidates)
            {
                if (TryMatchAt(sentence, candidate, preferredOffset, out var matchLength))
                    return Slice(sentence, preferredOffset, matchLength);
            }
        }

        // Priority 2: exact match with whole-word boundary
        foreach (var candidate in normalizedCandidates)
        {
            for (var start = 0; start < sentence.Length; start++)
            {
                if (TryMatchAt(sentence, candidate, start, out var matchLength)
                    && IsWordBoundaryValid(sentence, start, matchLength))
                {
                    return Slice(sentence, start, matchLength);
                }
            }
        }

        // Priority 3: English lemmatized match across sentence tokens
        List<(int Start, int Length, string Token)>? sentenceTokens = null;
        foreach (var candidate in normalizedCandidates)
        {
            if (!IsEligibleEnglishLemmaCandidate(candidate))
                continue;

            var candidateLemmas = BuildLemmaSet(candidate);
            if (candidateLemmas.Count == 0)
                continue;

            sentenceTokens ??= ExtractSentenceTokens(sentence);
            foreach (var (start, length, token) in sentenceTokens)
            {
                var tokenLemmas = Lemmatizer.GetCandidates(token);
                foreach (var lemma in tokenLemmas)
                {
                    if (lemma.Length >= 2 && candidateLemmas.Contains(lemma))
                        return Slice(sentence, start, length);
                }
            }
        }

        // Priority 4: substring fallback without boundary restriction
        foreach (var candidate in normalizedCandidates)
        {
            for (var start = 0; start < sentence.Length; start++)
            {
                if (TryMatchAt(sentence, candidate, start, out var matchLength))
                    return Slice(sentence, start, matchLength);
            }
        }

        return new HighlightedSentenceParts(sentence, string.Empty, string.Empty);
    }

    private static List<string> NormalizeCandidates(IEnumerable<string?>? candidates)
    {
        var list = new List<string>();
        if (candidates == null)
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            if (seen.Add(trimmed))
                list.Add(trimmed);
        }

        return list;
    }

    private static HighlightedSentenceParts Slice(string sentence, int offset, int length)
        => new(sentence[..offset], sentence.Substring(offset, length), sentence[(offset + length)..]);

    private static bool IsAsciiLetter(char c)
        => (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z');

    private static bool IsAsciiWordChar(char c)
        => IsAsciiLetter(c) || (c is >= '0' and <= '9');

    private static bool IsTokenChar(char c)
        => IsAsciiWordChar(c) || c is '\'' or '-';

    private static bool IsWordBoundaryValid(string sentence, int start, int length)
    {
        if (length <= 0)
            return false;

        if (IsAsciiWordChar(sentence[start]) && start > 0 && IsAsciiWordChar(sentence[start - 1]))
            return false;

        var end = start + length;
        if (IsAsciiWordChar(sentence[end - 1]) && end < sentence.Length && IsAsciiWordChar(sentence[end]))
            return false;

        return true;
    }

    private static bool IsEligibleEnglishLemmaCandidate(string candidate)
    {
        if (candidate.Length < 2)
            return false;

        var hasAsciiLetter = false;
        foreach (var ch in candidate)
        {
            if (char.IsWhiteSpace(ch))
                return false;
            if (IsAsciiLetter(ch))
                hasAsciiLetter = true;
        }

        return hasAsciiLetter;
    }

    private static HashSet<string> BuildLemmaSet(string candidate)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lemma in Lemmatizer.GetCandidates(candidate))
        {
            if (lemma.Length >= 2)
                set.Add(lemma);
        }

        return set;
    }

    private static List<(int Start, int Length, string Token)> ExtractSentenceTokens(string sentence)
    {
        var tokens = new List<(int Start, int Length, string Token)>();
        var i = 0;
        while (i < sentence.Length)
        {
            if (!IsTokenChar(sentence[i]))
            {
                i++;
                continue;
            }

            var rawStart = i;
            while (i < sentence.Length && IsTokenChar(sentence[i]))
                i++;
            var rawEnd = i;

            var start = rawStart;
            while (start < rawEnd && !IsAsciiWordChar(sentence[start]))
                start++;

            var end = rawEnd;
            while (end > start && !IsAsciiWordChar(sentence[end - 1]))
                end--;

            var length = end - start;
            if (length >= 2)
                tokens.Add((start, length, sentence.Substring(start, length)));
        }

        return tokens;
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

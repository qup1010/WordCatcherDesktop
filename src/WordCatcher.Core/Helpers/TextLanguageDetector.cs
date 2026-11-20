using System.Text.RegularExpressions;

namespace WordCatcher.Core.Helpers;

public static class TextLanguageDetector
{
    private const string Han = @"\u3400-\u4DBF\u4E00-\u9FFF";
    private const string KanaOrHangul = @"\u3040-\u30FF\uAC00-\uD7AF";
    private static readonly Regex HanPattern = new($"[{Han}]");
    private static readonly Regex KanaOrHangulPattern = new($"[{KanaOrHangul}]");
    private static readonly Regex EnglishLetterPattern = new("[A-Za-z]");
    private static readonly Regex CjkPattern = new(
        $@"[{Han}{KanaOrHangul}\u1100-\u11FF\u3130-\u318F\u31F0-\u31FF\uA960-\uA97F\uD7B0-\uD7FF\uF900-\uFAFF\uFF66-\uFFDC]|[\uD840-\uD88F][\uDC00-\uDFFF]");

    public static bool IsChineseText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && HanPattern.IsMatch(text) && !KanaOrHangulPattern.IsMatch(text);

    public static bool IsEnglishText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && EnglishLetterPattern.IsMatch(text)
        && !CjkPattern.IsMatch(text) && !text.Contains("://", StringComparison.Ordinal);

    public static string ResolveEffectiveTargetLanguage(
        string? sourceText, string? configuredExplainLanguage, bool autoBidirectionalTranslation) =>
        autoBidirectionalTranslation && IsChineseText(sourceText)
            ? "English"
            : string.IsNullOrWhiteSpace(configuredExplainLanguage) ? "简体中文" : configuredExplainLanguage.Trim();
}

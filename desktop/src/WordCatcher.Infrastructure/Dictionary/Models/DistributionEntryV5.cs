using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordCatcher.Infrastructure.Dictionary.Models;

public sealed class DistributionEntryV5
{
    [JsonPropertyName("schema_version")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("entry_id")]
    public string? EntryId { get; set; }

    [JsonPropertyName("headword")]
    public string Headword { get; set; } = string.Empty;

    [JsonPropertyName("normalized_headword")]
    public string? NormalizedHeadword { get; set; }

    [JsonPropertyName("headword_language")]
    public DictionaryLanguageV5? HeadwordLanguage { get; set; }

    [JsonPropertyName("definition_language")]
    public DictionaryLanguageV5? DefinitionLanguage { get; set; }

    [JsonPropertyName("headword_summary")]
    public string? HeadwordSummary { get; set; }

    [JsonPropertyName("memory_hook")]
    public string? MemoryHook { get; set; }

    [JsonPropertyName("study_notes")]
    public List<string>? StudyNotes { get; set; }

    [JsonPropertyName("etymology_note")]
    public string? EtymologyNote { get; set; }

    [JsonPropertyName("pronunciations")]
    public DictionaryPronunciations? Pronunciations { get; set; }

    [JsonPropertyName("pos_groups")]
    public List<DictionaryPosGroupV5> PosGroups { get; set; } = new();
}

public sealed class DictionaryPosGroupV5
{
    [JsonPropertyName("pos")]
    public string Pos { get; set; } = string.Empty;

    [JsonPropertyName("proper_name")]
    public bool? ProperName { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("forms")]
    public List<DictionaryFormV5>? Forms { get; set; }

    [JsonPropertyName("pronunciations")]
    public DictionaryPronunciations? Pronunciations { get; set; }

    [JsonPropertyName("meanings")]
    public List<DictionaryMeaningV5> Meanings { get; set; } = new();
}

[JsonConverter(typeof(DictionaryPronunciationsJsonConverter))]
public sealed class DictionaryPronunciations
{
    [JsonPropertyName("us")]
    public string? Us { get; set; }

    [JsonPropertyName("uk")]
    public string? Uk { get; set; }

    [JsonPropertyName("ipa")]
    public string? Ipa { get; set; }
}

[JsonConverter(typeof(DictionaryLanguageV5JsonConverter))]
public sealed class DictionaryLanguageV5
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class DictionaryLanguageV5JsonConverter : JsonConverter<DictionaryLanguageV5>
{
    public override DictionaryLanguageV5 Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.String)
        {
            return new DictionaryLanguageV5 { Code = root.GetString() };
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("词典语言字段必须是字符串或对象。");
        }

        return new DictionaryLanguageV5
        {
            Code = ReadString(root, "code"),
            Name = ReadString(root, "name")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        DictionaryLanguageV5 value,
        JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.Name))
        {
            writer.WriteStringValue(value.Code);
            return;
        }

        writer.WriteStartObject();
        if (value.Code != null) writer.WriteString("code", value.Code);
        if (value.Name != null) writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }

    private static string? ReadString(JsonElement objectElement, string propertyName)
    {
        return objectElement.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

public sealed class DictionaryFormV5
{
    [JsonPropertyName("roman")]
    public string? Roman { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>
/// Supports both the compact test/legacy object form and the official v5
/// pronunciation array form:
/// {\"us\":\"...\",\"uk\":\"...\",\"ipa\":\"...\"}
/// [{\"ipa\":\"/kwɪk/\",\"tags\":[],\"text\":null}]
/// </summary>
public sealed class DictionaryPronunciationsJsonConverter : JsonConverter<DictionaryPronunciations>
{
    public override DictionaryPronunciations Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var result = new DictionaryPronunciations();

        if (root.ValueKind == JsonValueKind.Object)
        {
            result.Us = ReadString(root, "us");
            result.Uk = ReadString(root, "uk");
            result.Ipa = ReadString(root, "ipa");
            return result;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("词典发音字段必须是对象或数组。");
        }

        string? firstAvailable = null;
        foreach (var pronunciation in root.EnumerateArray())
        {
            if (pronunciation.ValueKind != JsonValueKind.Object)
                continue;

            var ipa = ReadString(pronunciation, "ipa");
            var text = ReadString(pronunciation, "text");
            var value = !string.IsNullOrWhiteSpace(ipa) ? ipa : text;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            firstAvailable ??= value;
            var tags = ReadStringArray(pronunciation, "tags");
            if (HasTag(tags, "us") || HasTag(tags, "american"))
            {
                result.Us ??= value;
            }
            else if (HasTag(tags, "uk") || HasTag(tags, "british"))
            {
                result.Uk ??= value;
            }
        }

        // Official exports may omit dialect tags. Preserve the first available
        // pronunciation as the generic IPA/fallback value in that case.
        result.Ipa = firstAvailable;
        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        DictionaryPronunciations value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Us != null) writer.WriteString("us", value.Us);
        if (value.Uk != null) writer.WriteString("uk", value.Uk);
        if (value.Ipa != null) writer.WriteString("ipa", value.Ipa);
        writer.WriteEndObject();
    }

    private static string? ReadString(JsonElement objectElement, string propertyName)
    {
        return objectElement.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static List<string> ReadStringArray(JsonElement objectElement, string propertyName)
    {
        if (!objectElement.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToList();
    }

    private static bool HasTag(IEnumerable<string> tags, string expected)
    {
        return tags.Any(tag => string.Equals(tag, expected, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DictionaryMeaningV5
{
    [JsonPropertyName("sense_id")]
    public string SenseId { get; set; } = string.Empty;

    [JsonPropertyName("priority")]
    public string Priority { get; set; } = "common"; // "core", "common", "rare"

    [JsonPropertyName("short_gloss")]
    public string? ShortGloss { get; set; }

    [JsonPropertyName("learner_explanation")]
    public string? LearnerExplanation { get; set; }

    [JsonPropertyName("usage_note")]
    public string? UsageNote { get; set; }

    [JsonPropertyName("examples")]
    public List<DictionaryExampleV5>? Examples { get; set; }
}

public sealed class DictionaryExampleV5
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string? Translation { get; set; }
}

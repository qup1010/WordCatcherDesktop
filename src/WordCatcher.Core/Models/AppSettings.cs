using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace WordCatcher.Core.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Alt+Q";
    public string ExplainLanguage { get; set; } = "简体中文";
    public TranslationSettings Translation { get; set; } = new();
    public List<TranslationProfile> TranslationProfiles { get; set; } = new();
    public string ActiveTranslationProfileId { get; set; } = "default";
    public DictionarySettings Dictionary { get; set; } = new();
    public AnkiSettings Anki { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    [JsonIgnore]
    public TranslationSettings ActiveTranslation =>
        (TranslationProfiles ?? new List<TranslationProfile>()).FirstOrDefault(p => p.Id == ActiveTranslationProfileId)
        ?? ((TranslationProfiles?.Count ?? 0) > 0 ? TranslationProfiles![0] : (Translation ?? new TranslationSettings()));
}

public class TranslationSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 30;
    public string MachineTranslationProvider { get; set; } = "microsoft";
}

public sealed class TranslationProfile : TranslationSettings
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "默认";
}

public sealed class DictionarySettings
{
    public bool Enabled { get; set; } = true;
    public string DownloadUrl { get; set; } = "https://github.com/ahpxex/open-dictionary/releases/latest/download/distribution.sqlite.gz";
}

public sealed class AnkiSettings
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "http://127.0.0.1:8765";
    public string DeckName { get; set; } = "Word Catcher";
    public string NoteTypeName { get; set; } = "Word Catcher";
    public bool ClozeContext { get; set; } = true;
    public string TtsLang { get; set; } = "en_US";
}

public sealed class UiSettings
{
    public bool ClosePopupAfterSave { get; set; } = true;
}

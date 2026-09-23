using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Algorithms;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Anki;

public sealed class AnkiConnectClient : IAnkiClient
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AnkiConnectClient>? _logger;

    private static readonly string[] AnkiFields =
    [
        "Word",
        "Reading",
        "PartOfSpeech",
        "Definition",
        "MemoryHook",
        "Sentence",
        "SentencePlain",
        "SentenceTranslation",
        "Source"
    ];

    private static readonly string[] AnkiModes = ["Context", "Recognition", "Production", "Listening"];

    private const string ModelCss = @"
.card {
  font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", ""Microsoft YaHei"", sans-serif;
  font-size: 21px;
  text-align: left;
  color: #221f1a;
  background: #fdfcf9;
  padding: 28px 32px;
  line-height: 1.8;
  max-width: 460px;
  margin: 0 auto;
  border-radius: 12px;
  box-shadow: 0 1px 6px rgba(0, 0, 0, 0.04);
}

.wc-sentence {
  font-size: 24px;
  margin-bottom: 16px;
  font-weight: 500;
}

.wc-blank {
  color: #b45309;
  font-weight: 700;
  border-bottom: 2px solid #b45309;
  padding: 0 4px;
  background: rgba(180, 83, 9, 0.06);
  border-radius: 2px;
}

.wc-pos {
  margin-top: 12px;
  font-family: Georgia, ""Times New Roman"", serif;
  font-size: 16px;
  color: #0e6f63;
  font-style: italic;
}

.wc-word {
  margin-top: 8px;
  font-family: Georgia, ""Times New Roman"", serif;
  font-size: 36px;
  font-weight: 700;
  letter-spacing: -0.02em;
  color: #221f1a;
  line-height: 1.1;
}

.wc-reading {
  font-family: Georgia, ""Times New Roman"", serif;
  font-size: 20px;
  font-weight: 400;
  color: #a39c8f;
  margin-left: 4px;
}

.wc-listen-hint {
  margin-top: 14px;
  font-size: 16px;
  color: #a39c8f;
  letter-spacing: 0.06em;
  font-weight: 500;
}

.wc-def {
  margin-top: 14px;
  font-size: 20px;
  color: #3f3f3f;
  line-height: 1.6;
}

.wc-hook {
  margin-top: 16px;
  padding: 10px 14px 10px 12px;
  background: #f6f2e9;
  border-radius: 8px;
  border-left: 3px solid #b45309;
  font-size: 15px;
  color: #635b4f;
  line-height: 1.6;
}

.wc-hook-label {
  font-weight: 600;
  color: #92400e;
  margin-right: 4px;
}

.wc-full {
  margin-top: 18px;
  padding-left: 14px;
  border-left: 3px solid #0e6f63;
  color: #6d675d;
  font-size: 17px;
  line-height: 1.7;
}

.wc-trans {
  margin-top: 8px;
  font-size: 15px;
  color: #a39c8f;
  line-height: 1.6;
}

.wc-source {
  margin-top: 16px;
  font-size: 13px;
  color: #a39c8f;
}

.wc-source a {
  color: #a39c8f;
  text-decoration: none;
}

hr#answer {
  border: none;
  border-top: 1px solid #e8e3d9;
  margin: 22px 0 8px;
}

.nightMode .card,
.night_mode .card {
  color: #ece7dd;
  background: #23211d;
  box-shadow: 0 1px 6px rgba(0, 0, 0, 0.3);
}

.nightMode .wc-word,
.night_mode .wc-word {
  color: #ece7dd;
}

.nightMode .wc-pos,
.night_mode .wc-pos {
  color: #4fd1bb;
}

.nightMode .wc-blank,
.night_mode .wc-blank {
  color: #e8a94e;
  border-bottom-color: #e8a94e;
  background: rgba(232, 169, 78, 0.12);
}

.nightMode .wc-hook,
.night_mode .wc-hook {
  background: #2e2a24;
  color: #cfc6b8;
  border-left-color: #e8a94e;
}

.nightMode .wc-hook-label,
.night_mode .wc-hook-label {
  color: #e8a94e;
}

.nightMode .wc-full,
.night_mode .wc-full {
  color: #a89f90;
  border-left-color: #4fd1bb;
}

.nightMode .wc-listen-hint,
.night_mode .wc-listen-hint {
  color: #a89f90;
}

.nightMode hr#answer,
.night_mode hr#answer {
  border-top-color: #3a362e;
}
";

    public AnkiConnectClient(
        HttpClient httpClient,
        ISettingsService settingsService,
        ILogger<AnkiConnectClient>? logger = null)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<int> CheckVersionAsync(CancellationToken ct = default)
    {
        return await InvokeAsync<int>("version", null, ct).ConfigureAwait(false);
    }

    public async Task EnsureDeckAndModelAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.Current.Anki;
        var deckName = settings.DeckName;
        var noteTypeName = settings.NoteTypeName;
        var ttsLang = settings.TtsLang;

        // 1. Ensure Decks
        var existingDecks = await InvokeAsync<List<string>>("deckNames", null, ct).ConfigureAwait(false) ?? new();
        var wantedDecks = new List<string> { deckName };
        foreach (var mode in AnkiModes)
        {
            wantedDecks.Add($"{deckName}::{mode}");
        }

        foreach (var deck in wantedDecks)
        {
            if (!existingDecks.Contains(deck))
            {
                await InvokeAsync<object>("createDeck", new { deck }, ct).ConfigureAwait(false);
            }
        }

        // 2. Ensure Model
        var existingModels = await InvokeAsync<List<string>>("modelNames", null, ct).ConfigureAwait(false) ?? new();
        if (!existingModels.Contains(noteTypeName))
        {
            var templates = BuildCardTemplates(ttsLang);
            await InvokeAsync<object>("createModel", new
            {
                modelName = noteTypeName,
                inOrderFields = AnkiFields,
                css = ModelCss.Trim(),
                isCloze = false,
                cardTemplates = templates
            }, ct).ConfigureAwait(false);
        }
        else
        {
            // Existing note types may contain user-managed templates. Validate
            // compatibility without mutating their fields, templates or CSS.
            var fields = await InvokeAsync<List<string>>("modelFieldNames", new { modelName = noteTypeName }, ct).ConfigureAwait(false) ?? new();
            var missingFields = new List<string>();
            foreach (var field in AnkiFields)
            {
                if (!fields.Contains(field))
                    missingFields.Add(field);
            }

            if (missingFields.Count > 0)
            {
                throw new AnkiException(
                    $"Anki 笔记类型「{noteTypeName}」缺少字段：{string.Join("、", missingFields)}。为保护现有模板，应用未自动修改它；请新建 Word Catcher 专用笔记类型，或选择包含所需字段的笔记类型。");
            }
        }
    }

    public async Task<long> AddNoteAsync(Word word, Occurrence occurrence, CancellationToken ct = default)
    {
        var ankiSettings = _settingsService.Current.Anki;
        await EnsureDeckAndModelAsync(ct).ConfigureAwait(false);

        var fields = BuildNoteFields(word, occurrence, ankiSettings.ClozeContext);
        try
        {
            var noteId = await InvokeAsync<long>("addNote", new
            {
                note = new
                {
                    deckName = ankiSettings.DeckName,
                    modelName = ankiSettings.NoteTypeName,
                    fields = fields,
                    tags = new[] { "word-catcher" },
                    options = new
                    {
                        allowDuplicate = false,
                        duplicateScope = "deck"
                    }
                }
            }, ct).ConfigureAwait(false);

            // Try route cards to mode decks
            try
            {
                await RouteCardsToModeDecksAsync(noteId, ankiSettings.DeckName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to route cards to mode decks for note {NoteId}", noteId);
            }

            return noteId;
        }
        catch (AnkiException ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation("Note for '{Word}' is duplicate in Anki, treating as synced", word.DisplayWord);
            return 0; // Treated as synced
        }
    }

    private async Task RouteCardsToModeDecksAsync(long noteId, string deckName, CancellationToken ct)
    {
        var cardIds = await InvokeAsync<List<long>>("findCards", new { query = $"nid:{noteId}" }, ct).ConfigureAwait(false);
        if (cardIds == null || cardIds.Count == 0) return;

        var cardsInfo = await InvokeAsync<List<AnkiCardInfo>>("cardsInfo", new { cards = cardIds }, ct).ConfigureAwait(false);
        if (cardsInfo == null) return;

        var byDeck = new Dictionary<string, List<long>>();
        foreach (var info in cardsInfo)
        {
            if (info.Ord >= 0 && info.Ord < AnkiModes.Length)
            {
                var mode = AnkiModes[info.Ord];
                var targetDeck = $"{deckName}::{mode}";
                if (!byDeck.TryGetValue(targetDeck, out var list))
                {
                    list = new List<long>();
                    byDeck[targetDeck] = list;
                }
                list.Add(info.CardId);
            }
        }

        foreach (var (deck, cards) in byDeck)
        {
            await InvokeAsync<object>("changeDeck", new { cards, deck }, ct).ConfigureAwait(false);
        }
    }

    public static Dictionary<string, string> BuildNoteFields(Word word, Occurrence occurrence, bool clozeContext)
    {
        var sourceHtml = !string.IsNullOrWhiteSpace(occurrence.SourceUri)
            ? $"<a href=\"{ClozeSentenceBuilder.EscapeHtml(occurrence.SourceUri)}\">{ClozeSentenceBuilder.EscapeHtml(!string.IsNullOrWhiteSpace(occurrence.SourceWindowTitle) ? occurrence.SourceWindowTitle : occurrence.SourceUri)}</a>"
            : ClozeSentenceBuilder.EscapeHtml(occurrence.SourceWindowTitle);

        return new Dictionary<string, string>
        {
            ["Word"] = ClozeSentenceBuilder.EscapeHtml(word.DisplayWord),
            ["Reading"] = ClozeSentenceBuilder.EscapeHtml(NormalizeReading(word.Reading)),
            ["PartOfSpeech"] = ClozeSentenceBuilder.EscapeHtml(word.PartOfSpeech),
            ["Definition"] = ClozeSentenceBuilder.EscapeHtml(word.Definition),
            ["MemoryHook"] = ClozeSentenceBuilder.EscapeHtml(word.MemoryHook),
            ["Sentence"] = clozeContext
                ? ClozeSentenceBuilder.BuildClozeSentence(occurrence.Sentence, occurrence.SelectedText, occurrence.SelectionOffset)
                : ClozeSentenceBuilder.EscapeHtml(occurrence.Sentence),
            ["SentencePlain"] = ClozeSentenceBuilder.EscapeHtml(occurrence.Sentence),
            ["SentenceTranslation"] = ClozeSentenceBuilder.EscapeHtml(occurrence.ContextTranslation),
            ["Source"] = sourceHtml
        };
    }

    private static List<AnkiCardTemplate> BuildCardTemplates(string ttsLang)
    {
        return new List<AnkiCardTemplate>
        {
            new("Context",
                "<div class=\"wc-sentence\">{{Sentence}}</div>\n{{#PartOfSpeech}}<div class=\"wc-pos\">{{PartOfSpeech}}</div>{{/PartOfSpeech}}",
                @"{{FrontSide}}
<hr id=answer>
<div class=""wc-word"">{{Word}}{{#Reading}} <span class=""wc-reading"">{{Reading}}</span>{{/Reading}}</div>
<div class=""wc-tts"">{{tts " + ttsLang + @":Word}}</div>
<div class=""wc-def"">{{Definition}}</div>
{{#MemoryHook}}<div class=""wc-hook""><span class=""wc-hook-label"">&#x1F4A1; 记忆线索</span> {{MemoryHook}}</div>{{/MemoryHook}}
{{#SentencePlain}}<div class=""wc-full"">{{SentencePlain}}</div>{{/SentencePlain}}
{{#SentenceTranslation}}<div class=""wc-trans"">{{SentenceTranslation}}</div>{{/SentenceTranslation}}
{{#Source}}<div class=""wc-source"">{{Source}}</div>{{/Source}}"),

            new("Recognition",
                "<div class=\"wc-word\">{{Word}}</div>",
                @"{{FrontSide}}
<hr id=answer>
<div class=""wc-def"">{{Definition}}</div>
{{#MemoryHook}}<div class=""wc-hook""><span class=""wc-hook-label"">&#x1F4A1; 记忆线索</span> {{MemoryHook}}</div>{{/MemoryHook}}
<div class=""wc-tts"">{{tts " + ttsLang + @":Word}}</div>"),

            new("Production",
                "<div class=\"wc-def\">{{Definition}}</div>",
                @"{{FrontSide}}
<hr id=answer>
<div class=""wc-word"">{{Word}}{{#Reading}} <span class=""wc-reading"">{{Reading}}</span>{{/Reading}}</div>
<div class=""wc-tts"">{{tts " + ttsLang + @":Word}}</div>
{{#MemoryHook}}<div class=""wc-hook""><span class=""wc-hook-label"">&#x1F4A1; 记忆线索</span> {{MemoryHook}}</div>{{/MemoryHook}}"),

            new("Listening",
                "<div class=\"wc-tts\">{{tts " + ttsLang + @":Word}}</div>\n<div class=""wc-listen-hint"">听音</div>",
                @"{{FrontSide}}
<hr id=answer>
<div class=""wc-word"">{{Word}}{{#Reading}} <span class=""wc-reading"">{{Reading}}</span>{{/Reading}}</div>
<div class=""wc-tts"">{{tts " + ttsLang + @":Word}}</div>
<div class=""wc-def"">{{Definition}}</div>
{{#MemoryHook}}<div class=""wc-hook""><span class=""wc-hook-label"">&#x1F4A1; 记忆线索</span> {{MemoryHook}}</div>{{/MemoryHook}}")
        };
    }

    private async Task<T?> InvokeAsync<T>(string action, object? @params, CancellationToken ct)
    {
        var url = _settingsService.Current.Anki.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = "http://127.0.0.1:8765";
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));

        var reqObj = new
        {
            action,
            version = 6,
            @params = @params ?? new { }
        };

        var json = JsonSerializer.Serialize(reqObj);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await _httpClient.PostAsync(url, content, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AnkiException("AnkiConnect 超时（超过 8 秒未响应），请检查 Anki 是否有阻塞对话框。");
        }
        catch (HttpRequestException ex)
        {
            throw new AnkiException($"无法连接到 Anki: {ex.Message}", ex);
        }

        if (!res.IsSuccessStatusCode)
        {
            throw new AnkiException($"AnkiConnect 返回 HTTP {(int)res.StatusCode}");
        }

        var resJson = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(resJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
        {
            var errStr = errProp.GetString();
            if (!string.IsNullOrEmpty(errStr))
            {
                throw new AnkiException(errStr);
            }
        }

        if (root.TryGetProperty("result", out var resProp))
        {
            return JsonSerializer.Deserialize<T>(resProp.GetRawText());
        }

        return default;
    }

    private static string NormalizeReading(string reading)
    {
        var trimmed = (reading ?? string.Empty).Trim().Trim('/');
        return string.IsNullOrEmpty(trimmed) ? string.Empty : $"/{trimmed}/";
    }

    private sealed record AnkiCardTemplate(string Name, string Front, string Back);

    private sealed class AnkiCardInfo
    {
        [JsonPropertyName("cardId")]
        public long CardId { get; set; }

        [JsonPropertyName("ord")]
        public int Ord { get; set; }
    }
}

public sealed class AnkiException : Exception
{
    public AnkiException(string message) : base(message) { }
    public AnkiException(string message, Exception inner) : base(message, inner) { }
}

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

/// <summary>
/// Lightweight loopback HTTP bridge (http://127.0.0.1:18765) allowing the Word Catcher
/// browser extension to sync captured words and contexts directly into the Desktop SQLite library.
/// </summary>
public sealed class LocalBridgeServer : IDisposable
{
    public const string DefaultPrefix = "http://127.0.0.1:18765/";

    private readonly IWordRepository _wordRepository;
    private readonly IAnkiSyncQueue _syncQueue;
    private readonly WordCollectionEvents _wordCollectionEvents;
    private readonly ILogger<LocalBridgeServer>? _logger;
    private readonly string _prefix;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public LocalBridgeServer(
        IWordRepository wordRepository,
        IAnkiSyncQueue syncQueue,
        WordCollectionEvents wordCollectionEvents,
        ILogger<LocalBridgeServer>? logger = null,
        string prefix = DefaultPrefix)
    {
        _wordRepository = wordRepository;
        _syncQueue = syncQueue;
        _wordCollectionEvents = wordCollectionEvents;
        _logger = logger;
        _prefix = prefix;
    }

    public void Start()
    {
        if (_listener != null)
            return;

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(_prefix);
            listener.Start();

            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            _logger?.LogInformation("LocalBridgeServer listening on {Prefix}", _prefix);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LocalBridgeServer could not bind to {Prefix}; browser bridge disabled", _prefix);
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "LocalBridgeServer error accepting request");
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var req = context.Request;
        var res = context.Response;

        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

        try
        {
            if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = (int)HttpStatusCode.NoContent;
                res.Close();
                return;
            }

            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;

            if (string.Equals(req.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/api/v1/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(res, HttpStatusCode.OK, new
                {
                    status = "ok",
                    app = "WordCatcherDesktop"
                }).ConfigureAwait(false);
                return;
            }

            if (string.Equals(req.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(path, "/api/v1/words", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                var payload = JsonSerializer.Deserialize<BridgeWordPayload>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (payload == null || string.IsNullOrWhiteSpace(payload.Word) || string.IsNullOrWhiteSpace(payload.Definition))
                {
                    await WriteJsonAsync(res, HttpStatusCode.BadRequest, new { ok = false, error = "Missing word or definition" }).ConfigureAwait(false);
                    return;
                }

                var selectedText = !string.IsNullOrWhiteSpace(payload.Selection) ? payload.Selection.Trim() : payload.Word.Trim();
                var sentence = !string.IsNullOrWhiteSpace(payload.Sentence) ? payload.Sentence.Trim() : selectedText;

                var capture = new CaptureResult(
                    SelectedText: selectedText,
                    SourceProcess: "BrowserExtension",
                    SourceWindowTitle: payload.SourceTitle ?? string.Empty,
                    CursorPosition: new ScreenPoint(0, 0),
                    CapturedAt: payload.CreatedAt is { } createdAt ? DateTimeOffset.FromUnixTimeMilliseconds(createdAt) : DateTimeOffset.UtcNow,
                    Sentence: sentence,
                    SentenceOffset: payload.SentenceOffset ?? -1,
                    SourceUri: payload.SourceUri ?? string.Empty);

                var lookupSource = Enum.TryParse<LookupSource>(payload.LookupSource, ignoreCase: true, out var parsedSource)
                    ? parsedSource
                    : LookupSource.OpenDictionary;

                var translation = new TranslationResult(
                    Word: payload.Word.Trim(),
                    Reading: payload.Reading ?? string.Empty,
                    PartOfSpeech: payload.PartOfSpeech ?? string.Empty,
                    Definition: payload.Definition.Trim(),
                    ContextTranslation: payload.ContextTranslation ?? string.Empty,
                    MemoryHook: payload.MemoryHook,
                    Source: lookupSource,
                    SourceEntryId: null,
                    SourceSchemaVersion: null,
                    Analysis: payload.Analysis,
                    Generation: payload.Generation);

                var (savedWord, occ, syncJob) = await _wordRepository.SaveAsync(
                    new SaveCardCommand(capture, translation, payload.Edits, payload.UpdateExisting, payload.RecordId), ct).ConfigureAwait(false);

                if (Application.Current?.Dispatcher is { } dispatcher)
                {
                    _ = dispatcher.InvokeAsync(() => _wordCollectionEvents.NotifyWordSaved());
                }
                else
                {
                    _wordCollectionEvents.NotifyWordSaved();
                }

                _syncQueue.Enqueue(syncJob.Id);

                await WriteJsonAsync(res, HttpStatusCode.OK, new
                {
                    ok = true,
                    wordId = savedWord.Id,
                    occurrenceId = occ.Id,
                    syncJobId = syncJob.Id
                }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(res, HttpStatusCode.NotFound, new { ok = false, error = "Not Found" }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "LocalBridgeServer error handling {Method} {Url}", req.HttpMethod, req.Url);
            try
            {
                await WriteJsonAsync(res, HttpStatusCode.InternalServerError, new { ok = false, error = ex.Message }).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object payload)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _listener = null;
    }

    private sealed class BridgeWordPayload
    {
        public long? CreatedAt { get; set; }
        public LearningAnalysis? Analysis { get; set; }
        public GenerationInfo? Generation { get; set; }
        public LearningEdits? Edits { get; set; }
        public bool UpdateExisting { get; set; }
        public string? RecordId { get; set; }

        [JsonPropertyName("word")]
        public string? Word { get; set; }

        [JsonPropertyName("selection")]
        public string? Selection { get; set; }

        [JsonPropertyName("reading")]
        public string? Reading { get; set; }

        [JsonPropertyName("partOfSpeech")]
        public string? PartOfSpeech { get; set; }

        [JsonPropertyName("definition")]
        public string? Definition { get; set; }

        [JsonPropertyName("contextTranslation")]
        public string? ContextTranslation { get; set; }

        [JsonPropertyName("memoryHook")]
        public string? MemoryHook { get; set; }

        [JsonPropertyName("sentence")]
        public string? Sentence { get; set; }

        [JsonPropertyName("sentenceOffset")]
        public int? SentenceOffset { get; set; }

        [JsonPropertyName("sourceUri")]
        public string? SourceUri { get; set; }

        [JsonPropertyName("sourceTitle")]
        public string? SourceTitle { get; set; }

        [JsonPropertyName("lookupSource")]
        public string? LookupSource { get; set; }
    }
}

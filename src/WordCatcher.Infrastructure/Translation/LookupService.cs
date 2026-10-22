using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Translation;

public sealed class LookupService : IStreamingLookupService
{
    private const int MaxAiCacheSize = 50;
    private const int MaxSentenceCacheSize = 100;

    private readonly IOfflineDictionaryService _offlineDict;
    private readonly ITranslationService _aiTranslation;
    private readonly ILogger<LookupService>? _logger;
    private readonly ISettingsService? _settingsService;
    private readonly IMachineTranslationService? _machineTranslation;

    private readonly object _aiCacheLock = new();
    private readonly Dictionary<string, TranslationResult> _aiCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _aiCacheOrder = new();

    private readonly object _sentenceCacheLock = new();
    private readonly Dictionary<string, string> _sentenceCache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _sentenceCacheOrder = new();
    private long _sentenceCacheSettingsVersion;

    public LookupService(
        IOfflineDictionaryService offlineDict,
        ITranslationService aiTranslation,
        ILogger<LookupService>? logger = null,
        ISettingsService? settingsService = null,
        IMachineTranslationService? machineTranslation = null)
    {
        _offlineDict = offlineDict;
        _aiTranslation = aiTranslation;
        _logger = logger;
        _settingsService = settingsService;
        _machineTranslation = machineTranslation;
        if (_settingsService != null)
        {
            _settingsService.SettingsChanged += OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(AppSettings _)
    {
        lock (_sentenceCacheLock)
        {
            _sentenceCacheSettingsVersion++;
            _sentenceCache.Clear();
            _sentenceCacheOrder.Clear();
        }
    }

    public Task<TranslationResult> LookupAsync(CaptureResult capture, bool forceAi, CancellationToken ct = default)
        => LookupCoreAsync(capture, forceAi, null, false, ct);

    public Task<string> TranslateSentenceAsync(string sentence, string? selection = null, CancellationToken ct = default)
        => TranslateSentenceCoreAsync(sentence, selection, useAi: false, ct);

    public Task<string> TranslateSentenceWithAiAsync(string sentence, string? selection = null, CancellationToken ct = default)
        => TranslateSentenceCoreAsync(sentence, selection, useAi: true, ct);

    private async Task<string> TranslateSentenceCoreAsync(string sentence, string? selection, bool useAi, CancellationToken ct)
    {
        var trimmedSentence = sentence?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedSentence))
            throw new ArgumentException("原句内容为空，无法翻译。", nameof(sentence));

        long cacheSettingsVersion;
        lock (_sentenceCacheLock)
        {
            cacheSettingsVersion = _sentenceCacheSettingsVersion;
        }

        var settings = _settingsService?.Current;
        var explainLanguage = settings?.ExplainLanguage ?? "简体中文";
        var provider = settings?.Translation.MachineTranslationProvider ?? "microsoft";
        var cacheKey = System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = useAi ? "ai" : "mt",
            explainLanguage,
            provider = useAi ? settings?.ActiveTranslationProfileId : provider,
            sentence = trimmedSentence,
            selection = selection?.Trim() ?? string.Empty
        });

        lock (_sentenceCacheLock)
        {
            if (_sentenceCacheSettingsVersion == cacheSettingsVersion
                && _sentenceCache.TryGetValue(cacheKey, out var cached))
            {
                _sentenceCacheOrder.Remove(cacheKey);
                _sentenceCacheOrder.AddLast(cacheKey);
                return cached;
            }
        }

        string translated;
        if (useAi)
        {
            translated = await _aiTranslation.TranslateSentenceAsync(trimmedSentence, selection, ct).ConfigureAwait(false);
        }
        else
        {
            if (_machineTranslation == null)
                throw new InvalidOperationException("在线翻译服务未初始化。");
            try
            {
                translated = await _machineTranslation.TranslateAsync(trimmedSentence, explainLanguage, provider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"在线快译失败：{ex.Message}", ex);
            }
        }

        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException(useAi ? "AI 翻译未返回结果。" : "在线快译未返回结果。请重试，或改用 AI。");

        lock (_sentenceCacheLock)
        {
            if (_sentenceCacheSettingsVersion != cacheSettingsVersion)
            {
                return translated;
            }

            if (_sentenceCache.ContainsKey(cacheKey))
            {
                _sentenceCacheOrder.Remove(cacheKey);
            }
            else if (_sentenceCache.Count >= MaxSentenceCacheSize && _sentenceCacheOrder.First is { } oldest)
            {
                _sentenceCache.Remove(oldest.Value);
                _sentenceCacheOrder.RemoveFirst();
            }

            _sentenceCache[cacheKey] = translated;
            _sentenceCacheOrder.AddLast(cacheKey);
        }

        return translated;
    }

    private async Task<TranslationResult> LookupCoreAsync(
        CaptureResult capture,
        bool forceAi,
        IProgress<TranslationResult>? progress,
        bool bypassCache,
        CancellationToken ct = default)
    {
        var text = capture.SelectedText.Trim();
        var textInfo = DescribeText(text);
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("选中文本为空，无法查词。", nameof(capture));
        }

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var isShortText = tokens.Length <= 2;

        // 1. If offline dict is available and not forceAi and input is short word -> attempt offline query
        var dictionaryEnabled = _settingsService?.Current.Dictionary.Enabled ?? true;
        if (!forceAi && isShortText && dictionaryEnabled && _offlineDict.IsInstalled)
        {
            try
            {
                var dictResult = await _offlineDict.LookupAsync(text, ct).ConfigureAwait(false);
                if (dictResult != null)
                {
                    _logger?.LogInformation("Offline dictionary hit for selected text {TextInfo}", textInfo);
                    return dictResult;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Offline dictionary lookup failed for selected text {TextInfo}, continuing with machine translation", textInfo);
            }
        }

        // 2. Misses and sentences use the selected quick-translation provider.
        // AI is only requested by the explicit "AI 详解" action.
        if (!forceAi)
        {
            if (_machineTranslation == null)
                throw new InvalidOperationException("在线快译服务未初始化。请重试，或改用 AI 详解。");

            string translated;
            try
            {
                var settings = _settingsService?.Current;
                translated = await _machineTranslation.TranslateAsync(
                    text,
                    settings?.ExplainLanguage ?? "简体中文",
                    settings?.Translation.MachineTranslationProvider ?? "microsoft",
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Machine translation failed for selected text {TextInfo}", textInfo);
                throw new InvalidOperationException($"在线快译失败：{ex.Message}", ex);
            }

            if (string.IsNullOrWhiteSpace(translated))
                throw new InvalidOperationException("在线快译未返回结果。请重试，或改用 AI 详解。");

            _logger?.LogInformation("Machine translation hit for selected text {TextInfo}", textInfo);
            return new TranslationResult(
                Word: text,
                Reading: string.Empty,
                PartOfSpeech: string.Empty,
                Definition: translated,
                ContextTranslation: string.Empty,
                MemoryHook: null,
                Source: LookupSource.MachineTranslation,
                SourceEntryId: null,
                SourceSchemaVersion: null);
        }

        // 3. Explicit AI lookup (with LRU cache + offline dictionary MemoryHook/Reading backfill).
        var cacheKey = BuildAiCacheKey(capture, text);
        var useLookupCache = _aiTranslation is not IStreamingTranslationService;
        if (useLookupCache && !bypassCache && TryGetCachedAiResult(cacheKey, out var cachedResult))
        {
            _logger?.LogInformation("AI translation cache hit for selected text {TextInfo}", textInfo);
            return cachedResult;
        }

        _logger?.LogInformation("Using AI translation for selected text {TextInfo} (forceAi={ForceAi}, isShort={IsShort})", textInfo, forceAi, isShortText);
        var aiResult = _aiTranslation is IStreamingTranslationService streaming && progress != null
            ? await streaming.TranslateStreamingAsync(capture, progress, bypassCache, ct).ConfigureAwait(false)
            : await _aiTranslation.TranslateAsync(capture, ct).ConfigureAwait(false);

        if (forceAi && isShortText && dictionaryEnabled && _offlineDict.IsInstalled
            && (string.IsNullOrWhiteSpace(aiResult.MemoryHook) || string.IsNullOrWhiteSpace(aiResult.Reading)))
        {
            try
            {
                var dictFallback = await _offlineDict.LookupAsync(
                    !string.IsNullOrWhiteSpace(aiResult.Word) ? aiResult.Word : text,
                    ct).ConfigureAwait(false);
                if (dictFallback != null)
                {
                    aiResult = aiResult with
                    {
                        Reading = !string.IsNullOrWhiteSpace(aiResult.Reading) ? aiResult.Reading : dictFallback.Reading,
                        MemoryHook = !string.IsNullOrWhiteSpace(aiResult.MemoryHook) ? aiResult.MemoryHook : dictFallback.MemoryHook,
                        PosGroups = aiResult.PosGroups is { Count: > 0 } ? aiResult.PosGroups : dictFallback.PosGroups
                    };
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger?.LogDebug(ex, "Optional offline dictionary backfill skipped after AI lookup");
            }
        }

        if (useLookupCache) StoreCachedAiResult(cacheKey, aiResult);
        return aiResult;
    }

    private string BuildAiCacheKey(CaptureResult capture, string trimmedText)
    {
        var settings = _settingsService?.Current;
        var active = settings?.ActiveTranslation;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            settings?.ActiveTranslationProfileId, active?.BaseUrl, active?.Model,
            settings?.ExplainLanguage, capture.Sentence, Text = trimmedText
        });
    }

    private bool TryGetCachedAiResult(string key, out TranslationResult result)
    {
        lock (_aiCacheLock)
        {
            if (_aiCache.TryGetValue(key, out result!))
            {
                _aiCacheOrder.Remove(key);
                _aiCacheOrder.AddLast(key);
                return true;
            }
        }

        result = null!;
        return false;
    }

    private void StoreCachedAiResult(string key, TranslationResult result)
    {
        lock (_aiCacheLock)
        {
            if (_aiCache.ContainsKey(key))
            {
                _aiCacheOrder.Remove(key);
            }
            else if (_aiCache.Count >= MaxAiCacheSize && _aiCacheOrder.First is { } oldest)
            {
                _aiCache.Remove(oldest.Value);
                _aiCacheOrder.RemoveFirst();
            }

            _aiCache[key] = result;
            _aiCacheOrder.AddLast(key);
        }
    }

    public Task<TranslationResult> LookupStreamingAsync(CaptureResult capture, bool forceAi,
        IProgress<TranslationResult> progress, bool bypassCache = false, CancellationToken ct = default)
        => LookupCoreAsync(capture, forceAi, progress, bypassCache, ct);

    private static string DescribeText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        return $"length={text.Length},sha256={hash}";
    }
}

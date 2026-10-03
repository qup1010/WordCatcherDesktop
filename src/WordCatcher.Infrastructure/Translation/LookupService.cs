using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Translation;

public sealed class LookupService : ILookupService
{
    private readonly IOfflineDictionaryService _offlineDict;
    private readonly ITranslationService _aiTranslation;
    private readonly ILogger<LookupService>? _logger;
    private readonly ISettingsService? _settingsService;
    private readonly IMachineTranslationService? _machineTranslation;

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
    }

    public async Task<TranslationResult> LookupAsync(
        CaptureResult capture,
        bool forceAi,
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
                _logger?.LogWarning(ex, "Offline dictionary lookup failed for selected text {TextInfo}, falling back to AI", textInfo);
            }
        }

        // 2. Prefer free machine translation for misses and long text. AI is
        // still available through the explicit "AI 详解" action and remains a
        // final fallback when machine translation is unavailable.
        if (!forceAi && _machineTranslation != null)
        {
            try
            {
                var settings = _settingsService?.Current;
                var translated = await _machineTranslation.TranslateAsync(
                    text,
                    settings?.ExplainLanguage ?? "简体中文",
                    settings?.ActiveTranslation.MachineTranslationProvider ?? "microsoft",
                    ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    _logger?.LogInformation("Machine translation hit for selected text {TextInfo}", textInfo);
                    return new TranslationResult(
                        Word: text,
                        Reading: string.Empty,
                        PartOfSpeech: string.Empty,
                        Definition: translated,
                        ContextTranslation: translated,
                        MemoryHook: null,
                        Source: LookupSource.MachineTranslation,
                        SourceEntryId: null,
                        SourceSchemaVersion: null);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Machine translation failed for selected text {TextInfo}, falling back to AI", textInfo);
            }
        }

        // 3. Final fallback to online AI translation
        _logger?.LogInformation("Using AI translation for selected text {TextInfo} (forceAi={ForceAi}, isShort={IsShort})", textInfo, forceAi, isShortText);
        return await _aiTranslation.TranslateAsync(capture, ct).ConfigureAwait(false);
    }

    private static string DescribeText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        return $"length={text.Length},sha256={hash}";
    }
}

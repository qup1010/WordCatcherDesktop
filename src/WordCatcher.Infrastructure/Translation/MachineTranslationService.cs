using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Interfaces;

namespace WordCatcher.Infrastructure.Translation;

public sealed class MachineTranslationService : IMachineTranslationService
{
    private sealed record CachedMicrosoftToken(string Value, DateTimeOffset ExpiresAt);

    private static readonly SemaphoreSlim MicrosoftTokenGate = new(1, 1);
    private static CachedMicrosoftToken? _microsoftToken;

    private readonly HttpClient _httpClient;

    public MachineTranslationService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        string provider = "microsoft",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var target = ResolveTargetCodes(targetLanguage);
        var engines = provider?.ToLowerInvariant() switch
        {
            "google" => new[] { "google", "microsoft", "bilibili" },
            "bilibili" or "index-translate" => new[] { "bilibili", "microsoft", "google" },
            _ => new[] { "microsoft", "bilibili", "google" }
        };
        var reasons = new List<string>(engines.Length);
        var errors = new List<Exception>(engines.Length);
        foreach (var engine in engines)
        {
            ct.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(TimeSpan.FromSeconds(engine == "bilibili" ? 10 : 6));
            try
            {
                var result = await (engine switch
                {
                    "google" => TranslateGoogleAsync(text, target.Google, attempt.Token),
                    "bilibili" => TranslateBilibiliAsync(text, target.DisplayName, attempt.Token),
                    _ => TranslateMicrosoftAsync(text, target.Microsoft, attempt.Token)
                }).ConfigureAwait(false);
                attempt.Token.ThrowIfCancellationRequested();
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                or TranslationResponseException or JsonException)
            {
                ct.ThrowIfCancellationRequested();
                var name = engine switch
                {
                    "google" => "谷歌翻译",
                    "bilibili" => "B站 Index-Translate",
                    _ => "微软翻译"
                };
                var detail = ex switch
                {
                    OperationCanceledException => "请求超时",
                    HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
                    HttpRequestException or IOException => "网络请求失败",
                    JsonException => "返回的数据无法解析",
                    _ => ex.Message
                };
                reasons.Add($"{name}（{detail.TrimEnd('。')}）");
                errors.Add(ex);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            $"在线快译主引擎及备用引擎均不可用：{string.Join("；", reasons)}。",
            new AggregateException(errors));
    }

    private sealed class TranslationResponseException(string message) : Exception(message);

    private async Task<string> TranslateGoogleAsync(string text, string target, CancellationToken ct)
    {
        var baseParams = $"client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&ie=UTF-8&oe=UTF-8&dt=t&dt=bd";
        var query = $"{baseParams}&q={Uri.EscapeDataString(text)}";
        using var response = query.Length <= 1500
            ? await _httpClient.GetAsync(
                $"https://translate.googleapis.com/translate_a/single?{query}",
                ct).ConfigureAwait(false)
            : await _httpClient.PostAsync(
                $"https://translate.googleapis.com/translate_a/single?{baseParams}",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]),
                ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0
            || document.RootElement[0].ValueKind != JsonValueKind.Array)
        {
            throw new TranslationResponseException("返回的内容无法解析");
        }

        var builder = new StringBuilder();
        foreach (var chunk in document.RootElement[0].EnumerateArray())
        {
            if (chunk.ValueKind == JsonValueKind.Array
                && chunk.GetArrayLength() > 0
                && chunk[0].ValueKind == JsonValueKind.String)
            {
                builder.Append(chunk[0].GetString());
            }
        }

        var primary = builder.ToString().Trim();
        var dictLines = TryParseGoogleDictionaryEntries(document.RootElement);
        if (dictLines.Count > 0)
        {
            var dictSummary = string.Join("；", dictLines);
            return string.IsNullOrWhiteSpace(primary)
                ? dictSummary
                : $"{primary}\n{dictSummary}";
        }

        if (string.IsNullOrWhiteSpace(primary))
            throw new TranslationResponseException("未返回译文");
        return primary;
    }

    private static List<string> TryParseGoogleDictionaryEntries(JsonElement root)
    {
        var lines = new List<string>();
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() <= 1 || root[1].ValueKind != JsonValueKind.Array)
            return lines;

        foreach (var posBlock in root[1].EnumerateArray())
        {
            if (posBlock.ValueKind != JsonValueKind.Array || posBlock.GetArrayLength() < 2)
                continue;

            var pos = posBlock[0].ValueKind == JsonValueKind.String ? posBlock[0].GetString()?.Trim() : null;
            if (posBlock[1].ValueKind != JsonValueKind.Array)
                continue;

            var terms = new List<string>();
            foreach (var termEl in posBlock[1].EnumerateArray())
            {
                if (termEl.ValueKind == JsonValueKind.String)
                {
                    var term = termEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(term))
                    {
                        terms.Add(term);
                        if (terms.Count >= 4) break;
                    }
                }
            }

            if (terms.Count > 0)
            {
                var prefix = !string.IsNullOrWhiteSpace(pos) ? $"{AbbreviatePartOfSpeech(pos)} " : string.Empty;
                lines.Add($"{prefix}{string.Join("，", terms)}");
                if (lines.Count >= 3) break;
            }
        }

        return lines;
    }

    private static string AbbreviatePartOfSpeech(string pos)
    {
        return pos.ToLowerInvariant() switch
        {
            "noun" => "n.",
            "verb" => "v.",
            "adjective" => "adj.",
            "adverb" => "adv.",
            "preposition" => "prep.",
            "conjunction" => "conj.",
            "pronoun" => "pron.",
            "interjection" => "interj.",
            _ => pos
        };
    }

    private async Task<string> TranslateMicrosoftAsync(string text, string target, CancellationToken ct)
    {
        var token = await GetMicrosoftTokenAsync(ct).ConfigureAwait(false);
        var response = await SendMicrosoftTranslateAsync(text, target, token, ct).ConfigureAwait(false);
        if ((int)response.StatusCode == 401)
        {
            response.Dispose();
            await InvalidateMicrosoftTokenAsync(token, ct).ConfigureAwait(false);
            token = await GetMicrosoftTokenAsync(ct).ConfigureAwait(false);
            response = await SendMicrosoftTranslateAsync(text, target, token, ct).ConfigureAwait(false);
        }

        using (response)
        {
            if ((int)response.StatusCode == 401)
            {
                await InvalidateMicrosoftTokenAsync(token, ct).ConfigureAwait(false);
                throw new TranslationResponseException("刷新凭据后仍未通过身份验证");
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array
                || root.GetArrayLength() == 0
                || root[0].ValueKind != JsonValueKind.Object
                || !root[0].TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0
                || translations[0].ValueKind != JsonValueKind.Object
                || !translations[0].TryGetProperty("text", out var translatedText)
                || translatedText.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new TranslationResponseException("返回的内容无法解析");
            }

            var result = translatedText.GetString();
            if (string.IsNullOrWhiteSpace(result))
                throw new TranslationResponseException("未返回译文");
            return result;
        }
    }

    private async Task<HttpResponseMessage> SendMicrosoftTranslateAsync(string text, string target, string token, CancellationToken ct)
    {
        var query = $"to={Uri.EscapeDataString(target)}&api-version=3.0&textType=plain";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api-edge.cognitive.microsofttranslator.com/translate?{query}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }),
            Encoding.UTF8,
            "application/json");

        return await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task InvalidateMicrosoftTokenAsync(string failedToken, CancellationToken ct)
    {
        await MicrosoftTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (string.Equals(_microsoftToken?.Value, failedToken, StringComparison.Ordinal))
            {
                _microsoftToken = null;
            }
        }
        finally
        {
            MicrosoftTokenGate.Release();
        }
    }

    private async Task<string> GetMicrosoftTokenAsync(CancellationToken ct)
    {
        var cached = _microsoftToken;
        if (cached != null
            && !string.IsNullOrWhiteSpace(cached.Value)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.Value;
        }

        await MicrosoftTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _microsoftToken;
            if (cached != null
                && !string.IsNullOrWhiteSpace(cached.Value)
                && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return cached.Value;
            }

            using var response = await _httpClient.GetAsync(
                "https://edge.microsoft.com/translate/auth",
                ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var token = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new TranslationResponseException("未返回认证凭据");

            _microsoftToken = new CachedMicrosoftToken(token, ParseTokenExpiry(token));
            return token;
        }
        finally
        {
            MicrosoftTokenGate.Release();
        }
    }

    private static DateTimeOffset ParseTokenExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length > 1)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                if (document.RootElement.TryGetProperty("exp", out var exp)
                    && exp.TryGetInt64(out var unixSeconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
            }
        }
        catch
        {
            // Fall back to the conservative lifetime used by the browser client.
        }

        return DateTimeOffset.UtcNow.AddMinutes(8);
    }

    private async Task<string> TranslateBilibiliAsync(string text, string targetLanguageName, CancellationToken ct)
    {
        var prompt = $"请将以下文本翻译为{targetLanguageName}，直接输出翻译结果，不要进行任何解释。\n\n{text}";
        var payload = new
        {
            model = "Index-Translate-35B-A3B",
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = 0,
            max_tokens = 1024,
            chat_template_kwargs = new { enable_thinking = false }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://index-translate.bilibili.com/v1/chat/completions");
        request.Headers.TryAddWithoutValidation("User-Agent", "WordCatcherDesktop");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode == 429)
            throw new TranslationResponseException("请求过于频繁（HTTP 429）");
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
            || choices[0].ValueKind != JsonValueKind.Object)
        {
            throw new TranslationResponseException("返回的内容无法解析");
        }

        var firstChoice = choices[0];
        if (firstChoice.TryGetProperty("finish_reason", out var finishReason)
            && finishReason.ValueKind == JsonValueKind.String
            && string.Equals(finishReason.GetString(), "length", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationResponseException("译文被截断");
        }

        if (!firstChoice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var contentEl)
            || contentEl.ValueKind != JsonValueKind.String)
        {
            throw new TranslationResponseException("返回的内容无法解析");
        }

        var raw = contentEl.GetString() ?? string.Empty;
        var cleaned = Regex.Replace(raw, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new TranslationResponseException("未返回译文");
        return cleaned;
    }

    private static (string Google, string Microsoft, string DisplayName) ResolveTargetCodes(string language)
    {
        var trimmed = (language ?? string.Empty).Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "繁體中文" or "繁体中文" or "zh-tw" or "zh-hant" => ("zh-TW", "zh-Hant", "繁体中文"),
            "english" or "en" or "en-us" or "en-gb" => ("en", "en", "英语"),
            "日本語" or "日语" or "ja" or "ja-jp" => ("ja", "ja", "日语"),
            "简体中文" or "zh-cn" or "zh-hans" or "" => ("zh-CN", "zh-Hans", "简体中文"),
            _ => ("zh-CN", "zh-Hans", trimmed)
        };
    }
}

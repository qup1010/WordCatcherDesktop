using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var target = ResolveTargetCodes(targetLanguage);
        try
        {
            return string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase)
                ? await TranslateGoogleAsync(text, target.Google, ct).ConfigureAwait(false)
                : await TranslateMicrosoftAsync(text, target.Microsoft, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new InvalidOperationException($"在线快译不可用：{ex.Message}", ex);
        }
    }

    private async Task<string> TranslateGoogleAsync(string text, string target, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        var baseParams = $"client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&ie=UTF-8&oe=UTF-8&dt=t&dt=bd";
        var query = $"{baseParams}&q={Uri.EscapeDataString(text)}";
        using var response = query.Length <= 1500
            ? await _httpClient.GetAsync(
                $"https://translate.googleapis.com/translate_a/single?{query}",
                timeoutCts.Token).ConfigureAwait(false)
            : await _httpClient.PostAsync(
                $"https://translate.googleapis.com/translate_a/single?{baseParams}",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]),
                timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0
            || document.RootElement[0].ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("谷歌翻译返回了无法解析的内容。");
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
            throw new InvalidOperationException("谷歌翻译未返回译文。");
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
                throw new InvalidOperationException("微软翻译 token 已失效。");
            }
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                throw new InvalidOperationException("微软翻译返回了无法解析的内容。");

            var result = root[0].GetProperty("translations")[0].GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("微软翻译未返回译文。");
            return result;
        }
    }

    private async Task<HttpResponseMessage> SendMicrosoftTranslateAsync(string text, string target, string token, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        var query = $"to={Uri.EscapeDataString(target)}&api-version=3.0&textType=plain";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api-edge.cognitive.microsofttranslator.com/translate?{query}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }),
            Encoding.UTF8,
            "application/json");

        return await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
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

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _httpClient.GetAsync(
                "https://edge.microsoft.com/translate/auth",
                timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var token = (await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("微软翻译未返回 token。");

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

    private static (string Google, string Microsoft) ResolveTargetCodes(string language)
    {
        return language switch
        {
            "繁體中文" => ("zh-TW", "zh-Hant"),
            "English" => ("en", "en"),
            "日本語" => ("ja", "ja"),
            _ => ("zh-CN", "zh-Hans")
        };
    }
}

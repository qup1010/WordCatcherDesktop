using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Interfaces;

namespace WordCatcher.Infrastructure.Translation;

public sealed class MachineTranslationService : IMachineTranslationService
{
    private static readonly SemaphoreSlim MicrosoftTokenGate = new(1, 1);
    private static string? _microsoftToken;
    private static DateTimeOffset _microsoftTokenExpiresAt;

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
        var first = string.Equals(provider, "google", StringComparison.OrdinalIgnoreCase)
            ? "google"
            : "microsoft";
        var second = first == "microsoft" ? "google" : "microsoft";

        Exception? firstError = null;
        try
        {
            return first == "microsoft"
                ? await TranslateMicrosoftAsync(text, target.Microsoft, ct).ConfigureAwait(false)
                : await TranslateGoogleAsync(text, target.Google, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            firstError = ex;
        }

        try
        {
            return second == "microsoft"
                ? await TranslateMicrosoftAsync(text, target.Microsoft, ct).ConfigureAwait(false)
                : await TranslateGoogleAsync(text, target.Google, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"免费机翻不可用：{firstError?.Message ?? ex.Message}", ex);
        }
    }

    private async Task<string> TranslateGoogleAsync(string text, string target, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        var query = $"client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t&dt=bd&q={Uri.EscapeDataString(text)}";
        using var response = await _httpClient.GetAsync(
            $"https://translate.googleapis.com/translate_a/single?{query}",
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

        var result = builder.ToString();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("谷歌翻译未返回译文。");
        return result;
    }

    private async Task<string> TranslateMicrosoftAsync(string text, string target, CancellationToken ct)
    {
        var token = await GetMicrosoftTokenAsync(ct).ConfigureAwait(false);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

        var query = $"to={Uri.EscapeDataString(target)}&api-version=3.0";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api-edge.cognitive.microsofttranslator.com/translate?{query}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new[] { new { Text = text } }),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if ((int)response.StatusCode == 401)
        {
            _microsoftToken = null;
            throw new InvalidOperationException("微软翻译 token 已失效。");
        }
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            throw new InvalidOperationException("微软翻译返回了无法解析的内容。");

        var result = root[0].GetProperty("translations")[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("微软翻译未返回译文。");
        return result;
    }

    private async Task<string> GetMicrosoftTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_microsoftToken)
            && _microsoftTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _microsoftToken;
        }

        await MicrosoftTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_microsoftToken)
                && _microsoftTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _microsoftToken;
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

            _microsoftToken = token;
            _microsoftTokenExpiresAt = ParseTokenExpiry(token);
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

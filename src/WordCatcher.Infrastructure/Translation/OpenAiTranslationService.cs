using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Translation;

public sealed class OpenAiTranslationService : ITranslationService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<OpenAiTranslationService>? _logger;

    public OpenAiTranslationService(
        HttpClient httpClient,
        ISettingsService settingsService,
        ISecretStore secretStore,
        ILogger<OpenAiTranslationService>? logger = null)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _secretStore = secretStore;
        _logger = logger;
    }

    public async Task<TranslationResult> TranslateAsync(CaptureResult capture, CancellationToken ct = default)
    {
        var settings = _settingsService.Current;
        var apiKey = _secretStore is IProfileSecretStore profiles
            ? await profiles.GetApiKeyAsync(settings.ActiveTranslationProfileId, ct).ConfigureAwait(false)
            : await _secretStore.GetApiKeyAsync(ct).ConfigureAwait(false);
        var translation = settings.ActiveTranslation;

        return await TranslateCoreAsync(
            capture,
            translation.BaseUrl,
            translation.Model,
            translation.TimeoutSeconds,
            settings.ExplainLanguage,
            apiKey,
            ct).ConfigureAwait(false);
    }

    public Task<TranslationResult> TranslateWithConfigurationAsync(
        CaptureResult capture,
        string baseUrl,
        string model,
        int timeoutSeconds,
        string explainLanguage,
        string apiKey,
        CancellationToken ct = default)
    {
        return TranslateCoreAsync(
            capture,
            baseUrl,
            model,
            timeoutSeconds,
            explainLanguage,
            apiKey,
            ct);
    }

    private async Task<TranslationResult> TranslateCoreAsync(
        CaptureResult capture,
        string? configuredBaseUrl,
        string? configuredModel,
        int configuredTimeoutSeconds,
        string? explainLanguage,
        string? apiKey,
        CancellationToken ct)
    {

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("未配置翻译 API Key，请在设置页中输入。");
        }
        apiKey = apiKey.Trim();

        var baseUrl = (configuredBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("未配置翻译 Base URL，请在设置页中检查。");
        }

        var model = (configuredModel ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-4o-mini";
        }

        var timeoutSec = configuredTimeoutSeconds > 0 ? configuredTimeoutSeconds : 30;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var endpoint = $"{baseUrl}/chat/completions";
        var systemPrompt = BuildSystemPrompt(explainLanguage ?? "简体中文");
        var contextLine = !string.IsNullOrWhiteSpace(capture.Sentence)
            ? $"\n原句: {capture.Sentence}"
            : string.Empty;
        var sourceLine = !string.IsNullOrWhiteSpace(capture.SourceUri)
            ? $"\n来源地址: {capture.SourceUri}"
            : string.Empty;
        var userContent = $"选中文本: {capture.SelectedText}{contextLine}{sourceLine}\n来源窗口: {capture.SourceWindowTitle}";

        var requestBody = new
        {
            model = model,
            temperature = 0.2,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);

        // Attempt with single retry on transient error
        HttpResponseMessage? response = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                var status = (int)response.StatusCode;
                if (status is 400 or 401 or 402 or 403 or 404 or 429)
                {
                    // Non-retryable
                    break;
                }

                if (attempt == 0)
                {
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"AI 翻译请求超时（超过 {timeoutSec} 秒未响应）。");
            }
            catch (HttpRequestException ex)
            {
                if (attempt == 1)
                {
                    throw new InvalidOperationException($"无法连接翻译服务，请检查 Base URL 和网络: {ex.Message}", ex);
                }
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
        }

        if (response == null || !response.IsSuccessStatusCode)
        {
            var code = response?.StatusCode ?? HttpStatusCode.InternalServerError;
            var errBody = response != null ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : string.Empty;
            throw ClassifyError(code, errBody);
        }

        var respJson = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return ParseChatCompletion(respJson, capture.SelectedText);
    }

    private static string BuildSystemPrompt(string explainLanguage)
    {
        return $@"你是一个语言学习助手。用户会给你选中的词汇或句子，请以 JSON 格式输出：
1. word: 词条原形（时态/复数/比较级还原为原形字典形态；若是长句，保持原句）
2. reading: IPA 音标（如 /.../，无法确定或句子时为空字符串）
3. partOfSpeech: 词性简写（如 n. / v. / adj. / adv. / phr.，长句可为空）
4. definition: 该词在当前语境下的简明释义（必须使用{explainLanguage}）
5. contextTranslation: 选中文本或句子的自然通顺翻译（必须使用{explainLanguage}）
6. memoryHook: 可选记忆提示（联想/谐音/词根，若无则为空字符串）

请严格返回一个 JSON 对象，包含上述键名。";
    }

    private static Exception ClassifyError(HttpStatusCode statusCode, string responseBody)
    {
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new UnauthorizedAccessException("API Key 无效或已过期，请在设置页检查。"),
            HttpStatusCode.PaymentRequired => new InvalidOperationException("账户余额不足，请检查 API 额度。"),
            HttpStatusCode.NotFound => new InvalidOperationException("接口或模型不存在（404），请检查 Base URL 是否包含 /v1 以及模型名。"),
            HttpStatusCode.TooManyRequests => new InvalidOperationException("请求过于频繁（429），已被限流，请稍后重试。"),
            HttpStatusCode.BadRequest => new InvalidOperationException($"请求参数错误（400）: {responseBody}"),
            _ => new InvalidOperationException($"翻译服务返回错误 (HTTP {(int)statusCode}): {responseBody}")
        };
    }

    public static TranslationResult ParseChatCompletion(string chatResponseJson, string fallbackSelectedText)
    {
        using var doc = JsonDocument.Parse(chatResponseJson);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("模型未返回任何生成内容。");
        }

        var message = choices[0].GetProperty("message");
        var content = message.GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("模型返回的内容为空。");
        }

        var cleanContent = content.Trim();
        if (cleanContent.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleanContent = cleanContent["```json".Length..];
        }
        else if (cleanContent.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleanContent = cleanContent["```".Length..];
        }
        if (cleanContent.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleanContent = cleanContent[..^"```".Length];
        }

        var parsed = JsonSerializer.Deserialize<AiParsedEntry>(cleanContent.Trim(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("无法反序列化模型返回的 JSON 结构。");

        var word = !string.IsNullOrWhiteSpace(parsed.Word) ? parsed.Word.Trim() : fallbackSelectedText.Trim();
        var reading = parsed.Reading?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(reading))
        {
            reading = $"/{reading.Trim('/')}/";
        }

        var pos = parsed.PartOfSpeech?.Trim() ?? string.Empty;
        var definition = !string.IsNullOrWhiteSpace(parsed.Definition) ? parsed.Definition.Trim() : (parsed.ContextTranslation ?? string.Empty);
        var contextTrans = parsed.ContextTranslation?.Trim() ?? string.Empty;
        var memoryHook = !string.IsNullOrWhiteSpace(parsed.MemoryHook) ? parsed.MemoryHook.Trim() : null;

        return new TranslationResult(
            Word: word,
            Reading: reading,
            PartOfSpeech: pos,
            Definition: definition,
            ContextTranslation: contextTrans,
            MemoryHook: memoryHook,
            Source: LookupSource.Ai,
            SourceEntryId: null,
            SourceSchemaVersion: null,
            PosGroups: null);
    }

    private sealed class AiParsedEntry
    {
        [JsonPropertyName("word")]
        public string? Word { get; set; }

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
    }
}

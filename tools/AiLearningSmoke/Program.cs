using System.Text.Json;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Security;
using WordCatcher.Infrastructure.Settings;
using WordCatcher.Infrastructure.Translation;

// 使用已配置端点做少量真实质量检查；只输出分析结果，不输出凭据。
var settings = new JsonSettingsService();
var secrets = new DpapiSecretService();
var key = await secrets.GetApiKeyAsync(settings.Current.ActiveTranslationProfileId);
if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("SKIPPED: 当前用户没有配置可用的 AI API Key。"); return; }
var service = new OpenAiTranslationService(new HttpClient(), settings, secrets);
var samples = new[] {
    ("bank", "We sat on the bank of the river."),
    ("bank", "She deposited the money at the bank."),
    ("stride", "He took her criticism in stride."),
    ("apple", "She ate an apple."),
    ("in stride", "He took her criticism in stride."),
    ("He took her criticism in stride.", "He took her criticism in stride."),
    ("serendipity", "")
};
foreach (var (selection, sentence) in samples)
{
    var config = settings.Current.ActiveTranslation;
    try
    {
        var result = await service.TranslateWithConfigurationAsync(
            new CaptureResult(selection, "smoke", "Quality check", new(0, 0), DateTimeOffset.UtcNow, sentence),
            config.BaseUrl, config.Model, Math.Max(config.TimeoutSeconds, 60), settings.Current.ExplainLanguage, key);
        Console.WriteLine(JsonSerializer.Serialize(new { selection, sentence, result },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    catch (Exception ex) { Console.WriteLine($"FAILED: {selection}: {ex.Message}"); Environment.ExitCode = 1; }
}

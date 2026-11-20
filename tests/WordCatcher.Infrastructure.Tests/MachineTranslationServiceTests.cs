using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Infrastructure.Translation;

namespace WordCatcher.Infrastructure.Tests;

public class MachineTranslationServiceTests
{
    private const string MicrosoftHost = "api-edge.cognitive.microsofttranslator.com";
    private const string BilibiliHost = "index-translate.bilibili.com";
    private const string GoogleHost = "translate.googleapis.com";
    private const string TruncatedBilibiliResponse = """
        {"choices":[{"finish_reason":"length","message":{"content":"截断"}}]}
        """;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : null;
            return _respond(request, body);
        }
    }

    private static void ResetMicrosoftTokenCache()
    {
        var type = typeof(MachineTranslationService);
        type.GetField("_microsoftToken", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
    }

    [Fact]
    public async Task TranslateMicrosoftConsecutiveUnauthorizedClearsRejectedToken()
    {
        ResetMicrosoftTokenCache();
        var authCalls = 0;
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            if (req.RequestUri!.ToString() == "https://edge.microsoft.com/translate/auth")
            {
                authCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"token-{authCalls}", Encoding.UTF8, "text/plain")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }));

        var service = new MachineTranslationService(http);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync("retry", "简体中文", "microsoft"));

        var cached = typeof(MachineTranslationService)
            .GetField("_microsoftToken", BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null);
        Assert.Null(cached);
        Assert.Equal(2, authCalls);
    }

    [Fact]
    public async Task TranslateMicrosoftRetriesOnceOnUnauthorized()
    {
        ResetMicrosoftTokenCache();
        var authCalls = 0;
        var apiCalls = 0;
        using var http = new HttpClient(new StubHandler((req, _) =>
        {
            var uri = req.RequestUri!.ToString();
            if (uri == "https://edge.microsoft.com/translate/auth")
            {
                authCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"token-{authCalls}", Encoding.UTF8, "text/plain")
                };
            }

            apiCalls++;
            if (apiCalls == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"translations\":[{\"text\":\"重连成功\"}]}]", Encoding.UTF8, "application/json")
            };
        }));

        var service = new MachineTranslationService(http);
        var result = await service.TranslateAsync("retry", "简体中文", "microsoft");

        Assert.Equal("重连成功", result);
        Assert.Equal(2, authCalls);
        Assert.Equal(2, apiCalls);
    }

    [Fact]
    public async Task TranslateGoogleUsesPostFormForLongText()
    {
        HttpMethod? seenMethod = null;
        string? seenUri = null;
        string? seenBody = null;
        using var http = new HttpClient(new StubHandler((req, body) =>
        {
            seenMethod = req.Method;
            seenUri = req.RequestUri!.ToString();
            seenBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[[[\"长句译文\",\"source\",null]],null,\"en\"]", Encoding.UTF8, "application/json")
            };
        }));

        var longText = string.Join(" ", Enumerable.Repeat("segment", 300));
        var service = new MachineTranslationService(http);
        var result = await service.TranslateAsync(longText, "简体中文", "google");

        Assert.Equal("长句译文", result);
        Assert.Equal(HttpMethod.Post, seenMethod);
        Assert.DoesNotContain("&q=", seenUri);
        Assert.Contains("q=", seenBody);
    }

    [Fact]
    public async Task TranslateBilibiliUsesIndexTranslateEndpointAndStripsThinkBlock()
    {
        string? seenUri = null;
        string? seenUserAgent = null;
        string? seenBody = null;
        using var http = new HttpClient(new StubHandler((req, body) =>
        {
            seenUri = req.RequestUri!.ToString();
            seenUserAgent = req.Headers.UserAgent.ToString();
            seenBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"<think>reasoning</think>你好，世界！\"}}]}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var service = new MachineTranslationService(http);
        var result = await service.TranslateAsync("Hello, world!", "简体中文", "bilibili");

        Assert.Equal("你好，世界！", result);
        Assert.Equal("https://index-translate.bilibili.com/v1/chat/completions", seenUri);
        Assert.Contains("WordCatcherDesktop", seenUserAgent);
        Assert.Contains("Index-Translate-35B-A3B", seenBody);
        Assert.Contains("\"enable_thinking\":false", seenBody);
        Assert.Contains("Hello, world!", seenBody);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("format")]
    public async Task TranslateMicrosoftFallsBackToBilibili(string failure)
    {
        ResetMicrosoftTokenCache();
        var engines = new List<string>();
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            if (host == "edge.microsoft.com")
                return Reply("test-token");

            engines.Add(host);
            return host == MicrosoftHost
                ? failure switch
                {
                    "network" => throw new HttpRequestException("Network error"),
                    "timeout" => throw new OperationCanceledException(),
                    _ => Reply("""[{"translations":[]}]""")
                }
                : Reply("""{"choices":[{"message":{"content":"备用译文"}}]}""");
        }));

        var result = await new MachineTranslationService(http).TranslateAsync("test", "简体中文", "microsoft");

        Assert.Equal("备用译文", result);
        Assert.Equal(new[] { MicrosoftHost, BilibiliHost }, engines);
    }

    [Theory]
    [InlineData(429, "")]
    [InlineData(200, TruncatedBilibiliResponse)]
    [InlineData(200, """{"choices":[{"message":{"content":" "}}]}""")]
    public async Task TranslateBilibiliFallsBackToMicrosoft(int status, string body)
    {
        ResetMicrosoftTokenCache();
        var engines = new List<string>();
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            if (host == "edge.microsoft.com")
                return Reply("test-token");

            engines.Add(host);
            return host == BilibiliHost
                ? Reply(body, (HttpStatusCode)status)
                : Reply("""[{"translations":[{"text":"备用译文"}]}]""");
        }));

        var result = await new MachineTranslationService(http).TranslateAsync("test", "简体中文", "bilibili");

        Assert.Equal("备用译文", result);
        Assert.Equal(new[] { BilibiliHost, MicrosoftHost }, engines);
    }

    [Fact]
    public async Task TranslateReportsAllFailuresInAttemptOrder()
    {
        ResetMicrosoftTokenCache();
        var engines = new List<string>();
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            if (host == "edge.microsoft.com")
                return Reply("test-token");

            engines.Add(host);
            return host switch
            {
                GoogleHost => Reply("", HttpStatusCode.ServiceUnavailable),
                MicrosoftHost => Reply("{not-json"),
                _ => Reply(TruncatedBilibiliResponse)
            };
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MachineTranslationService(http).TranslateAsync("test", "简体中文", "google"));

        Assert.Equal(new[] { GoogleHost, MicrosoftHost, BilibiliHost }, engines);
        Assert.Contains("主引擎及备用引擎均不可用", error.Message);
        Assert.Contains("谷歌翻译（HTTP 503）", error.Message);
        Assert.Contains("微软翻译（返回的数据无法解析）", error.Message);
        Assert.Contains("B站 Index-Translate（译文被截断）", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationStopsBeforeAnyFallback(bool cancelBeforeRequest)
    {
        using var caller = new CancellationTokenSource();
        var engines = new List<string>();
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            engines.Add(request.RequestUri!.Host);
            caller.Cancel();
            throw new HttpRequestException("Network error during cancellation");
        }));
        if (cancelBeforeRequest)
            caller.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MachineTranslationService(http).TranslateAsync("test", "简体中文", "google", caller.Token));

        Assert.Equal(caller.Token, error.CancellationToken);
        Assert.Equal(cancelBeforeRequest ? Array.Empty<string>() : new[] { GoogleHost }, engines);
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };
}

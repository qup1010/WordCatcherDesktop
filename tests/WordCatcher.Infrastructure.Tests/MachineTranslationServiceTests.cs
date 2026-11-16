using System;
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
}

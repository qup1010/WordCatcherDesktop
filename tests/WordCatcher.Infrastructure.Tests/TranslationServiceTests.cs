using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Translation;
using Xunit;

namespace WordCatcher.Infrastructure.Tests;

public class TranslationServiceTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }

    private sealed class MemorySettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public event Action<AppSettings>? SettingsChanged;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(Current);
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        public string Key { get; set; } = "test_key";
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(Key);
        public Task SetApiKeyAsync(string apiKey, CancellationToken ct = default)
        {
            Key = apiKey;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TranslateAsync_ParsesJsonAndReturnsTranslationResult()
    {
        var jsonResponse = @"{
  ""choices"": [
    {
      ""message"": {
        ""content"": ""{\""word\"": \""devastate\"", \""reading\"": \""devəsteɪt\"", \""partOfSpeech\"": \""v.\"", \""definition\"": \""使极度震惊、悲痛\"", \""contextTranslation\"": \""这消息让他极度震惊。\"", \""memoryHook\"": \""de + vast\""}""
      }
    }
  ]
}";

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("test_key", req.Headers.Authorization?.Parameter);
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse)
            };
            return Task.FromResult(res);
        });

        var client = new HttpClient(handler);
        var settings = new MemorySettingsService();
        var secrets = new MemorySecretStore();
        var service = new OpenAiTranslationService(client, settings, secrets);

        var capture = new CaptureResult("devastated", "notepad.exe", "Notes", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        var result = await service.TranslateAsync(capture);

        Assert.Equal("devastate", result.Word);
        Assert.Equal("/devəsteɪt/", result.Reading);
        Assert.Equal("v.", result.PartOfSpeech);
        Assert.Equal("使极度震惊、悲痛", result.Definition);
        Assert.Equal("这消息让他极度震惊。", result.ContextTranslation);
        Assert.Equal("de + vast", result.MemoryHook);
        Assert.Equal(LookupSource.Ai, result.Source);
    }

    [Fact]
    public async Task TranslateAsync_Handles401Unauthorized()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\": \"invalid_api_key\"}")
            });
        });

        var client = new HttpClient(handler);
        var settings = new MemorySettingsService();
        var secrets = new MemorySecretStore();
        var service = new OpenAiTranslationService(client, settings, secrets);

        var capture = new CaptureResult("test", "notepad.exe", "Notes", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.TranslateAsync(capture));
    }
}

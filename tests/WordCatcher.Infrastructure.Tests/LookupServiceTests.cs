using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Translation;

namespace WordCatcher.Infrastructure.Tests;

public sealed class LookupServiceTests
{
    [Fact]
    public async Task ShortText_UsesOfflineDictionaryWithoutCallingAi()
    {
        var offline = new FakeOfflineDictionary
        {
            Result = CreateResult(LookupSource.OpenDictionary)
        };
        var ai = new FakeTranslationService();
        var service = new LookupService(offline, ai);

        var result = await service.LookupAsync(CreateCapture("quick"), forceAi: false);

        Assert.Equal(LookupSource.OpenDictionary, result.Source);
        Assert.Equal(1, offline.LookupCount);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task OfflineLookupFailure_FallsBackToAi()
    {
        var offline = new FakeOfflineDictionary
        {
            ThrowOnLookup = true
        };
        var ai = new FakeTranslationService
        {
            Result = CreateResult(LookupSource.Ai)
        };
        var service = new LookupService(offline, ai);

        var result = await service.LookupAsync(CreateCapture("quick"), forceAi: false);

        Assert.Equal(LookupSource.Ai, result.Source);
        Assert.Equal(1, offline.LookupCount);
        Assert.Equal(1, ai.CallCount);
    }

    [Fact]
    public async Task DictionaryMiss_UsesMachineTranslationBeforeAi()
    {
        var offline = new FakeOfflineDictionary { Result = null };
        var machine = new FakeMachineTranslationService { Result = "快速翻译结果" };
        var ai = new FakeTranslationService { Result = CreateResult(LookupSource.Ai) };
        var service = new LookupService(offline, ai, machineTranslation: machine);

        var result = await service.LookupAsync(CreateCapture("unfamiliar"), forceAi: false);

        Assert.Equal(LookupSource.MachineTranslation, result.Source);
        Assert.Equal("快速翻译结果", result.Definition);
        Assert.Equal(1, machine.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    private static CaptureResult CreateCapture(string text) =>
        new(text, "test.exe", "Test", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);

    private static TranslationResult CreateResult(LookupSource source) =>
        new("quick", "/kwɪk/", "adj.", "快速的", string.Empty, null, source, null, null);

    private sealed class FakeOfflineDictionary : IOfflineDictionaryService
    {
        public bool IsInstalled => true;
        public DictionaryMetadata? Metadata => null;
        public int LookupCount { get; private set; }
        public bool ThrowOnLookup { get; init; }
        public TranslationResult? Result { get; init; }

        public Task<TranslationResult?> LookupAsync(string text, CancellationToken ct = default)
        {
            LookupCount++;
            if (ThrowOnLookup)
                throw new InvalidOperationException("fixture lookup failure");

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeTranslationService : ITranslationService
    {
        public int CallCount { get; private set; }
        public TranslationResult Result { get; init; } = CreateResult(LookupSource.Ai);

        public Task<TranslationResult> TranslateAsync(CaptureResult capture, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeMachineTranslationService : IMachineTranslationService
    {
        public int CallCount { get; private set; }
        public string Result { get; init; } = string.Empty;

        public Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            string provider = "microsoft",
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Translation;
using Xunit;

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
    public async Task OfflineLookupFailure_ContinuesToMachineTranslationWithoutCallingAi()
    {
        var offline = new FakeOfflineDictionary
        {
            ThrowOnLookup = true
        };
        var machine = new FakeMachineTranslationService { Result = "快速翻译结果" };
        var ai = new FakeTranslationService { Result = CreateResult(LookupSource.Ai) };
        var service = new LookupService(offline, ai, machineTranslation: machine);

        var result = await service.LookupAsync(CreateCapture("quick"), forceAi: false);

        Assert.Equal(LookupSource.MachineTranslation, result.Source);
        Assert.Equal(1, offline.LookupCount);
        Assert.Equal(1, machine.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task DictionaryMiss_UsesMachineTranslationWithoutCallingAi_AndLeavesContextTranslationEmptyByDefault()
    {
        var offline = new FakeOfflineDictionary { Result = null };
        var machine = new FakeMachineTranslationService { Result = "快速翻译结果" };
        var ai = new FakeTranslationService { Result = CreateResult(LookupSource.Ai) };
        var service = new LookupService(offline, ai, machineTranslation: machine);

        var result = await service.LookupAsync(CreateCapture("unfamiliar"), forceAi: false);

        Assert.Equal(LookupSource.MachineTranslation, result.Source);
        Assert.Equal("快速翻译结果", result.Definition);
        Assert.Equal(string.Empty, result.ContextTranslation);
        Assert.Equal(1, machine.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task TranslateSentenceAsync_AlwaysUsesSelectedQuickProviderAndCachesRepeatedRequests()
    {
        var settings = new FakeSettingsService();
        var machine = new FakeMachineTranslationService { Result = "机翻整句结果" };
        var ai = new FakeTranslationService { SentenceResult = "AI 整句结果" };
        var service = new LookupService(new FakeOfflineDictionary(), ai, settingsService: settings, machineTranslation: machine);

        settings.Current.ExplainLanguage = "English";
        settings.Current.Translation.MachineTranslationProvider = "google";
        var mtFirst = await service.TranslateSentenceAsync("We sat on the bank.", "bank");
        var mtSecond = await service.TranslateSentenceAsync("We sat on the bank.", "bank");
        Assert.Equal("机翻整句结果", mtFirst);
        Assert.Equal("机翻整句结果", mtSecond);
        Assert.Equal(1, machine.CallCount);
        Assert.Equal(0, ai.SentenceCallCount);
        Assert.Equal("English", machine.LastTargetLanguage);
        Assert.Equal("google", machine.LastProvider);
    }

    [Fact]
    public async Task TranslateSentenceWithAiAsync_UsesAiOnlyWhenExplicitlyRequested()
    {
        var settings = new FakeSettingsService();
        var machine = new FakeMachineTranslationService { Result = "机翻整句结果" };
        var ai = new FakeTranslationService { SentenceResult = "AI 整句结果" };
        var service = new LookupService(new FakeOfflineDictionary(), ai, settingsService: settings, machineTranslation: machine);

        var aiFirst = await service.TranslateSentenceWithAiAsync("We sat on the bank.", "bank");
        var aiSecond = await service.TranslateSentenceWithAiAsync("We sat on the bank.", "bank");
        Assert.Equal("AI 整句结果", aiFirst);
        Assert.Equal("AI 整句结果", aiSecond);
        Assert.Equal(1, ai.SentenceCallCount);
        Assert.Equal(0, machine.CallCount);
    }

    [Fact]
    public async Task TranslateSentenceAsync_ReportsQuickFailureWithoutCallingAi()
    {
        var machine = new FakeMachineTranslationService { Failure = new InvalidOperationException("provider unavailable") };
        var ai = new FakeTranslationService { SentenceResult = "AI 整句结果" };
        var service = new LookupService(new FakeOfflineDictionary(), ai, machineTranslation: machine);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateSentenceAsync("We sat on the bank.", "bank"));

        Assert.Contains("在线快译失败", error.Message);
        Assert.Equal(1, machine.CallCount);
        Assert.Equal(0, ai.SentenceCallCount);

        var aiResult = await service.TranslateSentenceWithAiAsync("We sat on the bank.", "bank");
        Assert.Equal("AI 整句结果", aiResult);
        Assert.Equal(1, ai.SentenceCallCount);
    }

    [Fact]
    public async Task TranslateSentenceAsync_ClearsCacheWhenSettingsChange()
    {
        var settings = new FakeSettingsService();
        var machine = new FakeMachineTranslationService { Result = "译文" };
        var service = new LookupService(new FakeOfflineDictionary(), new FakeTranslationService(), settingsService: settings, machineTranslation: machine);

        await service.TranslateSentenceAsync("The bank is closed.", "bank");
        await service.TranslateSentenceAsync("The bank is closed.", "bank");
        Assert.Equal(1, machine.CallCount);

        settings.NotifySettingsChanged();
        await service.TranslateSentenceAsync("The bank is closed.", "bank");

        Assert.Equal(2, machine.CallCount);
    }

    [Fact]
    public async Task TranslateSentenceAsync_DoesNotCacheAnInFlightResultAfterSettingsChange()
    {
        var settings = new FakeSettingsService();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = new FakeMachineTranslationService
        {
            Handler = () => pending.Task
        };
        var service = new LookupService(new FakeOfflineDictionary(), new FakeTranslationService(), settingsService: settings, machineTranslation: machine);

        var firstRequest = service.TranslateSentenceAsync("The bank is closed.", "bank");
        settings.NotifySettingsChanged();
        pending.SetResult("旧设置的译文");
        Assert.Equal("旧设置的译文", await firstRequest);

        machine.Handler = () => Task.FromResult("新设置的译文");
        var secondResult = await service.TranslateSentenceAsync("The bank is closed.", "bank");

        Assert.Equal("新设置的译文", secondResult);
        Assert.Equal(2, machine.CallCount);
    }

    [Fact]
    public async Task QuickTranslationFailure_DoesNotSilentlyFallBackToAi()
    {
        var offline = new FakeOfflineDictionary { Result = null };
        var machine = new FakeMachineTranslationService { Failure = new InvalidOperationException("provider unavailable") };
        var ai = new FakeTranslationService { Result = CreateResult(LookupSource.Ai) };
        var service = new LookupService(offline, ai, machineTranslation: machine);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.LookupAsync(CreateCapture("A sentence to translate."), forceAi: false));

        Assert.Contains("在线快译失败", error.Message);
        Assert.Equal(1, machine.CallCount);
        Assert.Equal(0, ai.CallCount);
    }

    [Fact]
    public async Task ForceAi_CachesRepeatedLookupsAndBackfillsMemoryHookFromOfflineDictionary()
    {
        var offline = new FakeOfflineDictionary
        {
            Result = CreateResult(LookupSource.OpenDictionary) with { MemoryHook = "词根：quick（快）" }
        };
        var ai = new FakeTranslationService
        {
            Result = CreateResult(LookupSource.Ai) with { MemoryHook = null }
        };
        var service = new LookupService(offline, ai);
        var capture = CreateCapture("quick");

        var first = await service.LookupAsync(capture, forceAi: true);
        var second = await service.LookupAsync(capture, forceAi: true);

        Assert.Equal(LookupSource.Ai, first.Source);
        Assert.Equal("词根：quick（快）", first.MemoryHook);
        Assert.Equal("词根：quick（快）", second.MemoryHook);
        Assert.Equal(1, ai.CallCount);
    }

    private static CaptureResult CreateCapture(string text) =>
        new(text, "test.exe", "Test", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);

    [Fact]
    public async Task StreamingLookupDelegatesCacheAndRegenerationToTranslationService()
    {
        var ai = new StreamingTranslationService();
        var service = new LookupService(new FakeOfflineDictionary(), ai);
        var progress = new Progress<TranslationResult>();
        var capture = CreateCapture("quick");
        await service.LookupStreamingAsync(capture, true, progress);
        await service.LookupStreamingAsync(capture, true, progress, bypassCache: true);
        await service.LookupStreamingAsync(capture, true, progress);
        Assert.Equal(new[] { false, true, false }, ai.BypassValues);
    }

    private sealed class StreamingTranslationService : IStreamingTranslationService
    {
        public List<bool> BypassValues { get; } = new();
        public Task<TranslationResult> TranslateAsync(CaptureResult capture, CancellationToken ct = default)
            => throw new InvalidOperationException("Expected streaming path.");
        public Task<TranslationResult> TranslateStreamingAsync(CaptureResult capture,
            IProgress<TranslationResult> progress, bool bypassCache = false, CancellationToken ct = default)
        {
            BypassValues.Add(bypassCache);
            return Task.FromResult(CreateResult(LookupSource.Ai));
        }
    }

    private static TranslationResult CreateResult(LookupSource source) =>
        new("quick", "/kwɪk/", "adj.", "快速的", string.Empty, null, source, null, null);

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; } = new();
        public event Action<AppSettings>? SettingsChanged;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
        public void NotifySettingsChanged() => SettingsChanged?.Invoke(Current);
    }

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
        public int SentenceCallCount { get; private set; }
        public TranslationResult Result { get; init; } = CreateResult(LookupSource.Ai);
        public string SentenceResult { get; init; } = string.Empty;
        public Func<Task<string>>? SentenceHandler { get; set; }

        public Task<TranslationResult> TranslateAsync(CaptureResult capture, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }

        public Task<string> TranslateSentenceAsync(string sentence, string? selection = null, CancellationToken ct = default)
        {
            SentenceCallCount++;
            return SentenceHandler?.Invoke() ?? Task.FromResult(SentenceResult);
        }
    }

    private sealed class FakeMachineTranslationService : IMachineTranslationService
    {
        public int CallCount { get; private set; }
        public string Result { get; init; } = string.Empty;
        public Exception? Failure { get; init; }
        public Func<Task<string>>? Handler { get; set; }
        public string? LastTargetLanguage { get; private set; }
        public string? LastProvider { get; private set; }

        public Task<string> TranslateAsync(
            string text,
            string targetLanguage,
            string provider = "microsoft",
            CancellationToken ct = default)
        {
            CallCount++;
            LastTargetLanguage = targetLanguage;
            LastProvider = provider;
            if (Failure != null) throw Failure;
            return Handler?.Invoke() ?? Task.FromResult(Result);
        }
    }
}

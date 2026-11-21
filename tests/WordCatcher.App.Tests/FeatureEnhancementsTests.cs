using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Windows;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using Xunit;

namespace WordCatcher.App.Tests;

[Collection("Application resource rendering")]
public class FeatureEnhancementsTests
{
    [Fact]
    public async Task MiniCardAndSentenceBarSwitchAndExpandToFull()
    {
        var (vm, _, _, _) = LookupViewModelTests.Create();
        vm.IsMiniMode = true;

        var wordCapture = new CaptureResult("run", "chrome", "Docs", new ScreenPoint(100, 100), DateTimeOffset.UtcNow);
        await vm.StartLookupAsync(wordCapture);

        Assert.True(vm.IsMiniMode);
        Assert.True(vm.IsCompactWordCard);
        Assert.False(vm.IsCompactSentenceCard);
        Assert.Equal("Tab 展开 · Enter 保存 · 右键关闭", vm.ShortcutHintText);

        var sentenceCapture = new CaptureResult(
            "This is a complete English sentence that should render in the compact sentence bar.",
            "chrome", "Docs", new ScreenPoint(100, 100), DateTimeOffset.UtcNow);
        await vm.StartLookupAsync(sentenceCapture);

        Assert.True(vm.IsMiniMode);
        Assert.False(vm.IsCompactWordCard);
        Assert.True(vm.IsCompactSentenceCard);

        vm.ExpandToFullCommand.Execute(null);
        Assert.False(vm.IsMiniMode);
        Assert.True(vm.IsFullCard);
        Assert.Equal("Enter 保存 · Ctrl+Enter AI", vm.ShortcutHintText);
    }

    [Fact]
    public async Task SavedWordShowsEncounterCountBadgeAndUpdatesAfterAppend()
    {
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wc-encounter-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new WordCatcher.Infrastructure.Database.SqliteConnectionFactory(dbPath);
            await new WordCatcher.Infrastructure.Database.SqliteMigrationRunner(factory).MigrateAsync();
            var repository = new WordCatcher.Infrastructure.Database.WordRepository(factory);
            var vm = new LookupViewModel(
                new StubLookupService(),
                repository,
                new StubSyncQueue(),
                new StubSettingsService(),
                new SpeechService(),
                new WordCollectionEvents());

            var firstCapture = new CaptureResult("resilient", "chrome", "Article 1", new ScreenPoint(100, 100), DateTimeOffset.UtcNow,
                Sentence: "Teams must stay resilient under pressure.", SentenceOffset: 16);
            await vm.StartLookupAsync(firstCapture);
            Assert.Equal(string.Empty, vm.EncounterBadgeText);
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(1, vm.EncounterCount);
            Assert.Equal("已收藏", vm.EncounterBadgeText);

            // Encounter the same word in a second sentence.
            var secondCapture = new CaptureResult("resilient", "msedge", "Article 2", new ScreenPoint(120, 120), DateTimeOffset.UtcNow,
                Sentence: "Resilient systems recover gracefully from outages.", SentenceOffset: 0);
            await vm.StartLookupAsync(secondCapture);
            Assert.True(vm.IsSaved);
            Assert.False(vm.HasSavedContext);
            Assert.Equal(1, vm.EncounterCount);
            Assert.Equal("已收藏 · 第 2 次遇到", vm.EncounterBadgeText);

            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.EncounterCount);
            Assert.Equal("已收藏 · 第 2 次遇到", vm.EncounterBadgeText);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath); } catch { }
        }
    }

    private sealed class StubLookupService : ILookupService
    {
        public Task<TranslationResult> LookupAsync(CaptureResult capture, bool forceAi, CancellationToken ct = default) =>
            Task.FromResult(new TranslationResult(
                capture.SelectedText, "/rɪˈzɪljənt/", "adj.", "有弹性的；能迅速恢复的",
                "团队在压力下必须保持韧性。", null, LookupSource.OpenDictionary, null, null));
    }

    private sealed class StubSyncQueue : IAnkiSyncQueue
    {
        public void Enqueue(string syncJobId) { }
        public void TriggerSync() { }
        public Task SyncNowAsync() => Task.CompletedTask;
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; } = new() { Ui = new UiSettings { ClosePopupAfterSave = false } };
        public event Action<AppSettings>? SettingsChanged;
        public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void SpeechServiceHelpersNormalizeAccentAndOnlineUrl()
    {
        Assert.Equal(SpeechSettings.AccentUs, SpeechSettings.NormalizeAccent(null));
        Assert.Equal(SpeechSettings.AccentGb, SpeechSettings.NormalizeAccent("en-GB"));
        Assert.Equal(SpeechSettings.AccentAuto, SpeechSettings.NormalizeAccent("AUTO"));

        Assert.Equal("https://dict.youdao.com/dictvoice?audio=hello%20world&type=2",
            SpeechService.BuildOnlineVoiceUrl("hello world", SpeechSettings.AccentUs));
        Assert.Equal("https://dict.youdao.com/dictvoice?audio=colour&type=1",
            SpeechService.BuildOnlineVoiceUrl("colour", SpeechSettings.AccentGb));

        Assert.True(SpeechService.CanUseOnlineVoice("serendipity"));
        Assert.False(SpeechService.CanUseOnlineVoice("你好世界"));
    }

    [Fact]
    public void PillSettingsAndKeyboardDismissRulesWork()
    {
        Assert.False(new MouseSelectionSettings().HoverToShowMini);
        Assert.False(new SpeechSettings().UseOnlineVoice);

        Assert.Equal(UiSettings.PopupModeMini, UiSettings.NormalizePopupMode(null));
        Assert.Equal(UiSettings.PopupModeFull, UiSettings.NormalizePopupMode("FULL"));

        // Esc (0x1B) and letter A (0x41) dismiss the pill; Shift/Ctrl/Alt modifiers do not.
        Assert.True(MouseSelectionMonitor.ShouldDismissPillForVirtualKey(0x1B));
        Assert.True(MouseSelectionMonitor.ShouldDismissPillForVirtualKey(0x41));
        Assert.False(MouseSelectionMonitor.ShouldDismissPillForVirtualKey(0x10));
        Assert.False(MouseSelectionMonitor.ShouldDismissPillForVirtualKey(0xA2));
    }

    [Fact]
    public void WindowSizingFitsMiniAndSentencePopups()
    {
        var mini = WindowSizing.FitPopup(1920, 1080, 1.0, 296, 260);
        Assert.Equal(296, mini.Width);
        Assert.Equal(260, mini.MaxHeight);

        var sentence = WindowSizing.FitPopup(1920, 1080, 1.0, 440, 360);
        Assert.Equal(440, sentence.Width);
        Assert.Equal(360, sentence.MaxHeight);
    }

    [Fact]
    public void SelectionPillContextMenuExposesQuickSaveAndCopyTranslation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SelectionPillWindow? pill = null;
            try
            {
                pill = new SelectionPillWindow();
                Assert.NotEmpty(pill.Resources.MergedDictionaries);
                Assert.NotNull(pill.TryFindResource("SurfaceBrush"));
                var rootGrid = Assert.IsType<System.Windows.Controls.Grid>(pill.Content);
                var border = Assert.IsType<System.Windows.Controls.Border>(Assert.Single(rootGrid.Children));
                var emblem = Assert.IsType<System.Windows.Controls.Viewbox>(border.Child);
                var canvas = Assert.IsType<System.Windows.Controls.Canvas>(emblem.Child);
                Assert.Equal(3, canvas.Children.Count);

                Assert.NotNull(pill.ContextMenu);
                Assert.Equal(2, pill.ContextMenu!.Items.Count);
                Assert.Equal("直接收藏", ((System.Windows.Controls.MenuItem)pill.ContextMenu.Items[0]).Header);
                Assert.Equal("复制译文", ((System.Windows.Controls.MenuItem)pill.ContextMenu.Items[1]).Header);
                var crossThreadValue = Task.Run(() => pill.IsContextMenuOpen).GetAwaiter().GetResult();
                Assert.False(crossThreadValue);
            }
            catch (Exception ex) { failure = ex; }
            finally { pill?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }

    [Fact]
    public async Task OnlineSpeechTimeoutFallsBackToSapiAndStopPreventsStaleFallback()
    {
        var settings = new StubSettingsService();
        settings.Current.Speech.UseOnlineVoice = true;
        settings.Current.Speech.Accent = SpeechSettings.AccentGb;

        var fallbackCalled = new TaskCompletionSource<(string Text, string Accent)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowClient = new System.Net.Http.HttpClient(new SlowHttpHandler(TimeSpan.FromSeconds(5)));
        using var speech = new SpeechService(
            settings,
            slowClient,
            TimeSpan.FromMilliseconds(80),
            (text, accent) => fallbackCalled.TrySetResult((text, accent)));

        speech.Speak("timeout");
        var result = await fallbackCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(("timeout", SpeechSettings.AccentGb), result);

        // Verify Stop() prevents a canceled in-flight online request from falling back.
        var staleFallbackCount = 0;
        using var speech2 = new SpeechService(
            settings,
            slowClient,
            TimeSpan.FromMilliseconds(80),
            (_, _) => Interlocked.Increment(ref staleFallbackCount));
        speech2.Speak("canceled");
        speech2.Stop();
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref staleFallbackCount));
    }

    [Fact]
    public async Task ConcurrentSettingsUpdatesPreserveAllFields()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wc-settings-merge-{Guid.NewGuid():N}.json");
        try
        {
            var service = new WordCatcher.Infrastructure.Settings.JsonSettingsService(path);
            await Task.WhenAll(
                service.UpdateSettingsAsync(s => { s.Ui.PinnedPopupX = 320; s.Ui.PinnedPopupY = 180; }),
                service.UpdateSettingsAsync(s => { s.MouseSelection.HoverToShowMini = true; }),
                service.UpdateSettingsAsync(s => { s.Speech.Accent = SpeechSettings.AccentGb; s.Speech.UseOnlineVoice = true; }));

            Assert.Equal(320, service.Current.Ui.PinnedPopupX);
            Assert.Equal(180, service.Current.Ui.PinnedPopupY);
            Assert.True(service.Current.MouseSelection.HoverToShowMini);
            Assert.Equal(SpeechSettings.AccentGb, service.Current.Speech.Accent);
            Assert.True(service.Current.Speech.UseOnlineVoice);
        }
        finally
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { }
            try { if (System.IO.File.Exists(path + ".bak")) System.IO.File.Delete(path + ".bak"); } catch { }
        }
    }

    [Fact]
    public async Task MinimalModeFormatsTranslationAndMiniHeadwordScalesForLongWords()
    {
        Assert.Equal(UiSettings.PopupModeMinimal, UiSettings.NormalizePopupMode("MINIMAL"));
        Assert.Equal(UiSettings.PopupModeMini, UiSettings.NormalizePopupMode("mini"));
        Assert.Equal(UiSettings.PopupModeFull, UiSettings.NormalizePopupMode("full"));
        Assert.Equal(UiSettings.PopupModeMini, UiSettings.NormalizePopupMode(null));

        var (vm, _, _, _) = LookupViewModelTests.Create();
        vm.PopupMode = UiSettings.PopupModeMinimal;

        vm.IsLoading = true;
        Assert.Equal("正在翻译…", vm.MinimalDisplayText);
        vm.IsLoading = false;

        vm.HasError = true;
        vm.ErrorMessage = "网络超时";
        Assert.Equal("网络超时", vm.MinimalDisplayText);
        vm.HasError = false;

        await vm.StartLookupAsync(new CaptureResult("lightweight", "chrome", "Docs", new ScreenPoint(100, 100), DateTimeOffset.UtcNow));
        Assert.True(vm.IsMinimalMode);
        Assert.False(vm.IsStandardCard);
        Assert.False(string.IsNullOrWhiteSpace(vm.MinimalDisplayText));

        vm.DisplayPosGroups = new[]
        {
            new DictPosGroup("adj.", "轻量的", new[] { new DictMeaning("s1", "core", "轻量的", null, Array.Empty<DictExample>()) }),
            new DictPosGroup("n.", "轻量级选手", new[] { new DictMeaning("s2", "core", "轻量级选手", null, Array.Empty<DictExample>()) }),
            new DictPosGroup("v.", "减轻", new[] { new DictMeaning("s3", "core", "减轻", null, Array.Empty<DictExample>()) })
        };
        Assert.Equal("adj. 轻量的\nn. 轻量级选手", vm.MinimalDisplayText);

        vm.Word = "lightweight";
        vm.PopupMode = UiSettings.PopupModeMini;
        Assert.True(vm.IsMediumHeadword);
        Assert.False(vm.IsExtraLongHeadword);

        vm.ExpandToFullCommand.Execute(null);
        Assert.True(vm.IsFullCard);
    }

    [Fact]
    public void MinimalCardSupportsRightClickDismissAndMiniAdaptsWidth()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            LookupWindow? window = null;
            try
            {
                var (vm, _, _, _) = LookupViewModelTests.Create();
                vm.PopupMode = UiSettings.PopupModeMinimal;
                vm.StartLookupAsync(new CaptureResult("lightweight", "chrome", "Docs", new ScreenPoint(100, 100), DateTimeOffset.UtcNow))
                    .GetAwaiter().GetResult();

                window = new LookupWindow(vm) { ShowActivated = false };
                var pin = (System.Windows.Controls.Primitives.ToggleButton)window.FindName("PinToggle");
                pin.IsChecked = true;
                window.Show();
                Assert.True(window.PositionNearCursor(new ScreenPoint(100, 100)));
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.True(vm.IsMinimalMode);
                Assert.False(vm.IsStandardCard);
                var minimal = Assert.IsType<System.Windows.Controls.Grid>(window.FindName("MinimalCardContainer"));
                var minimalLoading = Assert.IsType<System.Windows.Controls.StackPanel>(window.FindName("MinimalLoadingPanel"));
                var minimalScroll = Assert.IsType<System.Windows.Controls.ScrollViewer>(window.FindName("MinimalScroll"));
                var standard = Assert.IsType<System.Windows.Controls.Grid>(window.FindName("StandardCardContainer"));
                Assert.Equal(System.Windows.Visibility.Visible, minimal.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, minimalLoading.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, minimalScroll.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, standard.Visibility);
                Assert.Equal(System.Windows.SizeToContent.WidthAndHeight, window.SizeToContent);

                vm.IsLoading = true;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(System.Windows.Visibility.Visible, minimalLoading.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, minimalScroll.Visibility);
                vm.IsLoading = false;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(System.Windows.Visibility.Collapsed, minimalLoading.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, minimalScroll.Visibility);

                // Switch to mini mode and verify fixed width stays identical between loading skeleton and result.
                vm.PopupMode = UiSettings.PopupModeMini;
                var loadingSkeleton = Assert.IsType<System.Windows.Controls.StackPanel>(window.FindName("LoadingSkeletonPanel"));
                vm.HasResult = false;
                vm.IsLoading = true;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(window.PositionNearCursor(new ScreenPoint(100, 100)));
                Assert.Equal(System.Windows.Visibility.Visible, loadingSkeleton.Visibility);
                var miniLoadingWidth = window.Width;
                Assert.Equal(System.Windows.SizeToContent.Height, window.SizeToContent);
                Assert.InRange(miniLoadingWidth, 300, 352);
                Assert.Equal(miniLoadingWidth, window.MinWidth);
                Assert.Equal(miniLoadingWidth, window.MaxWidth);

                vm.IsLoading = false;
                vm.HasResult = true;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(window.PositionNearCursor(new ScreenPoint(100, 100)));
                var speak = Assert.IsAssignableFrom<System.Windows.Controls.Primitives.ButtonBase>(window.FindName("SpeakButton"));
                Assert.Equal(System.Windows.Visibility.Collapsed, speak.Visibility);
                Assert.Equal(System.Windows.SizeToContent.Height, window.SizeToContent);
                Assert.Equal(miniLoadingWidth, window.Width);
                Assert.Equal(miniLoadingWidth, window.MinWidth);
                Assert.Equal(miniLoadingWidth, window.MaxWidth);
                Assert.True(window.LastPlacedPhysicalBoundsForTest.Width >= 300);

                // Switch back to minimal, press Tab to expand to full, then switch back to minimal and dismiss via right-click.
                vm.PopupMode = UiSettings.PopupModeMinimal;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var presentationSource = System.Windows.PresentationSource.FromVisual(window) ?? new StubPresentationSource();
                var tabEvent = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    presentationSource,
                    0,
                    System.Windows.Input.Key.Tab)
                {
                    RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(tabEvent);
                Assert.True(vm.IsFullCard);
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(System.Windows.SizeToContent.Height, window.SizeToContent);
                if (miniLoadingWidth == 352) Assert.True(window.Width > miniLoadingWidth);

                vm.PopupMode = UiSettings.PopupModeMinimal;
                pin.IsChecked = false;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var wasVisible = window.IsVisible;
                var closed = false;
                window.Closed += (_, _) => closed = true;
                var rightUp = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Right)
                {
                    RoutedEvent = System.Windows.UIElement.MouseRightButtonUpEvent,
                    Source = minimal
                };
                minimal.RaiseEvent(rightUp);
                Assert.True(window.HasDismissedForTest);
                if (wasVisible) Assert.True(closed);
                window?.Close();
                window = null;
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    [Fact]
    public async Task WaitForInitialLookupReturnsPromptlyWhenFastAndCapsSlowLookup()
    {
        var fast = Task.CompletedTask;
        var fastWatch = System.Diagnostics.Stopwatch.StartNew();
        await App.WaitForInitialLookupAsync(fast, 120);
        fastWatch.Stop();
        Assert.True(fastWatch.Elapsed < TimeSpan.FromMilliseconds(80));

        var slowTcs = new TaskCompletionSource();
        var slowWatch = System.Diagnostics.Stopwatch.StartNew();
        await App.WaitForInitialLookupAsync(slowTcs.Task, 60);
        slowWatch.Stop();
        Assert.InRange(slowWatch.ElapsedMilliseconds, 30, 500);
        Assert.False(slowTcs.Task.IsCompleted);
    }

    private sealed class StubPresentationSource : System.Windows.PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }

    private sealed class SlowHttpHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        public SlowHttpHandler(TimeSpan delay) => _delay = delay;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}

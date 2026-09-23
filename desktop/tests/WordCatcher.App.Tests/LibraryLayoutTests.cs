using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using WordCatcher.Core.Enums;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Services;
using WordCatcher.App.Views;
using WordCatcher.App.Windows;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tests;

public class LibraryLayoutTests
{
    [Fact]
    public void EmptyLibraryUsesSingleWorkspace()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var repo = DispatchProxy.Create<IWordRepository, LibraryViewModelTests.RepositoryProxy>();
                var vm = new LibraryViewModel(repo, new WordCollectionEvents());
                var (settingsVm, settingsService) = InteractionTests.Settings();
                settingsVm.InitializeAsync().GetAwaiter().GetResult();
                var syncRepo = DispatchProxy.Create<IWordRepository, InteractionTests.Proxy>();
                var syncVm = new SyncViewModel(syncRepo, DispatchProxy.Create<IAnkiSyncQueue, InteractionTests.Proxy>(), (ISettingsService)settingsService);
                var (lookupVm, _, _, _) = LookupViewModelTests.Create();
                var window = new LibraryWindow(vm, syncVm, settingsVm, new WordLookupViewModel(lookupVm));
                var root = (FrameworkElement)window.Content;
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(42, WindowChrome.GetWindowChrome(window)?.CaptionHeight);
                Assert.True(WindowChrome.GetIsHitTestVisibleInChrome((Button)window.FindName("CloseButton")));
                var tabs = (TabControl)window.FindName("MainTabs");
                var libraryTab = (TabItem)window.FindName("LibraryTab");
                var lookupTab = (TabItem)window.FindName("LookupTab");
                tabs.SelectedItem = libraryTab;

                root.Measure(new Size(1180, 700));
                root.Arrange(new Rect(0, 0, 1180, 700));
                root.UpdateLayout();

                Assert.Equal(Visibility.Visible, ((Border)libraryTab.Template.FindName("SelectionIndicator", libraryTab)).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Border)lookupTab.Template.FindName("SelectionIndicator", lookupTab)).Visibility);
                tabs.SelectedItem = lookupTab;
                root.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, ((Border)libraryTab.Template.FindName("SelectionIndicator", libraryTab)).Visibility);
                Assert.Equal(Visibility.Visible, ((Border)lookupTab.Template.FindName("SelectionIndicator", lookupTab)).Visibility);
                tabs.SelectedItem = libraryTab;
                root.UpdateLayout();

                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("EmptyLibraryWorkspace")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LibraryWorkspace")).Visibility);

                var bitmap = new RenderTargetBitmap(1180, 700, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(Path.GetTempPath(), "wordcatcher-LibraryEmpty-1180.png"));
                encoder.Save(file);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void LibraryRendersAtMinimumAndDefaultSizes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var repo = DispatchProxy.Create<IWordRepository, LibraryViewModelTests.RepositoryProxy>();
                var vm = new LibraryViewModel(repo, new WordCollectionEvents());
                var word = new Word { DisplayWord = "serendipity", Reading = "/ˌserənˈdɪpəti/", PartOfSpeech = "noun", Definition = "意外发现美好事物的能力；机缘巧合。\nThe occurrence of events by chance in a happy or beneficial way.", MemoryHook = "在阅读中，收藏一次不期而遇。" };
                vm.Words.Add(word);
                vm.Words.Add(new Word { DisplayWord = "perspective", Reading = "/pəˈspektɪv/", PartOfSpeech = "noun", Definition = "观点；视角" });
                vm.SelectedWord = word;
                vm.Occurrences.Add(new Occurrence { Sentence = "It was pure serendipity that we found this book.", ContextTranslation = "找到这本书完全是意外之喜。", SourceProcess = "Reader" });
                var (settingsVm, settingsService) = InteractionTests.Settings();
                settingsVm.InitializeAsync().GetAwaiter().GetResult();
                var syncRepo = DispatchProxy.Create<IWordRepository, InteractionTests.Proxy>();
                ((InteractionTests.Proxy)syncRepo).Jobs.Add(new SyncJob { Status = SyncStatus.Retryable, Word = word, LastError = "Anki 尚未启动，请打开 Anki 后重试。" });
                var syncVm = new SyncViewModel(syncRepo, DispatchProxy.Create<IAnkiSyncQueue, InteractionTests.Proxy>(), (ISettingsService)settingsService);
                syncVm.InitializeAsync().GetAwaiter().GetResult();
                var (lookupVm, _, _, _) = LookupViewModelTests.Create();
                var window = new LibraryWindow(vm, syncVm, settingsVm, new WordLookupViewModel(lookupVm));
                var root = (FrameworkElement)window.Content;
                foreach (var page in new[] { "LibraryTab", "LookupTab", "SyncTab", "SettingsTab" })
                foreach (var width in new[] { 900, 1180 })
                {
                    ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName(page);
                    root.Measure(new Size(width, 700));
                    root.Arrange(new Rect(0, 0, width, 700));
                    root.UpdateLayout();
                    if (page == "LookupTab")
                    {
                        var lookupPage = (WordLookupView)window.FindName("LookupPage");
                        Assert.Equal(Visibility.Visible, ((FrameworkElement)lookupPage.FindName("LookupEmptyState")).Visibility);
                        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)lookupPage.FindName("LookupResultScroll")).Visibility);
                    }
                    var bitmap = new RenderTargetBitmap(width, 700, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(Path.GetTempPath(), $"wordcatcher-{page}-{width}.png"));
                    encoder.Save(file);
                    Assert.True(root.ActualWidth > 0);
                }

                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("SyncTab");
                var history = (Expander)window.FindName("SyncHistoryExpander");
                history.IsExpanded = true;
                root.Measure(new Size(900, 700));
                root.Arrange(new Rect(0, 0, 900, 700));
                root.UpdateLayout();
                var historyBitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
                historyBitmap.Render(root);
                var historyEncoder = new PngBitmapEncoder();
                historyEncoder.Frames.Add(BitmapFrame.Create(historyBitmap));
                using (var file = File.Create(Path.Combine(Path.GetTempPath(), "wordcatcher-SyncHistory-900.png")))
                    historyEncoder.Save(file);
                history.IsExpanded = false;

                var jobsProxy = (InteractionTests.Proxy)syncRepo;
                foreach (var (name, enabled, status, expected) in new[]
                {
                    ("Waiting", true, (SyncStatus?)SyncStatus.Pending, SyncPageState.Waiting),
                    ("Complete", true, (SyncStatus?)SyncStatus.Synced, SyncPageState.Complete),
                    ("Disabled", false, (SyncStatus?)null, SyncPageState.Disabled),
                    ("Empty", true, (SyncStatus?)null, SyncPageState.Empty)
                })
                {
                    jobsProxy.Jobs.Clear();
                    if (status.HasValue)
                        jobsProxy.Jobs.Add(new SyncJob { Status = status.Value, Word = new Word { DisplayWord = "serendipity" } });
                    settingsService.Settings.Anki.Enabled = enabled;
                    syncVm.RefreshAsync().GetAwaiter().GetResult();
                    Assert.Equal(expected, syncVm.PageState);
                    ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("SyncTab");
                    root.Measure(new Size(900, 700));
                    root.Arrange(new Rect(0, 0, 900, 700));
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(Path.GetTempPath(), $"wordcatcher-Sync{name}-900.png"));
                    encoder.Save(file);
                    if (name == "Disabled")
                    {
                        var openSettings = (Button)window.FindName("OpenAnkiSettingsButton");
                        Assert.Equal(Visibility.Visible, openSettings.Visibility);
                        openSettings.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Same(window.FindName("SettingsTab"), ((TabControl)window.FindName("MainTabs")).SelectedItem);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}

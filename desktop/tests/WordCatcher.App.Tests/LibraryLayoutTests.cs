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

[CollectionDefinition("Application resource rendering", DisableParallelization = true)]
public class ApplicationResourceRenderingCollection { }

[Collection("Application resource rendering")]
public class LibraryLayoutTests
{
    [Fact]
    public void SettingsRendersWithActualApplicationResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            System.Windows.Application? application = null;
            try
            {
                application = new System.Windows.Application();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/WordCatcher.App;component/Themes/DesignSystem.xaml", UriKind.Relative)
                });
                application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
                    Color.FromRgb(15, 118, 110), Wpf.Ui.Appearance.ApplicationTheme.Light,
                    systemGlassColor: false, systemAccentColor: false);
                var repo = DispatchProxy.Create<IWordRepository, LibraryViewModelTests.RepositoryProxy>();
                var (settings, service) = InteractionTests.Settings();
                settings.InitializeAsync().GetAwaiter().GetResult();
                var syncRepo = DispatchProxy.Create<IWordRepository, InteractionTests.Proxy>();
                var sync = new SyncViewModel(syncRepo, DispatchProxy.Create<IAnkiSyncQueue, InteractionTests.Proxy>(), (ISettingsService)service);
                var (lookup, _, _, _) = LookupViewModelTests.Create();
                var window = new LibraryWindow(new LibraryViewModel(repo, new WordCollectionEvents()), sync, settings, new WordLookupViewModel(lookup));
                Assert.Contains(window.Resources.MergedDictionaries, d => d.Source?.OriginalString.Contains("DesignSystem.xaml") == true);
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("SettingsTab");
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(900, 700));
                root.Arrange(new Rect(0, 0, 900, 700));
                root.UpdateLayout();
                var scroll = (ScrollViewer)window.FindName("SettingsScrollViewer");
                scroll.ScrollToEnd();
                root.UpdateLayout();
                Assert.True(scroll.ScrollableHeight > 0);
                foreach (var page in new[] { "LibraryTab", "LookupTab", "SyncTab", "SettingsTab" })
                {
                    ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName(page);
                    root.Measure(new Size(900, 700));
                    root.Arrange(new Rect(0, 0, 900, 700));
                    root.UpdateLayout();
                }
                window.ShowSettings();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible);
                window.Hide();
            }
            catch (Exception ex) { failure = ex; }
            finally { application?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }

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

                var libraryNavigationItem = (Wpf.Ui.Controls.NavigationViewItem)window.FindName("LibraryNavigationItem");
                var lookupNavigationItem = (Wpf.Ui.Controls.NavigationViewItem)window.FindName("LookupNavigationItem");
                Assert.True(libraryNavigationItem.IsActive);
                Assert.False(lookupNavigationItem.IsActive);
                Assert.Equal(Visibility.Visible, libraryNavigationItem.Visibility);
                Assert.NotNull(libraryNavigationItem.Template);
                Assert.True(libraryNavigationItem.ActualWidth > 0);
                Assert.True(libraryNavigationItem.ActualHeight > 0);
                AssertNavigationAccent(libraryNavigationItem);
                tabs.SelectedItem = lookupTab;
                root.UpdateLayout();
                Assert.False(libraryNavigationItem.IsActive);
                Assert.True(lookupNavigationItem.IsActive);
                AssertNavigationAccent(lookupNavigationItem);
                var settingsNavigationItem = (Wpf.Ui.Controls.NavigationViewItem)window.FindName("SettingsNavigationItem");
                settingsNavigationItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Same(window.FindName("SettingsTab"), tabs.SelectedItem);
                Assert.True(settingsNavigationItem.IsActive);
                root.UpdateLayout();
                AssertNavigationAccent(settingsNavigationItem);
                tabs.SelectedItem = (TabItem)window.FindName("SyncTab");
                root.UpdateLayout();
                AssertNavigationAccent((Wpf.Ui.Controls.NavigationViewItem)window.FindName("SyncNavigationItem"));
                tabs.SelectedItem = libraryTab;
                root.UpdateLayout();

                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("EmptyLibraryWorkspace")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LibraryWorkspace")).Visibility);

                RenderState(root, "wordcatcher-titlebar-1180.png", 1180, 42);
                RenderState(root, "wordcatcher-navigation-accent.png", 208, 260);
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

                    if (page == "SettingsTab")
                    {
                        var settingsScroll = (ScrollViewer)window.FindName("SettingsScrollViewer");
                        settingsScroll.ScrollToEnd();
                        root.UpdateLayout();
                        Assert.True(settingsScroll.ScrollableHeight > 0);
                        var bottomBitmap = new RenderTargetBitmap(width, 700, 96, 96, PixelFormats.Pbgra32);
                        bottomBitmap.Render(root);
                        var bottomEncoder = new PngBitmapEncoder();
                        bottomEncoder.Frames.Add(BitmapFrame.Create(bottomBitmap));
                        using var bottomFile = File.Create(Path.Combine(Path.GetTempPath(), $"wordcatcher-SettingsBottom-{width}.png"));
                        bottomEncoder.Save(bottomFile);
                        settingsScroll.ScrollToTop();
                        root.UpdateLayout();
                    }
                }

                // 最小窗口下草稿操作与编辑操作仍可见，不被滚动内容挤出。
                settingsVm.ApiModel = "unsaved-draft";
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("SettingsTab");
                root.Measure(new Size(900, 600));
                root.Arrange(new Rect(0, 0, 900, 600));
                root.UpdateLayout();
                foreach (var name in new[] { "DiscardSettingsButton", "SettingsSaveButton" })
                {
                    var button = (FrameworkElement)window.FindName(name);
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    Assert.True(button.ActualWidth > 0);
                    var bottom = button.TransformToAncestor(root).Transform(new Point(0, button.ActualHeight));
                    Assert.InRange(bottom.Y, 0, 600);
                }
                RenderState(root, "wordcatcher-SettingsDraft-900x600.png", 900, 600);

                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("LibraryTab");
                vm.StartEditCommand.Execute(null);
                root.Measure(new Size(900, 600));
                root.Arrange(new Rect(0, 0, 900, 600));
                root.UpdateLayout();
                Assert.True(((FrameworkElement)window.FindName("EditWordBox")).ActualHeight > 0);
                RenderState(root, "wordcatcher-LibraryEdit-900x600.png", 900, 600);
                vm.CancelEditCommand.Execute(null);

                // 导出完整词库不应取决于当前筛选是否匹配任何词条。
                vm.Words.Clear();
                root.Measure(new Size(900, 600));
                root.Arrange(new Rect(0, 0, 900, 600));
                root.UpdateLayout();
                Assert.True(((System.Windows.Controls.Button)window.FindName("ExportBackupButton")).IsEnabled);

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

    private static void AssertNavigationAccent(Wpf.Ui.Controls.NavigationViewItem item)
    {
        var indicator = Assert.IsType<System.Windows.Shapes.Rectangle>(item.Template.FindName("ActiveRectangle", item));
        var brush = Assert.IsType<SolidColorBrush>(indicator.Fill);
        Assert.Equal(Color.FromRgb(15, 118, 110), brush.Color);
    }

    private static void RenderState(FrameworkElement root, string fileName, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Path.GetTempPath(), fileName));
        encoder.Save(file);
    }
}

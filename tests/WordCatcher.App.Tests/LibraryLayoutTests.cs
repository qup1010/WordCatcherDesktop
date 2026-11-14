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
                using var startupRegistration = new StartupTestRegistration();
                var (settings, service) = InteractionTests.Settings(startupRegistration.Service);
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
                var categoryTabs = (TabControl)window.FindName("SettingsCategoryTabs");
                var startupSwitch = (Wpf.Ui.Controls.ToggleSwitch)window.FindName("StartWithWindowsSwitch");
                startupSwitch.IsChecked = true;
                Assert.True(startupRegistration.Service.IsEnabled);
                ((FrameworkElement)window.FindName("InteractionSettingsGroup")).BringIntoView();
                root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                RenderState(root, "wordcatcher-startup-enabled.png", 900, 700);
                startupSwitch.IsChecked = false;
                Assert.False(startupRegistration.Service.IsEnabled);
                root.UpdateLayout();
                RenderState(root, "wordcatcher-startup-disabled.png", 900, 700);
                using (var startupKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(startupRegistration.KeyPath))
                    startupKey.SetValue("WordCatcherDesktop", "\"C:\\Old Folder\\WordCatcher.App.exe\"");
                // Exercise the actual navigation handler instead of manually refreshing the ViewModel.
                var tabs = (TabControl)window.FindName("MainTabs");
                var draftModel = settings.ApiModel;
                settings.ApiModel = "unsaved-navigation-draft";
                tabs.SelectedItem = window.FindName("LibraryTab");
                tabs.SelectedItem = window.FindName("SettingsTab");
                Assert.True(settings.StartWithWindows);
                Assert.Equal("unsaved-navigation-draft", settings.ApiModel);
                Assert.True(settings.HasUnsavedChanges);
                settings.ApiModel = draftModel;
                root.UpdateLayout();
                var repairButton = (Wpf.Ui.Controls.Button)window.FindName("StartupRepairButton");
                Assert.Equal(Visibility.Visible, repairButton.Visibility);
                RenderState(root, "wordcatcher-startup-repair.png", 900, 700);
                repairButton.Command.Execute(null);
                root.UpdateLayout();
                Assert.False(startupRegistration.Service.NeedsPathRepair);
                Assert.Equal(Visibility.Collapsed, repairButton.Visibility);
                startupSwitch.IsChecked = false;
                var pendingBar = (FrameworkElement)window.FindName("SettingsPendingBar");
                Assert.Equal(Visibility.Collapsed, pendingBar.Visibility);
                settings.ApiModel = "pending-model";
                root.UpdateLayout();
                Assert.Equal(Visibility.Visible, pendingBar.Visibility);
                settings.DiscardChangesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                root.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, pendingBar.Visibility);

                ((RadioButton)window.FindName("SyncCategoryTab")).IsChecked = true;
                root.UpdateLayout();
                scroll.ScrollToEnd();
                root.UpdateLayout();
                Assert.True(scroll.ScrollableHeight > 0);
                ((RadioButton)window.FindName("GeneralCategoryTab")).IsChecked = true;
                root.UpdateLayout();

                foreach (var page in new[] { "LibraryTab", "LookupTab", "SyncTab", "SettingsTab" })
                {
                    ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName(page);
                    root.Measure(new Size(900, 700));
                    root.Arrange(new Rect(0, 0, 900, 700));
                    root.UpdateLayout();
                }
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("SettingsTab");
                root.Measure(new Size(640, 480));
                root.Arrange(new Rect(0, 0, 640, 480));
                root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                Assert.Equal(64, ((ColumnDefinition)window.FindName("NavigationColumn")).Width.Value);
                Assert.Equal(Wpf.Ui.Controls.NavigationViewPaneDisplayMode.LeftMinimal,
                    ((Wpf.Ui.Controls.NavigationView)window.FindName("AppNavigation")).PaneDisplayMode);
                RenderState(root, "wordcatcher-settings-compact.png", 640, 480);

                var categories = new[]
                {
                    ("GeneralCategoryTab", "GeneralSettingsCategory", 0),
                    ("LookupCategoryTab", "LookupSettingsCategory", 1),
                    ("AiCategoryTab", "AiSettingsCategory", 2),
                    ("SyncCategoryTab", "SyncSettingsCategory", 3),
                    ("AboutCategoryTab", "AboutSettingsCategory", 4)
                };
                foreach (var (tabBtnName, categoryName, expectedIndex) in categories)
                {
                    ((RadioButton)window.FindName(tabBtnName)).IsChecked = true;
                    Assert.Equal(expectedIndex, categoryTabs.SelectedIndex);
                    root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    root.UpdateLayout();
                    RenderState(root, $"wordcatcher-settings-{categoryName}-640.png", 640, 480);

                    var category = (TabItem)window.FindName(categoryName);
                    if (category.Content is DependencyObject contentRoot)
                        SetExpanders(contentRoot, true);
                    root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    root.UpdateLayout();
                    RenderState(root, $"wordcatcher-settings-{categoryName}-expanded-640.png", 640, 480);
                    scroll.ScrollToEnd();
                    root.UpdateLayout();
                    RenderState(root, $"wordcatcher-settings-{categoryName}-bottom-640.png", 640, 480);
                    if (category.Content is DependencyObject collapseRoot)
                        SetExpanders(collapseRoot, false);
                    scroll.ScrollToTop();
                }

                ((RadioButton)window.FindName("LookupCategoryTab")).IsChecked = true;
                var dictExpander = (Expander)window.FindName("DictionaryManagementExpander");
                dictExpander.IsExpanded = true;
                root.UpdateLayout();
                RenderState(dictExpander, "wordcatcher-dictionary-options.png",
                    Math.Max(360, (int)Math.Ceiling(dictExpander.ActualWidth)),
                    Math.Max(120, (int)Math.Ceiling(dictExpander.ActualHeight)));
                dictExpander.IsExpanded = false;

                ((RadioButton)window.FindName("GeneralCategoryTab")).IsChecked = true;
                settings.RecordHotkeyCommand.Execute(null);
                Assert.True(settings.IsRecordingHotkey);
                ((FrameworkElement)window.FindName("InteractionSettingsGroup")).BringIntoView();
                root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                RenderState(root, "wordcatcher-hotkey-recording-640.png", 640, 480);
                settings.CancelHotkeyRecordingCommand.Execute(null);
                Assert.False(settings.IsRecordingHotkey);

                root.Measure(new Size(1180, 700));
                root.Arrange(new Rect(0, 0, 1180, 700));
                root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                root.UpdateLayout();
                Assert.Equal(208, ((ColumnDefinition)window.FindName("NavigationColumn")).Width.Value);
                RenderState(root, "wordcatcher-settings-light-1180.png", 1180, 700);
                ((RadioButton)window.FindName("SyncCategoryTab")).IsChecked = true;
                root.UpdateLayout();
                RenderState(root, "wordcatcher-settings-sync-light-1180.png", 1180, 700);

                ThemeManager.ApplyTheme(ThemeManager.DarkMode);
                root.UpdateLayout();
                RenderState(root, "wordcatcher-settings-sync-dark-1180.png", 1180, 700);
                ((RadioButton)window.FindName("GeneralCategoryTab")).IsChecked = true;
                root.UpdateLayout();
                RenderState(root, "wordcatcher-settings-dark-1180.png", 1180, 700);
                ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("LibraryTab");
                root.UpdateLayout();
                RenderState(root, "wordcatcher-library-dark-1180.png", 1180, 700);
                ThemeManager.ApplyTheme(ThemeManager.LightMode);

                window.ShowSettings();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.True(window.IsVisible);
                window.Hide();

                var lookupWindow = new LookupWindow(lookup);
                AssertAllSymbolIconsClean(window, lookupWindow, requireIdealAntiAliasing: true, renderMorePopup: true);
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
                vm.MatchingWordCount = 2;
                word.OccurrenceCount = 1;
                vm.Occurrences.Add(new Occurrence { Sentence = "It was pure serendipity that we found this book.", ContextTranslation = "找到这本书完全是意外之喜。", SourceProcess = "Reader" });
                var contexts = vm.Occurrences.ToArray();
                ((LibraryViewModelTests.RepositoryProxy)repo).ReadOccurrences = id =>
                    Task.FromResult<IReadOnlyList<Occurrence>>(id == word.Id ? contexts : Array.Empty<Occurrence>());
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
                    if (page == "LibraryTab")
                    {
                        var list = (ListBox)window.FindName("WordList");
                        list.SelectAll();
                        Assert.Equal(2, vm.SelectedWords.Count);
                        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
                        list.UnselectAll();
                        list.SelectedItem = word;
                        root.UpdateLayout();
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
                        ((RadioButton)window.FindName("SyncCategoryTab")).IsChecked = true;
                        root.UpdateLayout();
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
                        ((RadioButton)window.FindName("GeneralCategoryTab")).IsChecked = true;
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

                vm.StartReviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                root.Measure(new Size(900, 700));
                root.Arrange(new Rect(0, 0, 900, 700));
                root.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("WordDetailsPanel")).Visibility);
                RenderState(root, "wordcatcher-LibraryReview-900.png", 900, 700);
                vm.ToggleAnswerCommand.Execute(null);
                root.UpdateLayout();
                RenderState(root, "wordcatcher-LibraryReviewAnswer-900.png", 900, 700);
                vm.ExitReviewCommand.Execute(null);
                root.UpdateLayout();
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("WordDetailsPanel")).Visibility);

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
                        Assert.Equal(3, ((TabControl)window.FindName("SettingsCategoryTabs")).SelectedIndex);
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

    [Fact]
    public void AllAppSymbolIconsUseBmpCodepointsAndIdealAntiAliasing()
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
                lookupVm.StartLookupAsync(LookupViewModelTests.Capture()).GetAwaiter().GetResult();

                var libraryWindow = new LibraryWindow(vm, syncVm, settingsVm, new WordLookupViewModel(lookupVm));
                var lookupWindow = new LookupWindow(lookupVm);
                // When run outside Application.Current (already shut down by SettingsRendersWithActualApplicationResources),
                // verify BMP safety everywhere and Ideal anti-aliasing on all non-nested-template icons.
                AssertAllSymbolIconsClean(libraryWindow, lookupWindow, requireIdealAntiAliasing: false, renderMorePopup: true);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
    }

    private static void AssertAllSymbolIconsClean(
        LibraryWindow libraryWindow,
        LookupWindow lookupWindow,
        bool requireIdealAntiAliasing,
        bool renderMorePopup)
    {
        var libraryRoot = (FrameworkElement)libraryWindow.Content;
        var mainTabs = (TabControl)libraryWindow.FindName("MainTabs");
        var previousTab = mainTabs.SelectedItem;
        var previousCategory = ((RadioButton)libraryWindow.FindName("GeneralCategoryTab")).IsChecked == true
            ? "GeneralCategoryTab"
            : ((RadioButton)libraryWindow.FindName("LookupCategoryTab")).IsChecked == true
                ? "LookupCategoryTab"
                : ((RadioButton)libraryWindow.FindName("AiCategoryTab")).IsChecked == true
                    ? "AiCategoryTab"
                    : ((RadioButton)libraryWindow.FindName("SyncCategoryTab")).IsChecked == true
                        ? "SyncCategoryTab"
                        : "AboutCategoryTab";

        foreach (var tabName in new[] { "LookupTab", "SyncTab", "SettingsTab", "LibraryTab" })
        {
            mainTabs.SelectedItem = libraryWindow.FindName(tabName);
            if (tabName == "SettingsTab")
            {
                foreach (var catName in new[] { "GeneralCategoryTab", "LookupCategoryTab", "AiCategoryTab", "SyncCategoryTab", "AboutCategoryTab" })
                {
                    ((RadioButton)libraryWindow.FindName(catName)).IsChecked = true;
                    libraryRoot.Measure(new Size(1180, 700));
                    libraryRoot.Arrange(new Rect(0, 0, 1180, 700));
                    libraryRoot.UpdateLayout();
                }
                ((RadioButton)libraryWindow.FindName(previousCategory)).IsChecked = true;
            }
            libraryRoot.Measure(new Size(1180, 700));
            libraryRoot.Arrange(new Rect(0, 0, 1180, 700));
            libraryRoot.UpdateLayout();
        }
        mainTabs.SelectedItem = previousTab;

        var importButton = (FrameworkElement)libraryWindow.FindName("ImportBackupButton");
        var popupContent = Assert.IsType<Border>(((FrameworkElement)importButton.Parent).Parent);
        popupContent.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/WordCatcher.App;component/Themes/DesignSystem.xaml", UriKind.Relative)
        });
        popupContent.Measure(new Size(300, 300));
        popupContent.Arrange(new Rect(0, 0, popupContent.DesiredSize.Width, popupContent.DesiredSize.Height));
        popupContent.UpdateLayout();
        if (renderMorePopup)
        {
            RenderState(popupContent, "wordcatcher-library-more-popup.png",
                (int)Math.Ceiling(popupContent.DesiredSize.Width),
                (int)Math.Ceiling(popupContent.DesiredSize.Height));
        }

        var lookupRoot = (FrameworkElement)lookupWindow.Content;
        lookupRoot.Measure(new Size(420, double.PositiveInfinity));
        lookupRoot.Arrange(new Rect(0, 0, 420, lookupRoot.DesiredSize.Height));
        lookupRoot.UpdateLayout();

        var icons = CollectSymbolIcons(libraryRoot)
            .Concat(CollectSymbolIcons(popupContent))
            .Concat(CollectSymbolIcons(lookupRoot))
            .Distinct()
            .ToList();

        Assert.NotEmpty(icons);
        var crispIcons = icons.OfType<WordCatcher.App.Controls.CrispSymbolIcon>().ToList();
        Assert.True(crispIcons.Count > 0, "Expected CrispSymbolIcon instances across measured views.");

        foreach (var crispIcon in crispIcons)
        {
            var codepoint = (uint)crispIcon.Symbol;
            var visualParent = VisualTreeHelper.GetParent(crispIcon);
            var parentInfo = $"{crispIcon.TemplatedParent?.GetType().Name ?? visualParent?.GetType().Name ?? crispIcon.Parent?.GetType().Name ?? "<none>"}";
            Assert.True(codepoint <= 0xFFFF, $"CrispSymbolIcon {crispIcon.Symbol} (0x{codepoint:X}) on {parentInfo} exceeds BMP.");
            if (visualParent is not null)
            {
                var path = FindVisualDescendant<System.Windows.Shapes.Path>(crispIcon);
                Assert.True(path is not null, $"Expected rendered Path inside CrispSymbolIcon {crispIcon.Symbol} (Parent={parentInfo}).");
                Assert.True(path!.Data is not null && !path.Data.Bounds.IsEmpty, $"Expected non-empty glyph geometry inside CrispSymbolIcon {crispIcon.Symbol}.");
                Assert.True(path.Data!.IsFrozen, $"Expected frozen geometry on CrispSymbolIcon {crispIcon.Symbol}.");
                Assert.False(path.UseLayoutRounding, $"Expected UseLayoutRounding=false on CrispSymbolIcon {crispIcon.Symbol} path.");
                Assert.False(path.SnapsToDevicePixels, $"Expected SnapsToDevicePixels=false on CrispSymbolIcon {crispIcon.Symbol} path.");

                var image = FindVisualDescendant<System.Windows.Controls.Image>(crispIcon);
                Assert.True(image?.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true, PixelWidth: > 0, PixelHeight: > 0 },
                    $"Expected frozen supersampled BitmapSource inside CrispSymbolIcon {crispIcon.Symbol} (Parent={parentInfo}).");
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image!));
            }
        }

        foreach (var icon in icons.OfType<Wpf.Ui.Controls.SymbolIcon>())
        {
            var codepoint = (uint)icon.Symbol;
            var visualParent = VisualTreeHelper.GetParent(icon);
            var parentInfo = $"{icon.TemplatedParent?.GetType().Name ?? visualParent?.GetType().Name ?? icon.Parent?.GetType().Name ?? "<none>"}";
            Assert.True(codepoint <= 0xFFFF, $"Symbol {icon.Symbol} (0x{codepoint:X}) on {parentInfo} exceeds BMP and truncates in WPF UTF-16 rendering.");
            if ((requireIdealAntiAliasing && visualParent is not null) || (visualParent is not null && icon.TemplatedParent is null))
            {
                var formatting = TextOptions.GetTextFormattingMode(icon);
                var rendering = TextOptions.GetTextRenderingMode(icon);
                Assert.True(formatting == TextFormattingMode.Ideal,
                    $"Expected Ideal TextFormattingMode on {icon.Symbol} (Name='{icon.Name}', Parent={parentInfo}), actual={formatting}.");
                Assert.True(rendering == TextRenderingMode.Grayscale,
                    $"Expected Grayscale TextRenderingMode on {icon.Symbol} (Name='{icon.Name}', Parent={parentInfo}), actual={rendering}.");
            }
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                return match;

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static IEnumerable<Wpf.Ui.Controls.IconElement> CollectSymbolIcons(DependencyObject root)
    {
        if (root is WordCatcher.App.Controls.CrispSymbolIcon or Wpf.Ui.Controls.SymbolIcon)
            yield return (Wpf.Ui.Controls.IconElement)root;

        var seen = new HashSet<DependencyObject>();
        if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (seen.Add(child))
                {
                    foreach (var nested in CollectSymbolIcons(child))
                        yield return nested;
                }
            }
        }

        foreach (var logicalChild in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (seen.Add(logicalChild))
            {
                foreach (var nested in CollectSymbolIcons(logicalChild))
                    yield return nested;
            }
        }

        if (root is Wpf.Ui.Controls.CardControl card)
        {
            if (card.Icon is WordCatcher.App.Controls.CrispSymbolIcon or Wpf.Ui.Controls.SymbolIcon && seen.Add(card.Icon))
                yield return card.Icon;
        }
        else if (root is Wpf.Ui.Controls.CardExpander expander)
        {
            if (expander.Icon is WordCatcher.App.Controls.CrispSymbolIcon or Wpf.Ui.Controls.SymbolIcon && seen.Add(expander.Icon))
                yield return expander.Icon;
        }
        else if (root is Wpf.Ui.Controls.NavigationViewItem navItem)
        {
            if (navItem.Icon is WordCatcher.App.Controls.CrispSymbolIcon or Wpf.Ui.Controls.SymbolIcon && seen.Add(navItem.Icon))
                yield return navItem.Icon;
        }
        else if (root is TabControl tabControl)
        {
            foreach (var tab in tabControl.Items.OfType<DependencyObject>())
            {
                if (seen.Add(tab))
                {
                    foreach (var nested in CollectSymbolIcons(tab))
                        yield return nested;
                }
            }
        }
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
        if (width >= 600 && root.TryFindResource("CanvasBrush") is Brush canvasBrush)
        {
            var bgVisual = new DrawingVisual();
            using (var context = bgVisual.RenderOpen())
                context.DrawRectangle(canvasBrush, null, new Rect(0, 0, width, height));
            bitmap.Render(bgVisual);
        }
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Path.GetTempPath(), fileName));
        encoder.Save(file);
    }

    private static void SetExpanders(DependencyObject root, bool expanded)
    {
        if (root is Expander expander) expander.IsExpanded = expanded;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) SetExpanders(child, expanded);
    }
}

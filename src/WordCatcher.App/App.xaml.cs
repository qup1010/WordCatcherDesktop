using System;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WordCatcher.App.Services;
using WordCatcher.App.Tray;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Windows;
using WordCatcher.Core.Helpers;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Anki;
using WordCatcher.Infrastructure.Database;
using WordCatcher.Infrastructure.Dictionary;
using WordCatcher.Infrastructure.Logging;
using WordCatcher.Infrastructure.Security;
using WordCatcher.Infrastructure.Settings;
using WordCatcher.Infrastructure.Sync;
using WordCatcher.Infrastructure.Translation;
using Wpf.Ui.Appearance;

namespace WordCatcher.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "WordCatcher_Desktop_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private IServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    private HotkeyManager? _hotkeyManager;
    private MouseSelectionMonitor? _mouseSelectionMonitor;
    private volatile SelectionPillWindow? _selectionPillWindow;
    private TrayIconManager? _trayManager;
    private AnkiSyncWorker? _syncWorker;
    private WebDavSyncService? _webDavSync;
    private LibraryWindow? _libraryWindow;
    private volatile LookupWindow? _activeLookupWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(15, 118, 110),
            ApplicationTheme.Light,
            systemGlassColor: false,
            systemAccentColor: false);

        // 1. Single instance check
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                System.Windows.MessageBox.Show(
                "Word Catcher 已经在运行中，请查看屏幕右下角系统托盘。",
                "Word Catcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. Build Dependency Injection Container
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Word Catcher application starting up...");

        try
        {
            // 3. Database Migration
            var migrator = _serviceProvider.GetRequiredService<SqliteMigrationRunner>();
            await migrator.MigrateAsync().ConfigureAwait(true);
            _logger.LogInformation("Database migration completed");

            // 4. Start Anki Background Sync Worker & WebDAV Sync
            _syncWorker = _serviceProvider.GetRequiredService<AnkiSyncWorker>();
            _syncWorker.Start();

            _webDavSync = _serviceProvider.GetRequiredService<WebDavSyncService>();
            var wordEvents = _serviceProvider.GetRequiredService<WordCollectionEvents>();
            wordEvents.WordSaved += () => _webDavSync.ScheduleAutoSync();
            _webDavSync.SyncCompleted += () =>
            {
                if (Current?.Dispatcher is { } dispatcher)
                    _ = dispatcher.InvokeAsync(() => wordEvents.NotifyCollectionChanged());
            };
            _webDavSync.SyncFailed += message =>
            {
                if (Current?.Dispatcher is { } dispatcher && _libraryWindow is { } window)
                    _ = dispatcher.InvokeAsync(() => window.ShowBackgroundWebDavFailure(message));
            };

            // 5. Initialize Services & Tray
            _hotkeyManager = _serviceProvider.GetRequiredService<HotkeyManager>();
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            ThemeManager.ApplyTheme(settingsService.Current.Ui.ThemeMode);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            var registered = _hotkeyManager.Register(settingsService.Current.Hotkey);
            if (!registered)
            {
                System.Windows.MessageBox.Show(
                    $"默认快捷键「{settingsService.Current.Hotkey}」已被占用，可在「设置 → 常规与外观」更换快捷键。",
                    "快捷键占用提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            _hotkeyManager.HotkeyTriggered += OnHotkeyTriggered;
            if (!_hotkeyManager.RegisterOcr(settingsService.Current.OcrHotkey))
            {
                _logger?.LogWarning("OCR hotkey '{Hotkey}' could not be registered (may be occupied)", settingsService.Current.OcrHotkey);
            }
            _hotkeyManager.OcrHotkeyTriggered += OnOcrCaptureTriggered;

            _trayManager = _serviceProvider.GetRequiredService<TrayIconManager>();
            _trayManager.ToggleLibraryRequested += OnToggleLibraryRequested;
            _trayManager.OpenLibraryRequested += OnOpenLibraryRequested;
            _trayManager.OpenLookupRequested += OnOpenLookupRequested;
            _trayManager.OpenSettingsRequested += OnOpenSettingsRequested;
            _trayManager.CaptureNowRequested += () => OnHotkeyTriggered();
            _trayManager.OcrCaptureRequested += OnTrayOcrCaptureTriggered;
            _trayManager.MouseSelectionToggled += OnTrayMouseSelectionToggled;

            _selectionPillWindow = new SelectionPillWindow();
            _selectionPillWindow.PillClicked += capture => _ = ShowCapturedLookupAsync(capture);
            _selectionPillWindow.PillHovered += capture => _ = ShowCapturedLookupAsync(capture, forceMini: true);
            _selectionPillWindow.QuickSaveRequested += capture => _ = QuickSaveFromPillAsync(capture);
            _selectionPillWindow.CopyTranslationRequested += capture => _ = CopyTranslationFromPillAsync(capture);
            _selectionPillWindow.PillVisibilityChanged += UpdateMouseSelectionHook;
            _mouseSelectionMonitor = new MouseSelectionMonitor(settingsService,
                pt => _selectionPillWindow?.ContainsPhysicalPoint(pt) ?? false);
            _mouseSelectionMonitor.SelectionDetected += capture =>
            {
                if (_hotkeyManager is { IsEnabled: false }) return;
                Dispatcher?.InvokeAsync(async () =>
                {
                    var mouseSettings = settingsService.Current.MouseSelection;
                    if (ShouldAutoTranslateSelection(mouseSettings?.TriggerMode, capture.SelectedText))
                    {
                        _selectionPillWindow?.HidePill();
                        await ShowCapturedLookupAsync(capture);
                    }
                    else
                    {
                        _selectionPillWindow?.ShowForCapture(
                            capture,
                            mouseSettings?.HoverToShowMini ?? false);
                    }
                });
            };
            _mouseSelectionMonitor.DismissRequested += OnMouseDismissRequested;
            _mouseSelectionMonitor.KeyboardDismissRequested += () =>
            {
                if (_selectionPillWindow is { IsPillVisible: true } pill)
                    Dispatcher?.InvokeAsync(pill.HidePill);
            };
            settingsService.SettingsChanged += OnMouseSelectionSettingsChanged;
            UpdateMouseSelectionHook();

            _libraryWindow = _serviceProvider.GetRequiredService<LibraryWindow>();
            var settingsVm = _serviceProvider.GetRequiredService<SettingsViewModel>();
            settingsVm.UpdateInstallRequested += InstallDownloadedUpdate;

            if (!e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            {
                _logger?.LogInformation("Interactive launch: opening the library window");
                _libraryWindow.ShowLibrary();
            }

            if (settingsService.Current.WebDav.Enabled
                && settingsService.Current.WebDav.AutoSync
                && !string.IsNullOrWhiteSpace(settingsService.Current.WebDav.Url))
            {
                _webDavSync.ScheduleAutoSync();
            }

            _logger?.LogInformation("Word Catcher initialized and resident in system tray");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Critical failure during application startup");
            System.Windows.MessageBox.Show(
                $"应用启动失败:\n{ex.Message}",
                "启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(builder =>
        {
            builder.AddProvider(new FileLoggerProvider());
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // HTTP Client
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(15) });
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<IAppUpdater, AppUpdater>();

        // Database & Repositories
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteMigrationRunner>();
        services.AddSingleton<IWordRepository, WordRepository>();
        services.AddSingleton<IQueryHistory, QueryHistory>();
        services.AddSingleton<WordCollectionEvents>();

        // Settings & DPAPI Secrets
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<ISecretStore, DpapiSecretService>();

        // Offline Dictionary
        services.AddSingleton<IOfflineDictionaryService, OfflineDictionaryService>();
        services.AddSingleton<IDictionaryInstaller>(sp =>
        {
            var http = sp.GetRequiredService<HttpClient>();
            var offlineDict = (OfflineDictionaryService)sp.GetRequiredService<IOfflineDictionaryService>();
            var logger = sp.GetService<ILogger<DictionaryInstaller>>();
            return new DictionaryInstaller(http, onDictionaryUpdated: () => offlineDict.InvalidateConnection(), logger: logger);
        });

        // AI Translation & Lookup
        services.AddSingleton<OpenAiTranslationService>();
        services.AddSingleton<ITranslationService>(sp => sp.GetRequiredService<OpenAiTranslationService>());
        services.AddSingleton<IMachineTranslationService, MachineTranslationService>();
        services.AddSingleton<ILookupService, LookupService>();

        // Anki Sync & WebDAV Sync
        services.AddSingleton<IAnkiClient, AnkiConnectClient>();
        services.AddSingleton<AnkiSyncWorker>();
        services.AddSingleton<IAnkiSyncQueue>(sp => sp.GetRequiredService<AnkiSyncWorker>());
        services.AddSingleton(sp => new WebDavSyncService(sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IWordRepository>(), sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<ISecretStore>(), sp.GetService<ILogger<WebDavSyncService>>(),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WordCatcher", "sync-status.json")));
        services.AddSingleton<IWebDavSyncService>(sp => sp.GetRequiredService<WebDavSyncService>());

        // Win32 Capture & Hotkey & Tray
        services.AddSingleton<ISelectionCaptureService, SelectionCaptureService>();
        services.AddSingleton<IOcrService, WinRtOcrService>();
        services.AddSingleton<ISpeechService, SpeechService>();
        services.AddSingleton<HotkeyManager>();
        services.AddSingleton<TrayIconManager>();

        // ViewModels
        services.AddTransient<LookupViewModel>();
        services.AddSingleton<WordLookupViewModel>();
        services.AddSingleton(sp => new LibraryViewModel(
            sp.GetRequiredService<IWordRepository>(), sp.GetRequiredService<WordCollectionEvents>(),
            sp.GetRequiredService<ISpeechService>(), sp.GetRequiredService<IWebDavSyncService>(),
            sp.GetRequiredService<ILookupService>(),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WordCatcher", "library-view.json")));
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Windows
        services.AddSingleton<LibraryWindow>();
    }

    private bool _isOcrCapturing;

    private void OnOcrCaptureTriggered() => _ = TriggerOcrCaptureAsync(delayMs: 0);

    private void OnTrayOcrCaptureTriggered() => _ = TriggerOcrCaptureAsync(delayMs: 180);

    private async Task TriggerOcrCaptureAsync(int delayMs)
    {
        if (_isOcrCapturing) return;
        _isOcrCapturing = true;
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(true);

            _logger?.LogInformation("Screen OCR capture triggered");
            var snip = await ScreenSnipWindow.CaptureRegionAsync().ConfigureAwait(true);
            if (snip == null || snip.PngBytes.Length == 0)
                return;

            var ocrService = _serviceProvider?.GetRequiredService<IOcrService>();
            if (ocrService == null) return;

            var text = await ocrService.RecognizePngAsync(snip.PngBytes).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(text))
            {
                _trayManager?.ShowNotification("未识别到文字", "所选截图区域未检测到可识别文本，请放大或重新框选。",
                    System.Windows.Forms.ToolTipIcon.Info);
                return;
            }

            var capture = new CaptureResult(
                text,
                "ScreenOcr",
                "截屏 OCR 取词",
                snip.CursorPosition,
                DateTimeOffset.UtcNow,
                Sentence: text,
                SentenceOffset: 0);
            await ShowCapturedLookupAsync(capture).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Screen OCR capture failed");
            _trayManager?.ShowNotification("截屏 OCR 失败", ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _isOcrCapturing = false;
        }
    }

    private async void OnHotkeyTriggered()
    {
        _logger?.LogInformation("Global hotkey handler invoked; capturing selected text");
        var captureService = _serviceProvider?.GetRequiredService<ISelectionCaptureService>();
        if (captureService == null) return;

        await HotkeyCaptureHandler.HandleAsync(captureService, ShowCapturedLookupAsync, ex =>
        {
            _logger?.LogWarning(ex, "Global hotkey selection capture failed");
            if (_serviceProvider?.GetService<WordLookupViewModel>() is { } lookupViewModel)
            {
                lookupViewModel.QueryError = ex.Message;
                _libraryWindow?.ShowLookup();
            }
            _trayManager?.ShowNotification("取词未完成", ex.Message + "\n点击此通知，打开查词页手动粘贴。",
                System.Windows.Forms.ToolTipIcon.Warning, () => _libraryWindow?.ShowLookup());
        });
    }

    internal static bool ShouldAutoTranslateSelection(string? triggerMode, string? text) =>
        MouseSelectionSettings.NormalizeTriggerMode(triggerMode) switch
        {
            MouseSelectionSettings.AutoAll => true,
            MouseSelectionSettings.AutoEnglish => TextLanguageDetector.IsEnglishText(text),
            _ => false
        };

    private void OnMouseDismissRequested(ScreenPoint point, bool isMouseDown)
    {
        if (_selectionPillWindow is { IsPillVisible: true } pill && !pill.IsContextMenuOpen &&
            (!isMouseDown || !pill.ContainsPhysicalPoint(point)))
        {
            Dispatcher.InvokeAsync(pill.HidePill);
        }

        if (isMouseDown && _activeLookupWindow is { } lookup && lookup.ShouldQueueDismissForMouseDown(point))
            Dispatcher.InvokeAsync(() => lookup.DismissForMouseDown(point));
    }

    private void OnMouseSelectionSettingsChanged(AppSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(UpdateMouseSelectionHook);
            return;
        }

        UpdateMouseSelectionHook();
    }

    private void UpdateMouseSelectionHook()
    {
        try
        {
            _mouseSelectionMonitor?.UpdateHook(
                _activeLookupWindow is { IsVisible: true, IsPinned: false },
                _selectionPillWindow is { IsPillVisible: true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger?.LogWarning(ex, "Mouse selection monitor could not start");
        }
    }

    private async Task QuickSaveFromPillAsync(CaptureResult capture)
    {
        if (_serviceProvider == null) return;
        try
        {
            var lookupService = _serviceProvider.GetRequiredService<ILookupService>();
            var wordRepository = _serviceProvider.GetRequiredService<IWordRepository>();
            var events = _serviceProvider.GetRequiredService<WordCollectionEvents>();
            var syncQueue = _serviceProvider.GetRequiredService<IAnkiSyncQueue>();

            var result = await lookupService.LookupAsync(capture, forceAi: false, CancellationToken.None).ConfigureAwait(true);
            var cmd = new SaveCardCommand(
                capture,
                result,
                new LearningEdits(result.Definition, result.ContextTranslation, string.Empty));
            var (_, _, syncJob) = await wordRepository.SaveAsync(cmd).ConfigureAwait(true);
            events.NotifyWordSaved();
            syncQueue.Enqueue(syncJob.Id);
            _trayManager?.ShowNotification("已存入生词库", $"{result.Word}：{result.Definition}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Quick save from selection pill failed");
            _trayManager?.ShowNotification("直接收藏失败", ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private async Task CopyTranslationFromPillAsync(CaptureResult capture)
    {
        if (_serviceProvider == null) return;
        try
        {
            var lookupService = _serviceProvider.GetRequiredService<ILookupService>();
            var result = await lookupService.LookupAsync(capture, forceAi: false, CancellationToken.None).ConfigureAwait(true);
            var text = string.IsNullOrWhiteSpace(result.Definition) ? result.ContextTranslation : result.Definition;
            if (!string.IsNullOrWhiteSpace(text))
            {
                System.Windows.Clipboard.SetText(text);
                _trayManager?.ShowNotification("已复制译文", text);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Copy translation from selection pill failed");
            _trayManager?.ShowNotification("复制译文失败", ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    private async Task SavePinnedPositionAsync(ScreenPoint origin)
    {
        var settingsService = _serviceProvider?.GetService<ISettingsService>();
        if (settingsService == null) return;
        try
        {
            var current = settingsService.Current;
            if (current.Ui.PinnedPopupX == origin.X && current.Ui.PinnedPopupY == origin.Y)
                return;
            await settingsService.UpdateSettingsAsync(s =>
            {
                s.Ui.PinnedPopupX = origin.X;
                s.Ui.PinnedPopupY = origin.Y;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist pinned popup position");
        }
    }

    internal static async Task WaitForInitialLookupAsync(Task lookupTask, int delayMs)
    {
        if (delayMs <= 0 || lookupTask.IsCompleted)
            return;

        using var delayCts = new CancellationTokenSource();
        var delayTask = Task.Delay(delayMs, delayCts.Token);
        var completed = await Task.WhenAny(lookupTask, delayTask).ConfigureAwait(true);
        if (ReferenceEquals(completed, lookupTask))
            delayCts.Cancel();
    }

    private Task ShowCapturedLookupAsync(CaptureResult capture) => ShowCapturedLookupAsync(capture, forceMini: false);

    private async Task ShowCapturedLookupAsync(CaptureResult capture, bool forceMini)
    {
        _logger?.LogInformation("Hotkey captured selected text ({Length} characters) from {ProcessName}",
            capture.SelectedText.Length, capture.SourceProcess);

        // A pinned card acts as a fixed lookup surface: preserve its position
        // and refresh the result in place for every subsequent capture.
        if (_activeLookupWindow is { IsVisible: true, IsPinned: true } pinnedWindow)
        {
            await pinnedWindow.StartLookupAsync(capture).ConfigureAwait(true);
            return;
        }

        if (_activeLookupWindow != null)
        {
            try { _activeLookupWindow.Close(); } catch { }
            _activeLookupWindow = null;
        }

        var settingsService = _serviceProvider!.GetRequiredService<ISettingsService>();
        var uiSettings = settingsService.Current.Ui;
        var vm = _serviceProvider!.GetRequiredService<LookupViewModel>();
        var defaultMode = UiSettings.NormalizePopupMode(uiSettings.PopupDefaultMode);
        vm.PopupMode = forceMini
            ? (defaultMode == UiSettings.PopupModeMinimal ? UiSettings.PopupModeMinimal : UiSettings.PopupModeMini)
            : defaultMode;
        var lookupWindow = new LookupWindow(vm);
        if (uiSettings.PinnedPopupX is { } px && uiSettings.PinnedPopupY is { } py)
        {
            lookupWindow.RememberedPinnedOrigin = new ScreenPoint(px, py);
        }
        lookupWindow.PinnedPositionChanged += origin => _ = SavePinnedPositionAsync(origin);
        _activeLookupWindow = lookupWindow;
        var windowClosed = false;
        vm.OpenInMainHandoffRequested += handoff =>
        {
            if (!lookupWindow.IsPinned)
            {
                try { lookupWindow.Close(); } catch { }
            }
            if (_libraryWindow == null) return;
            if (handoff.ShouldOpenInLibrary) _ = _libraryWindow.ShowLibraryForHandoffAsync(handoff);
            else _ = _libraryWindow.ShowLookupHandoffAsync(handoff);
        };
        lookupWindow.Closed += (_, _) =>
        {
            windowClosed = true;
            if (ReferenceEquals(_activeLookupWindow, lookupWindow))
                _activeLookupWindow = null;
            UpdateMouseSelectionHook();
        };
        lookupWindow.IsVisibleChanged += (_, _) => UpdateMouseSelectionHook();
        lookupWindow.PinnedChanged += (_, _) => UpdateMouseSelectionHook();

        // Buffer initial fast lookups (offline dictionary) before revealing the popup
        // to eliminate loading flashes and content-driven width jumps.
        var lookupTask = lookupWindow.StartLookupAsync(capture);
        var initialBufferMs = vm.IsMinimalMode ? 150 : 120;
        await WaitForInitialLookupAsync(lookupTask, initialBufferMs).ConfigureAwait(true);

        if (windowClosed || !ReferenceEquals(_activeLookupWindow, lookupWindow))
            return;

        // Create/show the HWND without exposing its default location, then
        // position it in physical pixels using the cursor monitor's DPI.
        lookupWindow.Opacity = 0;
        lookupWindow.Show();
        if (!lookupWindow.PositionNearCursor(capture.CursorPosition))
        {
            _logger?.LogWarning("Lookup popup physical placement failed; falling back to default window position");
        }
        lookupWindow.Opacity = 1;
        _logger?.LogInformation("Lookup popup shown for hotkey capture");
        lookupWindow.Activate();

        await lookupTask.ConfigureAwait(true);
    }

    private void OnToggleLibraryRequested()
    {
        _libraryWindow?.ToggleVisibility();
    }

    private void OnOpenLibraryRequested()
    {
        _libraryWindow?.ShowLibrary();
    }

    private void OnOpenLookupRequested()
    {
        _libraryWindow?.ShowLookup();
    }

    private void OnOpenSettingsRequested()
    {
        _libraryWindow?.ShowSettings();
    }

    private void OnTrayMouseSelectionToggled(bool enabled) => _ = ToggleMouseSelectionFromTrayAsync(enabled);

    private async Task ToggleMouseSelectionFromTrayAsync(bool enabled)
    {
        if (_serviceProvider == null) return;
        try
        {
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();
            await settingsService.UpdateSettingsAsync(s => s.MouseSelection.Enabled = enabled).ConfigureAwait(true);
            if (_serviceProvider.GetService<SettingsViewModel>() is { HasUnsavedChanges: false } settingsVm)
                await settingsVm.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to toggle mouse selection from tray");
        }
    }

    private void InstallDownloadedUpdate()
    {
        if (_serviceProvider == null) return;
        var settings = _serviceProvider.GetRequiredService<SettingsViewModel>();
        var library = _serviceProvider.GetRequiredService<LibraryViewModel>();
        var mainLookup = _serviceProvider.GetRequiredService<WordLookupViewModel>().Lookup;
        var popupLookup = _activeLookupWindow?.DataContext as LookupViewModel;
        if (library.IsEditing || library.IsEditingContext || library.IsSavingEdit || library.IsSavingContext
            || library.IsChangingCollection || library.IsExporting || library.IsSyncingWebDav
            || settings.HasUnsavedChanges || settings.IsSaving || settings.IsSyncingWebDav || settings.IsDictInstalling
            || mainLookup.IsEditingDefinition || mainLookup.IsSaving || mainLookup.IsTranslatingSentence || mainLookup.IsLoading
            || popupLookup is { IsEditingDefinition: true } or { IsSaving: true } or { IsTranslatingSentence: true } or { IsLoading: true })
        {
            settings.UpdateStatus = "请先保存或结束当前查词、编辑、导出和同步操作，再打开安装向导。";
            return;
        }
        try
        {
            _serviceProvider.GetRequiredService<IAppUpdater>().LaunchInstaller();
            Shutdown();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to launch updater");
            settings.UpdateStatus = "无法启动更新安装，请重试或打开下载页面。";
        }
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        var settingsService = _serviceProvider?.GetService<ISettingsService>();
        if (settingsService == null) return;
        if (!string.Equals(ThemeManager.NormalizeMode(settingsService.Current.Ui.ThemeMode), ThemeManager.SystemMode, StringComparison.Ordinal))
            return;
        Dispatcher?.InvokeAsync(() => ThemeManager.ApplyTheme(ThemeManager.SystemMode));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _logger?.LogInformation("Word Catcher shutting down...");
        _serviceProvider?.GetService<SettingsViewModel>()?.CheckForUpdatesCancelCommand.Execute(null);

        if (_serviceProvider?.GetService<ISettingsService>() is { } settingsService)
            settingsService.SettingsChanged -= OnMouseSelectionSettingsChanged;
        var mouseSelectionMonitor = _mouseSelectionMonitor;
        _mouseSelectionMonitor = null;
        mouseSelectionMonitor?.Dispose();
        _selectionPillWindow?.Close();
        _hotkeyManager?.Dispose();
        _trayManager?.Dispose();
        _webDavSync?.Dispose();
        _syncWorker?.Dispose();

        if (_ownsSingleInstanceMutex && _singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The process may be exiting after startup failed; never let
                // mutex cleanup mask the original shutdown path.
            }
            _ownsSingleInstanceMutex = false;
        }
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}

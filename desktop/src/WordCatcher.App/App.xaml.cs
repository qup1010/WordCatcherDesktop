using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WordCatcher.App.Services;
using WordCatcher.App.Tray;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Windows;
using WordCatcher.Core.Interfaces;
using WordCatcher.Infrastructure.Anki;
using WordCatcher.Infrastructure.Database;
using WordCatcher.Infrastructure.Dictionary;
using WordCatcher.Infrastructure.Logging;
using WordCatcher.Infrastructure.Security;
using WordCatcher.Infrastructure.Settings;
using WordCatcher.Infrastructure.Translation;

namespace WordCatcher.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "WordCatcher_Desktop_SingleInstance_Mutex";
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private IServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    private HotkeyManager? _hotkeyManager;
    private TrayIconManager? _trayManager;
    private AnkiSyncWorker? _syncWorker;
    private LibraryWindow? _libraryWindow;
    private LookupWindow? _activeLookupWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Single instance check
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
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

            // 4. Start Anki Background Sync Worker
            _syncWorker = _serviceProvider.GetRequiredService<AnkiSyncWorker>();
            _syncWorker.Start();

            // 5. Initialize Services & Tray
            _hotkeyManager = _serviceProvider.GetRequiredService<HotkeyManager>();
            var settingsService = _serviceProvider.GetRequiredService<ISettingsService>();

            var registered = _hotkeyManager.Register(settingsService.Current.Hotkey);
            if (!registered)
            {
                System.Windows.MessageBox.Show(
                    $"默认快捷键「{settingsService.Current.Hotkey}」被占用，您可以在设置页面更换热键。",
                    "热键占用提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            _hotkeyManager.HotkeyTriggered += OnHotkeyTriggered;

            _trayManager = _serviceProvider.GetRequiredService<TrayIconManager>();
            _trayManager.OpenLibraryRequested += OnOpenLibraryRequested;
            _trayManager.OpenSettingsRequested += OnOpenSettingsRequested;
            _trayManager.CaptureNowRequested += () => OnHotkeyTriggered();

            _libraryWindow = _serviceProvider.GetRequiredService<LibraryWindow>();

            _logger.LogInformation("Word Catcher initialized and resident in system tray");
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

        // Database & Repositories
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteMigrationRunner>();
        services.AddSingleton<IWordRepository, WordRepository>();

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

        // Anki Sync
        services.AddSingleton<IAnkiClient, AnkiConnectClient>();
        services.AddSingleton<AnkiSyncWorker>();
        services.AddSingleton<IAnkiSyncQueue>(sp => sp.GetRequiredService<AnkiSyncWorker>());

        // Win32 Capture & Hotkey & Tray
        services.AddSingleton<ISelectionCaptureService, SelectionCaptureService>();
        services.AddSingleton<SpeechService>();
        services.AddSingleton<HotkeyManager>();
        services.AddSingleton<TrayIconManager>();

        // ViewModels
        services.AddTransient<LookupViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // Windows
        services.AddSingleton<LibraryWindow>();
    }

    private async void OnHotkeyTriggered()
    {
        var captureService = _serviceProvider?.GetRequiredService<ISelectionCaptureService>();
        if (captureService == null) return;

        var capture = await captureService.CaptureSelectionAsync().ConfigureAwait(true);
        if (capture == null || string.IsNullOrWhiteSpace(capture.SelectedText))
        {
            _logger?.LogInformation("No text captured on hotkey trigger");
            _trayManager?.ShowNotification(
                "未获取到选中文字",
                "请先在目标程序中选中文字，再按 Alt+Q 重试。管理员权限程序可能需要提升本应用权限。",
                System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }

        // A pinned card acts as a fixed lookup surface: preserve its position
        // and refresh the result in place for every subsequent capture.
        if (_activeLookupWindow is { IsVisible: true, IsPinned: true } pinnedWindow)
        {
            pinnedWindow.Activate();
            await pinnedWindow.StartLookupAsync(capture).ConfigureAwait(true);
            return;
        }

        if (_activeLookupWindow != null)
        {
            try { _activeLookupWindow.Close(); } catch { }
            _activeLookupWindow = null;
        }

        var vm = _serviceProvider!.GetRequiredService<LookupViewModel>();
        var lookupWindow = new LookupWindow(vm);
        _activeLookupWindow = lookupWindow;
        lookupWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeLookupWindow, lookupWindow))
                _activeLookupWindow = null;
        };

        lookupWindow.Show();
        // The window must be attached to a presentation source before reading
        // its DPI. This is especially important on a secondary high-DPI monitor.
        lookupWindow.PositionNearCursor(capture.CursorPosition);
        lookupWindow.Activate();

        await lookupWindow.StartLookupAsync(capture).ConfigureAwait(true);
    }

    private void OnOpenLibraryRequested()
    {
        _libraryWindow?.ShowLibrary();
    }

    private void OnOpenSettingsRequested()
    {
        _libraryWindow?.ShowSettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Word Catcher shutting down...");

        _hotkeyManager?.Dispose();
        _trayManager?.Dispose();
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

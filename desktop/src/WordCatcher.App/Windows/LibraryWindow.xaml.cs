using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WordCatcher.App.Services;
using WordCatcher.App.Themes;
using WordCatcher.App.ViewModels;

namespace WordCatcher.App.Windows;

public partial class LibraryWindow : Window
{
    private readonly LibraryViewModel _libraryVm;
    private readonly SyncViewModel _syncVm;
    private readonly SettingsViewModel _settingsVm;
    private readonly WordLookupViewModel? _lookupVm;
    private readonly DispatcherTimer _syncRefreshTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public LibraryWindow(
        LibraryViewModel libraryVm,
        SyncViewModel syncVm,
        SettingsViewModel settingsVm,
        WordLookupViewModel? lookupVm = null)
    {
        InitializeComponent();
        WpfUiResourceScope.PreferApplicationResources(this);
        _libraryVm = libraryVm;
        _syncVm = syncVm;
        _settingsVm = settingsVm;
        _lookupVm = lookupVm;
        _syncRefreshTimer.Tick += async (_, _) => await _syncVm.RefreshAsync();
        DataContext = _settingsVm;

        LibraryTab.DataContext = _libraryVm;
        LookupTab.DataContext = _lookupVm;
        SyncTab.DataContext = _syncVm;
        SettingsTab.DataContext = _settingsVm;
        _settingsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ApiKey) && ApiKeyBox.Password != _settingsVm.ApiKey)
                ApiKeyBox.Password = _settingsVm.ApiKey;
        };
        _libraryVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsEditing) && _libraryVm.IsEditing)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    EditWordBox.Focus();
                    EditWordBox.SelectAll();
                }));
            if (e.PropertyName == nameof(LibraryViewModel.SelectedWord))
                DetailScroll.ScrollToTop();
        };
        MainTabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source != MainTabs) return;
            UpdateNavigationSelection();
            UpdateSyncRefreshTimer();
            if (e.Source == MainTabs && MainTabs.SelectedItem != SettingsTab)
                _settingsVm.CancelHotkeyRecording();
            if (e.Source == MainTabs && IsLoaded && MainTabs.SelectedItem == SyncTab)
                await _syncVm.RefreshAsync();
        };

        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            if (WindowBackdrop.TryApply(this, 42))
            {
                Background = System.Windows.Media.Brushes.Transparent;
                TitleBar.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(221, 242, 247, 245));
            }
        };
        UpdateNavigationSelection();
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
        };
        Deactivated += (_, _) => _settingsVm.CancelHotkeyRecording();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _settingsVm.CancelHotkeyRecording();
            UpdateSyncRefreshTimer();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (_settingsVm.IsRecordingHotkey)
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                _settingsVm.CaptureHotkey(key, Keyboard.Modifiers);
                e.Handled = key != Key.Tab;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D4)
            {
                MainTabs.SelectedIndex = e.Key - Key.D1;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (MainTabs.SelectedItem == LookupTab) LookupPage.FocusQuery();
                else if (!_libraryVm.IsEditing)
                {
                    MainTabs.SelectedItem = LibraryTab;
                    SearchBox.Focus();
                    SearchBox.SelectAll();
                }
                e.Handled = true;
            }
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (MainTabs.SelectedItem == SettingsTab && _settingsVm.SaveSettingsCommand.CanExecute(null))
                    _settingsVm.SaveSettingsCommand.Execute(null);
                else if (MainTabs.SelectedItem == LibraryTab && _libraryVm.IsEditing && _libraryVm.SaveEditCommand.CanExecute(null))
                    _libraryVm.SaveEditCommand.Execute(null);
                e.Handled = true;
            }
            if (e.Key == Key.Escape && MainTabs.SelectedItem == LibraryTab && _libraryVm.IsEditing)
            {
                if (_libraryVm.CancelEditCommand.CanExecute(null)) _libraryVm.CancelEditCommand.Execute(null);
                e.Handled = true;
            }
            if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None
                && MainTabs.SelectedItem == LibraryTab && _libraryVm.StartEditCommand.CanExecute(null))
            {
                _libraryVm.StartEditCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateSyncRefreshTimer();
        await _libraryVm.InitializeAsync();
        await _syncVm.InitializeAsync();
        await _settingsVm.InitializeAsync();
    }

    public void ShowLibrary()
    {
        MainTabs.SelectedItem = LibraryTab;
        ShowAndActivate();
        if (IsLoaded && !_libraryVm.IsEditing) _ = _libraryVm.InitializeAsync();
    }

    public void ShowSync()
    {
        MainTabs.SelectedItem = SyncTab;
        ShowAndActivate();
        if (IsLoaded) _ = _syncVm.RefreshAsync();
    }

    private void UpdateSyncRefreshTimer()
    {
        if (IsLoaded && IsVisible && MainTabs.SelectedItem == SyncTab)
            _syncRefreshTimer.Start();
        else
            _syncRefreshTimer.Stop();
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string destination }) return;

        MainTabs.SelectedItem = destination switch
        {
            "Library" => LibraryTab,
            "Lookup" => LookupTab,
            "Sync" => SyncTab,
            "Settings" => SettingsTab,
            _ => MainTabs.SelectedItem
        };
        if (MainTabs.SelectedItem == LookupTab) LookupPage.FocusQuery();
    }

    private void UpdateNavigationSelection()
    {
        LibraryNavigationItem.IsActive = MainTabs.SelectedItem == LibraryTab;
        LookupNavigationItem.IsActive = MainTabs.SelectedItem == LookupTab;
        SyncNavigationItem.IsActive = MainTabs.SelectedItem == SyncTab;
        SettingsNavigationItem.IsActive = MainTabs.SelectedItem == SettingsTab;
    }

    private void OpenAnkiSettings_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = SettingsTab;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AnkiSettingsGroup.BringIntoView()));
    }

    public void ShowSettings()
    {
        MainTabs.SelectedItem = SettingsTab;
        ShowAndActivate();
    }

    private void ShowAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Hide to tray rather than exiting
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _libraryVm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
        if (e.Key == Key.Enter)
        {
            if (_libraryVm.SearchCommand.CanExecute(null))
            {
                _libraryVm.SearchCommand.Execute(null);
            }
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_settingsVm != null && _settingsVm.ApiKey != ApiKeyBox.Password)
            _settingsVm.ApiKey = ApiKeyBox.Password;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

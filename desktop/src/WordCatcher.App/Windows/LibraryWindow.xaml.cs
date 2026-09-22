using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using WordCatcher.App.ViewModels;

namespace WordCatcher.App.Windows;

public partial class LibraryWindow : Window
{
    private readonly LibraryViewModel _libraryVm;
    private readonly SyncViewModel _syncVm;
    private readonly SettingsViewModel _settingsVm;

    public LibraryWindow(
        LibraryViewModel libraryVm,
        SyncViewModel syncVm,
        SettingsViewModel settingsVm)
    {
        InitializeComponent();
        _libraryVm = libraryVm;
        _syncVm = syncVm;
        _settingsVm = settingsVm;
        DataContext = _settingsVm;

        LibraryTab.DataContext = _libraryVm;
        SyncTab.DataContext = _syncVm;
        SettingsTab.DataContext = _settingsVm;
        _settingsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ApiKey) && ApiKeyBox.Password != _settingsVm.ApiKey)
                ApiKeyBox.Password = _settingsVm.ApiKey;
        };
        MainTabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == MainTabs && MainTabs.SelectedItem != SettingsTab)
                _settingsVm.CancelHotkeyRecording();
            if (e.Source == MainTabs && IsLoaded && MainTabs.SelectedItem == SyncTab)
                await _syncVm.RefreshAsync();
        };

        Loaded += OnLoaded;
        Deactivated += (_, _) => _settingsVm.CancelHotkeyRecording();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _settingsVm.CancelHotkeyRecording(); };
        PreviewKeyDown += (_, e) =>
        {
            if (_settingsVm.IsRecordingHotkey)
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                _settingsVm.CaptureHotkey(key, Keyboard.Modifiers);
                e.Handled = key != Key.Tab;
                return;
            }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !_libraryVm.IsEditing)
            {
                MainTabs.SelectedItem = LibraryTab;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
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
}

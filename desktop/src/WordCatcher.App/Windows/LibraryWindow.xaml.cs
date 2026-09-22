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

        LibraryTab.DataContext = _libraryVm;
        SyncTab.DataContext = _syncVm;
        SettingsTab.DataContext = _settingsVm;

        Loaded += OnLoaded;
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
    }

    public void ShowSync()
    {
        MainTabs.SelectedItem = SyncTab;
        ShowAndActivate();
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
        if (e.Key == Key.Enter)
        {
            if (_libraryVm.SearchCommand.CanExecute(null))
            {
                _libraryVm.SearchCommand.Execute(null);
            }
        }
    }
}

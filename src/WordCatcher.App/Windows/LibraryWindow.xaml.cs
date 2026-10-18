using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;
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

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public Win32Rect Monitor;
        public Win32Rect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Win32Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Win32Point lpPoint);

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
            if (e.PropertyName == nameof(SettingsViewModel.WebDavPassword) && WebDavPasswordBox.Password != _settingsVm.WebDavPassword)
                WebDavPasswordBox.Password = _settingsVm.WebDavPassword;
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
            UpdateWindowStateVisuals();
        };
        UpdateNavigationSelection();
        StateChanged += (_, _) =>
        {
            UpdateWindowStateVisuals();
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(UpdateWindowStateVisuals));
        };
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
            {
                UpdateWindowStateVisuals();
            }
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
            if (MainTabs.SelectedItem == LibraryTab && _libraryVm.IsReviewMode && Keyboard.Modifiers == ModifierKeys.None)
            {
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (focused is not TextBox)
                {
                    if (e.Key == Key.Space)
                    {
                        _libraryVm.ToggleAnswerCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key is Key.Right or Key.J)
                    {
                        _libraryVm.NextReviewCardCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key is Key.Left or Key.K)
                    {
                        _libraryVm.PrevReviewCardCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.P)
                    {
                        _libraryVm.SpeakWordCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                    if (e.Key == Key.Escape)
                    {
                        _libraryVm.ExitReviewCommand.Execute(null);
                        e.Handled = true;
                        return;
                    }
                }
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

    public void ShowBackgroundWebDavFailure(string message)
    {
        _libraryVm.StatusMessage = $"自动 WebDAV 同步失败：{message}";
    }

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "向下还原" : "最大化");

        if (RootLayoutGrid == null) return;

        if (!maximized)
        {
            RootLayoutGrid.Margin = new Thickness(0);
            RootLayoutGrid.MaxWidth = double.PositiveInfinity;
            RootLayoutGrid.MaxHeight = double.PositiveInfinity;
            return;
        }

        // Compensate for the Win32 maximized border overhang so the top-right
        // caption buttons (Minimize / Maximize / Close) never clip off-screen.
        var fallbackBorder = SystemParameters.WindowResizeBorderThickness;
        var margin = new Thickness(
            Math.Max(6, fallbackBorder.Left + 4),
            Math.Max(6, fallbackBorder.Top + 4),
            Math.Max(6, fallbackBorder.Right + 4),
            Math.Max(6, fallbackBorder.Bottom + 4));

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out var clientRect))
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref monitorInfo))
            {
                var topLeft = new Win32Point { X = clientRect.Left, Y = clientRect.Top };
                var bottomRight = new Win32Point { X = clientRect.Right, Y = clientRect.Bottom };
                ClientToScreen(hwnd, ref topLeft);
                ClientToScreen(hwnd, ref bottomRight);

                var dpi = VisualTreeHelper.GetDpi(this);
                var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

                var leftOverhang = Math.Max(0, monitorInfo.WorkArea.Left - topLeft.X) / scaleX;
                var topOverhang = Math.Max(0, monitorInfo.WorkArea.Top - topLeft.Y) / scaleY;
                var rightOverhang = Math.Max(0, bottomRight.X - monitorInfo.WorkArea.Right) / scaleX;
                var bottomOverhang = Math.Max(0, bottomRight.Y - monitorInfo.WorkArea.Bottom) / scaleY;

                if (leftOverhang > 0 || topOverhang > 0 || rightOverhang > 0 || bottomOverhang > 0)
                {
                    margin = new Thickness(leftOverhang, topOverhang, rightOverhang, bottomOverhang);
                }

                var workWidthDip = (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / scaleX;
                var workHeightDip = (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / scaleY;
                if (workWidthDip > 0 && workHeightDip > 0)
                {
                    RootLayoutGrid.MaxWidth = workWidthDip;
                    RootLayoutGrid.MaxHeight = workHeightDip;
                }
            }
        }

        RootLayoutGrid.Margin = margin;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateWindowStateVisuals();
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

    private void WebDavPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_settingsVm != null && _settingsVm.WebDavPassword != WebDavPasswordBox.Password)
            _settingsVm.WebDavPassword = WebDavPasswordBox.Password;
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

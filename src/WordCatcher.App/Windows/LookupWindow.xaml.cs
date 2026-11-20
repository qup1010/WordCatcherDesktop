using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using WordCatcher.App.Interop;
using WordCatcher.App.Services;
using WordCatcher.App.Themes;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Windows;

public partial class LookupWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly LookupViewModel _viewModel;
    private readonly ISettingsService? _settingsService;
    private bool _isClosing;
    private bool _isApplyingPhysicalPlacement;
    private ScreenPoint? _physicalAnchor;
    private string? _resultContext;

    public bool IsPinned => PinToggle.IsChecked == true;

    public LookupWindow(LookupViewModel viewModel, ISettingsService? settingsService = null)
    {
        InitializeComponent();
        WpfUiResourceScope.PreferApplicationResources(this);
        _viewModel = viewModel;
        _settingsService = settingsService;
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Deactivated += Window_Deactivated;
        SizeChanged += (_, _) => OnPopupSizeChanged();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => RepositionPopup(preserveCurrentOrigin: true)));
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_isClosing || IsPinned || !IsLoaded || !IsVisible)
            return;

        if (_settingsService?.Current.Ui.ClosePopupOnDeactivate ?? true)
        {
            try
            {
                _isClosing = true;
                Close();
            }
            catch (InvalidOperationException)
            {
                _isClosing = false;
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LookupViewModel.HasResult) && _viewModel.HasResult)
        {
            var context = _viewModel.SelectedText + "\n" + _viewModel.OriginalSentence;
            if (_resultContext != context) ResultScroll.ScrollToTop();
            _resultContext = context;
        }
    }

    private void OnRequestClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        Dispatcher.Invoke(Close);
    }

    public Task StartLookupAsync(CaptureResult capture)
    {
        return _viewModel.StartLookupAsync(capture);
    }

    public bool PositionNearCursor(ScreenPoint physicalCursor)
    {
        _physicalAnchor = physicalCursor;
        return RepositionPopup(preserveCurrentOrigin: false);
    }

    private void ApplyDisplayConstraints(double workWidth, double workHeight, double scale)
    {
        var fit = WindowSizing.FitPopup(workWidth, workHeight, scale);
        MinWidth = 0;
        MaxWidth = fit.Width;
        Width = fit.Width;
        MinWidth = fit.Width;
        MaxHeight = fit.MaxHeight;
    }

    private void OnPopupSizeChanged()
    {
        if (_isApplyingPhysicalPlacement || _isClosing || _physicalAnchor is null || !IsLoaded)
            return;

        // Keep the card's current position during content growth (including a
        // pinned refresh), and clamp its new physical rectangle to the work area.
        RepositionPopup(preserveCurrentOrigin: true);
    }

    private bool RepositionPopup(bool preserveCurrentOrigin)
    {
        if (_isApplyingPhysicalPlacement || _isClosing || _physicalAnchor is null)
            return false;

        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();
        _isApplyingPhysicalPlacement = true;
        try
        {
            var placementAnchor = preserveCurrentOrigin && GetWindowRect(hwnd, out var currentRect)
                ? new ScreenPoint(currentRect.Left, currentRect.Top)
                : _physicalAnchor.Value;
            var monitor = MonitorFromPoint(new NativeMethods.POINT { X = placementAnchor.X, Y = placementAnchor.Y }, MonitorDefaultToNearest);
            var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return false;

            var workArea = new PhysicalRectangle(info.WorkArea.Left, info.WorkArea.Top,
                info.WorkArea.Right, info.WorkArea.Bottom);
            var scale = GetMonitorDpiScale(monitor);

            // Move the transparent HWND onto the target screen before measuring
            // so WPF evaluates ActualWidth/ActualHeight under the target monitor DPI.
            if (!preserveCurrentOrigin)
            {
                SetWindowPos(hwnd, IntPtr.Zero, workArea.Left, workArea.Top, 0, 0,
                    0x0001 | SetWindowPosNoZOrder | SetWindowPosNoActivate); // SWP_NOSIZE
            }

            ApplyDisplayConstraints(workArea.Width, workArea.Height, scale);
            UpdateLayout();

            var widthDip = ActualWidth > 0 ? ActualWidth : Width;
            var heightDip = ActualHeight > 0 ? ActualHeight : Math.Min(480, MaxHeight);
            var physical = WindowSizing.PlacePopup(placementAnchor, workArea, scale,
                widthDip, heightDip, preserveCurrentOrigin);

            return SetWindowPos(hwnd, IntPtr.Zero, physical.Left, physical.Top,
                physical.Width, physical.Height, SetWindowPosNoZOrder | SetWindowPosNoActivate);
        }
        finally
        {
            _isApplyingPhysicalPlacement = false;
        }
    }

    private static double GetMonitorDpiScale(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (BadImageFormatException) { }

        // 96 DPI is the stable fallback on systems without GetDpiForMonitor.
        return 1.0;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _isClosing = true;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            PinToggle.IsChecked = !IsPinned;
            e.Handled = true;
        }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            // Let editors and selectable result text copy only the user's selection.
            if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            if (_viewModel.CopyResultCommand.CanExecute(null))
            {
                _viewModel.CopyResultCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_viewModel.ForceAiCommand.CanExecute(null))
            {
                _viewModel.ForceAiCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
                 && !IsButtonSource(Keyboard.FocusedElement as DependencyObject)
                 && Keyboard.FocusedElement is not System.Windows.Controls.TextBox { IsReadOnly: false })
        {
            if (_viewModel.SaveCommand.CanExecute(null))
            {
                _viewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsButtonSource(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released between the event and DragMove.
        }
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
                return true;
        }
        return false;
    }

    private void PinToggle_Checked(object sender, RoutedEventArgs e)
    {
        _viewModel.KeepOpenAfterSave = true;
        Topmost = true;
        PinToggle.ToolTip = "取消固定（Ctrl+P）";
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, "取消固定查词浮窗");
    }

    private void PinToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _viewModel.KeepOpenAfterSave = false;
        PinToggle.ToolTip = "固定卡片；保持位置并在此刷新后续取词 (Ctrl+P)";
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, "固定查词浮窗");
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        Deactivated -= Window_Deactivated;
        _viewModel.CancelLookup();
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }
}

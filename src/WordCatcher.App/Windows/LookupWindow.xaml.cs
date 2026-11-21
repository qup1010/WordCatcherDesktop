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
using WordCatcher.Core.Models;
using ContextMenu = System.Windows.Controls.ContextMenu;

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

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly LookupViewModel _viewModel;
    private volatile bool _isClosingForHitTest;
    private bool _isApplyingPhysicalPlacement;
    private bool _hasUserDragged;
    private ScreenPoint? _physicalAnchor;
    private string? _resultContext;
    private readonly object _hitTestLock = new();
    private PhysicalRectangle _physicalBounds;
    private PhysicalRectangle _lastPlacedPhysicalBounds;
    private bool _isVisibleForHitTest;
    private bool _isPinnedForHitTest;

    public bool IsPinned => PinToggle.IsChecked == true;
    public ScreenPoint? RememberedPinnedOrigin { get; set; }
    internal PhysicalRectangle LastPlacedPhysicalBoundsForTest => _lastPlacedPhysicalBounds;
    internal bool HasDismissedForTest => _isClosingForHitTest;
    internal event EventHandler? PinnedChanged;
    internal event Action<ScreenPoint>? PinnedPositionChanged;

    public LookupWindow(LookupViewModel viewModel)
    {
        InitializeComponent();
        _isPinnedForHitTest = IsPinned;
        WpfUiResourceScope.PreferApplicationResources(this);
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        Deactivated += Window_Deactivated;
        IsVisibleChanged += (_, _) => RefreshPhysicalBounds();
        LocationChanged += (_, _) => RefreshPhysicalBounds();
        SizeChanged += (_, _) =>
        {
            OnPopupSizeChanged();
            RefreshPhysicalBounds();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            RepositionPopup(preserveCurrentOrigin: true);
            RefreshPhysicalBounds();
        }));
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (CanDismiss && !IsOwnedWindow(NativeMethods.GetForegroundWindow()))
            Dismiss();
    }

    private bool CanDismiss => !_isClosingForHitTest && IsVisible && !IsPinned;

    internal bool ShouldQueueDismissForMouseDown(ScreenPoint point)
    {
        lock (_hitTestLock)
        {
            return !_isClosingForHitTest && _isVisibleForHitTest && !_isPinnedForHitTest &&
                !IsInsidePhysicalBounds(point);
        }
    }

    private bool IsOverCapturedMenu(ScreenPoint point)
    {
        if (Mouse.Captured is ContextMenu { IsOpen: true, PlacementTarget: { } target } menu &&
            ReferenceEquals(Window.GetWindow(target), this) && PresentationSource.FromVisual(menu) != null)
        {
            var local = menu.PointFromScreen(new Point(point.X, point.Y));
            if (new Rect(menu.RenderSize).Contains(local))
                return true;
        }

        var lookupHandle = new WindowInteropHelper(this).Handle;
        foreach (PresentationSource source in PresentationSource.CurrentSources)
        {
            if (source is not HwndSource hwndSource || hwndSource.IsDisposed ||
                hwndSource.Handle == IntPtr.Zero || hwndSource.Handle == lookupHandle)
                continue;

            if (!IsOwnedWindow(hwndSource.Handle) && !IsPopupSourceOwnedByThisWindow(hwndSource))
                continue;

            if (GetWindowRect(hwndSource.Handle, out var rect) &&
                point.X >= rect.Left && point.X < rect.Right &&
                point.Y >= rect.Top && point.Y < rect.Bottom)
                return true;
        }

        return false;
    }

    private bool IsPopupSourceOwnedByThisWindow(HwndSource source)
    {
        if (source.RootVisual is not DependencyObject root)
            return false;

        if (LogicalTreeHelper.GetParent(root) is not System.Windows.Controls.Primitives.Popup { IsOpen: true } popup)
            return false;

        var target = popup.PlacementTarget ?? (popup.Child as ContextMenu)?.PlacementTarget;
        if (target != null && ReferenceEquals(Window.GetWindow(target), this))
            return true;

        for (DependencyObject? current = popup; current != null; current = LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
                return true;
        }

        return false;
    }

    internal void DismissForMouseDown(ScreenPoint point)
    {
        if (CanDismiss && !ContainsPhysicalPoint(point) && !IsOverCapturedMenu(point) &&
            !IsOwnedWindow(WindowFromPoint(new NativeMethods.POINT { X = point.X, Y = point.Y })))
            Dismiss();
    }

    private void Dismiss()
    {
        if (_isClosingForHitTest) return;
        try
        {
            _isClosingForHitTest = true;
            Close();
        }
        catch (InvalidOperationException)
        {
            _isClosingForHitTest = false;
        }
    }

    public bool ContainsPhysicalPoint(ScreenPoint point)
    {
        lock (_hitTestLock)
        {
            return _isVisibleForHitTest && IsInsidePhysicalBounds(point);
        }
    }

    private bool IsInsidePhysicalBounds(ScreenPoint point) =>
        point.X >= _physicalBounds.Left && point.X < _physicalBounds.Right &&
        point.Y >= _physicalBounds.Top && point.Y < _physicalBounds.Bottom;

    private void RefreshPhysicalBounds()
    {
        var rect = default(NativeRect);
        var hwnd = !_isClosingForHitTest && IsVisible ? new WindowInteropHelper(this).Handle : IntPtr.Zero;
        var visible = hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
        lock (_hitTestLock)
        {
            _physicalBounds = visible ? new PhysicalRectangle(rect.Left, rect.Top, rect.Right, rect.Bottom) : default;
            _isVisibleForHitTest = visible;
        }
    }

    private bool IsOwnedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var lookupHandle = new WindowInteropHelper(this).Handle;
        const uint root = 2; // GA_ROOT
        const uint owner = 4; // GW_OWNER
        for (var current = GetAncestor(hwnd, root); current != IntPtr.Zero; current = GetWindow(current, owner))
        {
            if (current == lookupHandle) return true;
        }
        return false;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LookupViewModel.HasResult) && _viewModel.HasResult)
        {
            var context = _viewModel.SelectedText + "\n" + _viewModel.OriginalSentence;
            if (_resultContext != context) ResultScroll.ScrollToTop();
            _resultContext = context;
        }

        if (e.PropertyName is nameof(LookupViewModel.PopupMode) or nameof(LookupViewModel.IsLongText)
            || (IsAdaptiveWidthMode() && e.PropertyName is nameof(LookupViewModel.MinimalDisplayText)
                or nameof(LookupViewModel.HasResult) or nameof(LookupViewModel.HasError)
                or nameof(LookupViewModel.IsLoading)))
        {
            Dispatcher.BeginInvoke(new Action(() => RepositionPopup(preserveCurrentOrigin: true)));
        }
    }

    private bool IsAdaptiveWidthMode() => _viewModel.IsMinimalMode;

    private void OnRequestClose()
    {
        Dispatcher.Invoke(Dismiss);
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

    public bool MoveToPhysicalOrigin(ScreenPoint physicalOrigin)
    {
        _physicalAnchor = physicalOrigin;
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();
        if (hwnd == IntPtr.Zero) return false;
        SetWindowPos(hwnd, IntPtr.Zero, physicalOrigin.X, physicalOrigin.Y, 0, 0,
            0x0001 | SetWindowPosNoZOrder | SetWindowPosNoActivate);
        return RepositionPopup(preserveCurrentOrigin: true);
    }

    private void ApplyDisplayConstraints(double workWidth, double workHeight, double scale)
    {
        if (_viewModel.IsMinimalMode)
        {
            var fit = WindowSizing.FitPopup(workWidth, workHeight, scale, 340, 196);
            ClearValue(WidthProperty);
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = Math.Min(72, fit.Width);
            MaxWidth = fit.Width;
            MaxHeight = fit.MaxHeight;
            return;
        }

        var (preferredWidth, preferredMaxHeight) = _viewModel.IsCompactWordCard
            ? (352, 340)
            : _viewModel.IsCompactSentenceCard
                ? (440, 360)
                : (420, 680);
        var fixedFit = WindowSizing.FitPopup(workWidth, workHeight, scale, preferredWidth, preferredMaxHeight);
        SizeToContent = SizeToContent.Height;
        MinWidth = 0;
        MaxWidth = fixedFit.Width;
        Width = fixedFit.Width;
        MinWidth = fixedFit.Width;
        MaxHeight = fixedFit.MaxHeight;
    }

    private void OnPopupSizeChanged()
    {
        if (_isApplyingPhysicalPlacement || _isClosingForHitTest || _physicalAnchor is null || !IsLoaded)
            return;

        // Keep the card's current position during content growth (including a
        // pinned refresh), and clamp its new physical rectangle to the work area.
        RepositionPopup(preserveCurrentOrigin: true);
    }

    private bool RepositionPopup(bool preserveCurrentOrigin)
    {
        if (_isApplyingPhysicalPlacement || _isClosingForHitTest || _physicalAnchor is null)
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

            var prevWidth = Width;
            var prevSizeToContent = SizeToContent;
            ApplyDisplayConstraints(workArea.Width, workArea.Height, scale);
            if (!preserveCurrentOrigin
                || SizeToContent == SizeToContent.WidthAndHeight
                || prevSizeToContent != SizeToContent
                || Math.Abs(Width - prevWidth) > 0.5)
            {
                UpdateLayout();
            }

            double widthDip;
            double heightDip;
            if (SizeToContent == SizeToContent.WidthAndHeight && Content is FrameworkElement content)
            {
                content.Measure(new Size(MaxWidth, MaxHeight));
                var measuredW = Math.Max(ActualWidth, content.DesiredSize.Width);
                var measuredH = Math.Max(ActualHeight, content.DesiredSize.Height);
                widthDip = Math.Clamp(measuredW > 0 ? measuredW : MinWidth, MinWidth, MaxWidth);
                heightDip = Math.Clamp(measuredH > 0 ? measuredH : 120, 1, MaxHeight);
            }
            else
            {
                widthDip = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
                var measuredH = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
                heightDip = measuredH > 0 ? measuredH : Math.Min(480, MaxHeight);
            }
            var physical = WindowSizing.PlacePopup(placementAnchor, workArea, scale,
                widthDip, heightDip, preserveCurrentOrigin);
            _lastPlacedPhysicalBounds = physical;

            return SetWindowPos(hwnd, IntPtr.Zero, physical.Left, physical.Top,
                physical.Width, physical.Height, SetWindowPosNoZOrder | SetWindowPosNoActivate);
        }
        finally
        {
            _isApplyingPhysicalPlacement = false;
            RefreshPhysicalBounds();
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
            Dismiss();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && (_viewModel.IsMiniMode || _viewModel.IsMinimalMode))
        {
            _viewModel.ExpandToFullCommand.Execute(null);
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

        PerformDragMove();
    }

    private void MinimalCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsButtonSource(e.OriginalSource as DependencyObject))
            return;

        PerformDragMove();
    }

    private void MinimalCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
            return;

        e.Handled = true;
        Dismiss();
    }

    private void StandardCard_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right || IsInteractiveOrTextSource(e.OriginalSource as DependencyObject))
            return;

        e.Handled = true;
        Dismiss();
    }

    private void PerformDragMove()
    {
        try
        {
            DragMove();
            _hasUserDragged = true;
            RefreshPhysicalBounds();
            if (IsPinned && TryGetCurrentPhysicalOrigin(out var origin))
            {
                RememberedPinnedOrigin = origin;
                PinnedPositionChanged?.Invoke(origin);
            }
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released between the event and DragMove.
        }
    }

    private static bool IsInteractiveOrTextSource(DependencyObject? source)
    {
        for (var current = source; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.ContextMenu)
                return true;
        }
        return false;
    }

    private bool TryGetCurrentPhysicalOrigin(out ScreenPoint origin)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            origin = new ScreenPoint(rect.Left, rect.Top);
            return true;
        }
        origin = default;
        return false;
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
        lock (_hitTestLock) _isPinnedForHitTest = true;
        _viewModel.KeepOpenAfterSave = true;
        Topmost = true;
        PinToggle.ToolTip = "取消固定（Ctrl+P）";
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, "取消固定查词浮窗");
        if (!_hasUserDragged && RememberedPinnedOrigin is { } remembered)
        {
            MoveToPhysicalOrigin(remembered);
        }
        else if (_hasUserDragged && TryGetCurrentPhysicalOrigin(out var current))
        {
            RememberedPinnedOrigin = current;
            PinnedPositionChanged?.Invoke(current);
        }
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PinToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        lock (_hitTestLock) _isPinnedForHitTest = false;
        _viewModel.KeepOpenAfterSave = false;
        PinToggle.ToolTip = "固定卡片；保持位置并在此刷新后续取词 (Ctrl+P)";
        System.Windows.Automation.AutomationProperties.SetName(PinToggle, "固定查词浮窗");
        PinnedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosingForHitTest = true;
        RefreshPhysicalBounds();
        Deactivated -= Window_Deactivated;
        _viewModel.CancelLookup();
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }
}

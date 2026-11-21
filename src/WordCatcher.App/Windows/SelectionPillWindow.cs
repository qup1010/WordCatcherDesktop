using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WordCatcher.App.Interop;
using WordCatcher.App.Themes;
using WordCatcher.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using PathShape = System.Windows.Shapes.Path;

namespace WordCatcher.App.Windows;

public sealed class SelectionPillWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;
    private const double BadgeSizeDip = 32;
    private const double ShadowMarginDip = 12;
    private static readonly TimeSpan DefaultAutoHideDelay = TimeSpan.FromSeconds(4);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly Border _pillBorder;
    private readonly ScaleTransform _scaleTransform = new(1, 1);
    private readonly TranslateTransform _translateTransform = new(0, 0);
    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly ContextMenu _contextMenu;
    private CaptureResult? _pendingCapture;
    private bool _hoverToShowMini = false;
    private volatile bool _isPillShown;
    private volatile bool _isContextMenuOpen;
    private int _leftPx;
    private int _topPx;
    private int _rightPx;
    private int _bottomPx;

    public event Action<CaptureResult>? PillClicked;
    public event Action<CaptureResult>? PillHovered;
    public event Action<CaptureResult>? QuickSaveRequested;
    public event Action<CaptureResult>? CopyTranslationRequested;
    public event Action? PillVisibilityChanged;

    public bool IsPillVisible => _isPillShown;
    public bool IsContextMenuOpen => _isContextMenuOpen;

    public bool ContainsPhysicalPoint(ScreenPoint pt) =>
        _isPillShown && pt.X >= Volatile.Read(ref _leftPx) && pt.X <= Volatile.Read(ref _rightPx)
        && pt.Y >= Volatile.Read(ref _topPx) && pt.Y <= Volatile.Read(ref _bottomPx);

    public SelectionPillWindow()
    {
        Resources.MergedDictionaries.Add(new StandaloneDesignSystemDictionary());
        WpfUiResourceScope.PreferApplicationResources(this);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = BadgeSizeDip + ShadowMarginDip * 2;
        Height = BadgeSizeDip + ShadowMarginDip * 2;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Hand;

        _pillBorder = new Border
        {
            Width = BadgeSizeDip,
            Height = BadgeSizeDip,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            ToolTip = "点击翻译 · 右键快捷操作",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup
            {
                Children = { _scaleTransform, _translateTransform }
            },
            Child = BuildBrandEmblem(),
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                Opacity = 0.14,
                ShadowDepth = 3,
                Direction = 270,
                Color = Colors.Black
            }
        };
        _pillBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pillBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

        Content = new Grid
        {
            Margin = new Thickness(ShadowMarginDip),
            Children = { _pillBorder }
        };

        _autoHideTimer = new DispatcherTimer { Interval = DefaultAutoHideDelay };
        _autoHideTimer.Tick += (_, _) => HidePill();
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (!_isPillShown || _contextMenu?.IsOpen == true) return;
            var capture = _pendingCapture;
            HidePill();
            if (capture != null) PillHovered?.Invoke(capture);
        };

        _contextMenu = BuildStyledContextMenu();
        var quickSaveItem = CreateStyledMenuItem("直接收藏", () =>
        {
            var capture = _pendingCapture;
            HidePill();
            if (capture != null) QuickSaveRequested?.Invoke(capture);
        });
        var copyTranslationItem = CreateStyledMenuItem("复制译文", () =>
        {
            var capture = _pendingCapture;
            HidePill();
            if (capture != null) CopyTranslationRequested?.Invoke(capture);
        });
        _contextMenu.Items.Add(quickSaveItem);
        _contextMenu.Items.Add(copyTranslationItem);
        _contextMenu.Opened += (_, _) =>
        {
            _isContextMenuOpen = true;
            _hoverTimer.Stop();
            _autoHideTimer.Stop();
        };
        _contextMenu.Closed += (_, _) =>
        {
            _isContextMenuOpen = false;
            if (_isPillShown) _autoHideTimer.Start();
        };
        ContextMenu = _contextMenu;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle | WsExToolWindow | WsExNoActivate));
            }
        };

        MouseEnter += (_, _) =>
        {
            _autoHideTimer.Stop();
            _pillBorder.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
            _pillBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            AnimateScale(1.06, 110);
            if (_hoverToShowMini && _isPillShown && !_contextMenu.IsOpen)
                _hoverTimer.Start();
        };
        MouseLeave += (_, _) =>
        {
            _hoverTimer.Stop();
            _pillBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            _pillBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            AnimateScale(1.0, 110);
            if (IsVisible && !_contextMenu.IsOpen) _autoHideTimer.Start();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            AnimateScale(0.95, 60);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _hoverTimer.Stop();
            var capture = _pendingCapture;
            HidePill();
            if (capture != null) PillClicked?.Invoke(capture);
        };
        MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            _hoverTimer.Stop();
            _autoHideTimer.Stop();
            _contextMenu.PlacementTarget = _pillBorder;
            _contextMenu.IsOpen = true;
        };
    }

    public void ShowForCapture(CaptureResult capture, bool hoverToShowMini = false)
    {
        _pendingCapture = capture;
        _hoverToShowMini = hoverToShowMini;
        _autoHideTimer.Stop();
        _hoverTimer.Stop();
        _autoHideTimer.Interval = DefaultAutoHideDelay;

        ClearAnimations();
        _pillBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pillBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        _translateTransform.Y = 3.0;

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var anchor = capture.CursorPosition;
        var monitor = MonitorFromPoint(new NativeMethods.POINT { X = anchor.X, Y = anchor.Y }, MonitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var scale = 1.0;
            try
            {
                if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                    scale = dpiX / 96.0;
            }
            catch { }

            var shadowPx = (int)Math.Round(ShadowMarginDip * scale);
            var badgePx = Math.Max(26, (int)Math.Round(BadgeSizeDip * scale));
            var windowPx = badgePx + shadowPx * 2;
            var offsetPx = (int)Math.Round(10 * scale);
            var badgeLeft = anchor.X + offsetPx;
            var badgeTop = anchor.Y + offsetPx;
            if (badgeLeft + badgePx > info.WorkArea.Right)
                badgeLeft = Math.Max(info.WorkArea.Left, anchor.X - badgePx - offsetPx);
            if (badgeTop + badgePx > info.WorkArea.Bottom)
                badgeTop = Math.Max(info.WorkArea.Top, anchor.Y - badgePx - offsetPx);

            SetWindowPos(hwnd, IntPtr.Zero, badgeLeft - shadowPx, badgeTop - shadowPx, windowPx, windowPx,
                SwpNoZOrder | SwpNoActivate);
            Volatile.Write(ref _leftPx, badgeLeft);
            Volatile.Write(ref _topPx, badgeTop);
            Volatile.Write(ref _rightPx, badgeLeft + badgePx);
            Volatile.Write(ref _bottomPx, badgeTop + badgePx);
        }

        _isPillShown = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)) { EasingFunction = ease });
        _translateTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(3, 0, TimeSpan.FromMilliseconds(110)) { EasingFunction = ease });

        _autoHideTimer.Start();
        PillVisibilityChanged?.Invoke();
    }

    public void HidePill()
    {
        var wasVisible = _isPillShown;
        _isPillShown = false;
        _isContextMenuOpen = false;
        _autoHideTimer.Stop();
        _hoverTimer.Stop();
        ClearAnimations();
        _pillBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pillBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        _translateTransform.Y = 0.0;
        if (_contextMenu.IsOpen) _contextMenu.IsOpen = false;
        _pendingCapture = null;
        if (IsVisible) Hide();
        if (wasVisible) PillVisibilityChanged?.Invoke();
    }

    private void ClearAnimations()
    {
        BeginAnimation(OpacityProperty, null);
        _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void AnimateScale(double targetScale, int durationMs)
    {
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, duration) { EasingFunction = ease });
        _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, duration) { EasingFunction = ease });
    }

    private static Viewbox BuildBrandEmblem()
    {
        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            IsHitTestVisible = false
        };

        var amberBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x9A, 0x2B));
        amberBrush.Freeze();

        canvas.Children.Add(new PathShape
        {
            Data = Geometry.Parse("M 3.5,8.5 V 5.2 Q 3.5,3.5 5.2,3.5 H 8.5"),
            Stroke = amberBrush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });

        canvas.Children.Add(new PathShape
        {
            Data = Geometry.Parse("M 20.5,15.5 V 18.8 Q 20.5,20.5 18.8,20.5 H 15.5"),
            Stroke = amberBrush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });

        var wPath = new PathShape
        {
            Data = Geometry.Parse("M 6.4,8.6 L 9.1,16.1 L 12,10.2 L 14.9,16.1 L 17.6,8.6"),
            StrokeThickness = 2.15,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        wPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(wPath);

        return new Viewbox
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas
        };
    }

    private static ContextMenu BuildStyledContextMenu()
    {
        var template = new ControlTemplate(typeof(ContextMenu));
        var card = new FrameworkElementFactory(typeof(Border));
        card.SetValue(Border.MarginProperty, new Thickness(8));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.PaddingProperty, new Thickness(4));
        card.SetValue(Border.SnapsToDevicePixelsProperty, true);
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
        card.SetValue(UIElement.EffectProperty, new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 14,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = 0.16
        });

        var itemsHost = new FrameworkElementFactory(typeof(StackPanel));
        itemsHost.SetValue(Panel.IsItemsHostProperty, true);
        card.AppendChild(itemsHost);
        template.VisualTree = card;

        return new ContextMenu
        {
            OverridesDefaultStyle = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HasDropShadow = false,
            Template = template
        };
    }

    private static MenuItem CreateStyledMenuItem(string header, Action onClick)
    {
        var template = new ControlTemplate(typeof(MenuItem));
        var root = new FrameworkElementFactory(typeof(Border), "PART_Root");
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        root.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));
        root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        root.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);
        template.VisualTree = root;

        var highlightTrigger = new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true
        };
        highlightTrigger.Setters.Add(new Setter
        {
            TargetName = "PART_Root",
            Property = Border.BackgroundProperty,
            Value = new DynamicResourceExtension("HoverSurfaceBrush")
        });
        template.Triggers.Add(highlightTrigger);

        var item = new MenuItem
        {
            Header = header,
            OverridesDefaultStyle = true,
            FontSize = 12.5,
            Cursor = Cursors.Hand,
            Template = template
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        item.Click += (_, _) => onClick();
        return item;
    }
}

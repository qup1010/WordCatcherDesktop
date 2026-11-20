using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WordCatcher.App.Interop;
using WordCatcher.App.Services;
using WordCatcher.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace WordCatcher.App.Windows;

public sealed class SelectionPillWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

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
    private readonly TextBlock _glyph;
    private readonly DispatcherTimer _autoHideTimer;
    private CaptureResult? _pendingCapture;
    private volatile bool _isPillShown;
    private int _leftPx;
    private int _topPx;
    private int _rightPx;
    private int _bottomPx;

    public event Action<CaptureResult>? PillClicked;

    public bool IsPillVisible => _isPillShown;

    public bool ContainsPhysicalPoint(ScreenPoint pt) =>
        _isPillShown && pt.X >= Volatile.Read(ref _leftPx) && pt.X <= Volatile.Read(ref _rightPx)
        && pt.Y >= Volatile.Read(ref _topPx) && pt.Y <= Volatile.Read(ref _bottomPx);

    public SelectionPillWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 38;
        Height = 38;
        Cursor = Cursors.Hand;

        _glyph = new TextBlock
        {
            Text = "W",
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x6E)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        _pillBorder = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xE0, 0xDD)),
            BorderThickness = new Thickness(1),
            Child = _glyph,
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                Opacity = 0.16,
                ShadowDepth = 2,
                Color = Color.FromRgb(0x1E, 0x29, 0x3B)
            }
        };
        _pillBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _pillBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        Content = new Grid { Margin = new Thickness(4), Children = { _pillBorder } };

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _autoHideTimer.Tick += (_, _) => HidePill();

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
        };
        MouseLeave += (_, _) =>
        {
            _pillBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            if (IsVisible) _autoHideTimer.Start();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var capture = _pendingCapture;
            HidePill();
            if (capture != null) PillClicked?.Invoke(capture);
        };
    }

    public void ShowForCapture(CaptureResult capture)
    {
        _pendingCapture = capture;
        _autoHideTimer.Stop();

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

            var sizePx = Math.Max(28, (int)Math.Round(38 * scale));
            var offsetPx = (int)Math.Round(12 * scale);
            var left = anchor.X + offsetPx;
            var top = anchor.Y + offsetPx;
            if (left + sizePx > info.WorkArea.Right)
                left = Math.Max(info.WorkArea.Left, anchor.X - sizePx - offsetPx);
            if (top + sizePx > info.WorkArea.Bottom)
                top = Math.Max(info.WorkArea.Top, anchor.Y - sizePx - offsetPx);

            SetWindowPos(hwnd, IntPtr.Zero, left, top, sizePx, sizePx, SwpNoZOrder | SwpNoActivate);
            Volatile.Write(ref _leftPx, left);
            Volatile.Write(ref _topPx, top);
            Volatile.Write(ref _rightPx, left + sizePx);
            Volatile.Write(ref _bottomPx, top + sizePx);
        }

        _isPillShown = true;
        Opacity = 1;
        _autoHideTimer.Start();
    }

    public void HidePill()
    {
        _isPillShown = false;
        _autoHideTimer.Stop();
        _pendingCapture = null;
        if (IsVisible) Hide();
    }
}

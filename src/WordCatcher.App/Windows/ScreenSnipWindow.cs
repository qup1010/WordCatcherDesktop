using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WordCatcher.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace WordCatcher.App.Windows;

public sealed record ScreenSnipResult(byte[] PngBytes, ScreenPoint CursorPosition);

public sealed class ScreenSnipWindow : Window
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private readonly Bitmap _screenBitmap;
    private readonly System.Drawing.Rectangle _virtualBounds;
    private readonly Canvas _overlayCanvas;
    private readonly System.Windows.Shapes.Path _dimPath;
    private readonly Rectangle _selectionRect;
    private readonly Border _hintPill;
    private readonly TextBlock _hintText;
    private readonly TaskCompletionSource<ScreenSnipResult?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _isDragging;
    private Point _startDip;
    private NativePoint _startPhysical;
    private Rect _currentRect;
    private bool _completed;

    public static Task<ScreenSnipResult?> CaptureRegionAsync()
    {
        var virtualBounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(virtualBounds.Width, virtualBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(virtualBounds.Left, virtualBounds.Top, 0, 0, virtualBounds.Size, CopyPixelOperation.SourceCopy);
            }

            var window = new ScreenSnipWindow(bitmap, virtualBounds);
            bitmap = null; // Ownership transferred to ScreenSnipWindow.
            window.Show();
            window.Activate();
            window.Focus();
            return window._tcs.Task;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
    }

    private ScreenSnipWindow(Bitmap screenBitmap, System.Drawing.Rectangle virtualBounds)
    {
        _screenBitmap = screenBitmap;
        _virtualBounds = virtualBounds;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var root = new Grid();
        var bgImage = new Image
        {
            Source = ToBitmapSource(_screenBitmap),
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true
        };
        root.Children.Add(bgImage);

        _overlayCanvas = new Canvas { Background = Brushes.Transparent };
        _dimPath = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            IsHitTestVisible = false
        };
        _overlayCanvas.Children.Add(_dimPath);

        _selectionRect = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x6E)),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _overlayCanvas.Children.Add(_selectionRect);

        _hintText = new TextBlock
        {
            Text = "拖动框选要识别的文字 · Esc 或右键取消",
            Foreground = Brushes.White,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium
        };
        _hintPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(224, 22, 24, 28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6),
            Child = _hintText,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_hintPill, 20);
        Canvas.SetTop(_hintPill, 20);
        _overlayCanvas.Children.Add(_hintPill);

        root.Children.Add(_overlayCanvas);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, IntPtr.Zero, _virtualBounds.Left, _virtualBounds.Top,
                    _virtualBounds.Width, _virtualBounds.Height, SwpNoZOrder | SwpShowWindow);
            }
        };
        Loaded += (_, _) =>
        {
            UpdateDimGeometry(Rect.Empty);
            PositionInitialHintNearCursor();
        };
        PreviewKeyDown += OnWindowKeyDown;
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseMove += OnWindowMouseMove;
        MouseLeftButtonUp += OnMouseLeftUp;
        LostMouseCapture += (_, _) =>
        {
            if (_isDragging && !_completed)
            {
                _isDragging = false;
                Complete(null);
            }
        };
        MouseRightButtonDown += (_, _) => Complete(null);
        Closed += OnWindowClosed;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Complete(null);
        }
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!CaptureMouse())
        {
            Complete(null);
            return;
        }
        _isDragging = true;
        _startDip = e.GetPosition(_overlayCanvas);
        GetCursorPos(out _startPhysical);
        _currentRect = new Rect(_startDip, new System.Windows.Size(0, 0));
        _selectionRect.Visibility = Visibility.Visible;
        UpdateVisualSelection();
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(_overlayCanvas);
        var x = Math.Min(_startDip.X, pos.X);
        var y = Math.Min(_startDip.Y, pos.Y);
        var w = Math.Abs(pos.X - _startDip.X);
        var h = Math.Abs(pos.Y - _startDip.Y);
        _currentRect = new Rect(x, y, w, h);
        UpdateVisualSelection();
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();

        try
        {
            GetCursorPos(out var endPhysical);
            var minX = Math.Min(_startPhysical.X, endPhysical.X) - _virtualBounds.Left;
            var minY = Math.Min(_startPhysical.Y, endPhysical.Y) - _virtualBounds.Top;
            var widthPx = Math.Abs(endPhysical.X - _startPhysical.X);
            var heightPx = Math.Abs(endPhysical.Y - _startPhysical.Y);

            if (widthPx < 6 || heightPx < 6)
            {
                Complete(null);
                return;
            }

            var cropX = Math.Clamp(minX, 0, _screenBitmap.Width - 1);
            var cropY = Math.Clamp(minY, 0, _screenBitmap.Height - 1);
            var cropW = Math.Clamp(widthPx, 1, _screenBitmap.Width - cropX);
            var cropH = Math.Clamp(heightPx, 1, _screenBitmap.Height - cropY);

            var cropRect = new System.Drawing.Rectangle(cropX, cropY, cropW, cropH);
            using var cropped = _screenBitmap.Clone(cropRect, _screenBitmap.PixelFormat);
            using var ms = new MemoryStream();
            cropped.Save(ms, ImageFormat.Png);

            Complete(new ScreenSnipResult(ms.ToArray(), new ScreenPoint(endPhysical.X, endPhysical.Y)));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void UpdateVisualSelection()
    {
        Canvas.SetLeft(_selectionRect, _currentRect.X);
        Canvas.SetTop(_selectionRect, _currentRect.Y);
        _selectionRect.Width = _currentRect.Width;
        _selectionRect.Height = _currentRect.Height;
        UpdateDimGeometry(_currentRect);

        GetCursorPos(out var curPhysical);
        var pxW = Math.Abs(curPhysical.X - _startPhysical.X);
        var pxH = Math.Abs(curPhysical.Y - _startPhysical.Y);
        _hintText.Text = $"{pxW} × {pxH}";
        Canvas.SetLeft(_hintPill, Math.Clamp(_currentRect.X, 12, Math.Max(12, ActualWidth - 140)));
        var pillTop = _currentRect.Y >= 38 ? _currentRect.Y - 34 : _currentRect.Bottom + 8;
        Canvas.SetTop(_hintPill, Math.Clamp(pillTop, 12, Math.Max(12, ActualHeight - 40)));
    }

    private void PositionInitialHintNearCursor()
    {
        if (!GetCursorPos(out var cursor))
            return;

        var workArea = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea;
        var topLeft = _overlayCanvas.PointFromScreen(new Point(workArea.Left, workArea.Top));
        var bottomRight = _overlayCanvas.PointFromScreen(new Point(workArea.Right, workArea.Bottom));
        var cursorDip = _overlayCanvas.PointFromScreen(new Point(cursor.X, cursor.Y));

        var pillW = _hintPill.ActualWidth > 0 ? _hintPill.ActualWidth : 240;
        var pillH = _hintPill.ActualHeight > 0 ? _hintPill.ActualHeight : 34;
        var minX = Math.Min(topLeft.X, bottomRight.X) + 12;
        var maxX = Math.Max(minX, Math.Max(topLeft.X, bottomRight.X) - pillW - 12);
        var minY = Math.Min(topLeft.Y, bottomRight.Y) + 12;
        var maxY = Math.Max(minY, Math.Max(topLeft.Y, bottomRight.Y) - pillH - 12);

        var x = cursorDip.X + 16 <= maxX ? cursorDip.X + 16 : cursorDip.X - pillW - 16;
        var y = cursorDip.Y + 16 <= maxY ? cursorDip.Y + 16 : cursorDip.Y - pillH - 16;
        Canvas.SetLeft(_hintPill, Math.Clamp(x, minX, maxX));
        Canvas.SetTop(_hintPill, Math.Clamp(y, minY, maxY));
    }

    private void UpdateDimGeometry(Rect hole)
    {
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height));
        if (hole.IsEmpty || hole.Width < 1 || hole.Height < 1)
        {
            _dimPath.Data = full;
            return;
        }
        _dimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole));
    }

    private void Complete(ScreenSnipResult? result)
    {
        if (_completed) return;
        _completed = true;
        _tcs.TrySetResult(result);
        Close();
    }

    private void Fail(Exception ex)
    {
        if (_completed) return;
        _completed = true;
        _tcs.TrySetException(ex);
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (!_completed)
        {
            _completed = true;
            _tcs.TrySetResult(null);
        }
        _screenBitmap.Dispose();
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

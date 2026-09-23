using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Tasks;
using WordCatcher.App.Interop;
using WordCatcher.App.Themes;
using WordCatcher.App.ViewModels;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Windows;

public partial class LookupWindow : Window
{
    private readonly LookupViewModel _viewModel;

    public bool IsPinned => PinToggle.IsChecked == true;

    public LookupWindow(LookupViewModel viewModel)
    {
        InitializeComponent();
        WpfUiResourceScope.PreferApplicationResources(this);
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.RequestClose += OnRequestClose;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LookupViewModel.HasResult) && _viewModel.HasResult)
            ResultScroll.ScrollToTop();
    }

    private void OnRequestClose()
    {
        Dispatcher.Invoke(Close);
    }

    public Task StartLookupAsync(CaptureResult capture)
    {
        return _viewModel.StartLookupAsync(capture);
    }

    public void PositionNearCursor(ScreenPoint physicalCursor)
    {
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var dpi = NativeMethods.GetDpiForWindow(handle);
            if (dpi > 0)
            {
                dpiX = dpi / 96.0;
                dpiY = dpi / 96.0;
            }
        }

        double dipX = physicalCursor.X / dpiX;
        double dipY = physicalCursor.Y / dpiY;

        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(physicalCursor.X, physicalCursor.Y));
        var physicalWorkArea = screen.WorkingArea;
        var workArea = new Rect(
            physicalWorkArea.Left / dpiX,
            physicalWorkArea.Top / dpiY,
            physicalWorkArea.Width / dpiX,
            physicalWorkArea.Height / dpiY);

        // Keep the popup usable on small displays and guarantee the intended
        // card width even when SizeToContent measures a very long dictionary entry.
        Width = 420;
        MinWidth = 420;
        MaxWidth = 420;
        MaxHeight = Math.Max(360, workArea.Height - 24);
        UpdateLayout();

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Math.Min(DesiredSize.Height, MaxHeight);

        double left = dipX + 16;
        double top = dipY + 16;

        // Flip to left if overflowing right edge
        if (left + width > workArea.Right)
        {
            left = dipX - width - 8;
        }

        // Flip up if overflowing bottom edge
        if (top + height > workArea.Bottom)
        {
            top = dipY - height - 8;
        }

        // Clamp inside bounds
        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - width));
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - height));

        Left = left;
        Top = top;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
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
        else if (e.Key == Key.Enter)
        {
            if (_viewModel.SaveCommand.CanExecute(null))
            {
                _viewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
        PinToggle.ToolTip = "已钉住；窗口保持当前位置，后续取词在此刷新 (Ctrl+P)";
    }

    private void PinToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        _viewModel.KeepOpenAfterSave = false;
        PinToggle.ToolTip = "钉住卡片；保持位置并在这里刷新后续取词 (Ctrl+P)";
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CancelLookup();
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        base.OnClosed(e);
    }
}

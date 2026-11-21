using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WordCatcher.App.Controls;
using WordCatcher.App.Themes;
using Wpf.Ui.Controls;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using PathShape = System.Windows.Shapes.Path;
using TextBlock = System.Windows.Controls.TextBlock;

namespace WordCatcher.App.Tray;

public sealed record TrayMenuSnapshot(
    bool MouseSelectionEnabled,
    bool HotkeysEnabled,
    string HotkeyText,
    string OcrHotkeyText);

public sealed record TrayMenuActions(
    Action OpenLibrary,
    Action OpenLookup,
    Action TriggerOcrCapture,
    Action ToggleMouseSelection,
    Action ToggleHotkeys,
    Action OpenSettings,
    Action ExitApp);

public sealed class TrayContextMenuWindow : Window
{
    private const double ShadowMarginDip = 12;
    private const double CardWidthDip = 264;
    private const int MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref NativeMonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly TrayMenuSnapshot _snapshot;
    private readonly TrayMenuActions _actions;
    private readonly List<string> _menuItemLabels = [];
    private readonly Dictionary<string, Button> _buttonsByTag = new(StringComparer.Ordinal);
    private readonly Border _cardBorder;
    private bool _isClosing;

    internal IReadOnlyList<string> MenuItemLabelsForTest => _menuItemLabels;
    internal Button? GetButtonByTagForTest(string tag) => _buttonsByTag.GetValueOrDefault(tag);

    public TrayContextMenuWindow(TrayMenuSnapshot snapshot, TrayMenuActions actions)
    {
        _snapshot = snapshot;
        _actions = actions;

        Resources.MergedDictionaries.Add(new StandaloneDesignSystemDictionary());
        WpfUiResourceScope.PreferApplicationResources(this);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowActivated = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var itemsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6)
        };

        BuildMenuItems(itemsPanel);

        _cardBorder = new Border
        {
            Width = CardWidthDip,
            Margin = new Thickness(ShadowMarginDip),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = itemsPanel,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.16
            }
        };
        _cardBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        _cardBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");

        Content = _cardBorder;

        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowAtCursor()
    {
        if (GetCursorPos(out var cursor))
            ShowAtPhysicalPoint(cursor.X, cursor.Y);
        else
            ShowAtPhysicalPoint(100, 100);
    }

    internal void ShowAtPhysicalPoint(int physicalX, int physicalY)
    {
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();

        var pt = new NativePoint { X = physicalX, Y = physicalY };
        var monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var info = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var scale = GetMonitorDpiScale(monitor);
            SetWindowPos(hwnd, IntPtr.Zero, info.WorkArea.Left, info.WorkArea.Top, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);

            _cardBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = _cardBorder.DesiredSize;
            var totalWidthPx = (int)Math.Ceiling((desired.Width + ShadowMarginDip * 2) * scale);
            var totalHeightPx = (int)Math.Ceiling((desired.Height + ShadowMarginDip * 2) * scale);
            var shadowPx = (int)Math.Round(ShadowMarginDip * scale);

            var (originX, originY) = ComputeMenuOrigin(
                physicalX,
                physicalY,
                totalWidthPx,
                totalHeightPx,
                shadowPx,
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right,
                info.WorkArea.Bottom);

            SetWindowPos(hwnd, IntPtr.Zero, originX, originY, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        Show();
        Activate();
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    internal static (int X, int Y) ComputeMenuOrigin(
        int cursorX,
        int cursorY,
        int widthPx,
        int heightPx,
        int shadowMarginPx,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom)
    {
        const int edgeGap = 6;
        var cardWidth = Math.Max(1, widthPx - shadowMarginPx * 2);
        var cardHeight = Math.Max(1, heightPx - shadowMarginPx * 2);

        var cardX = cursorX + cardWidth + edgeGap <= workRight
            ? cursorX - 12
            : cursorX - cardWidth + 12;

        var maxCardX = Math.Max(workLeft + edgeGap, workRight - cardWidth - edgeGap);
        cardX = Math.Clamp(cardX, workLeft + edgeGap, maxCardX);

        var openUpward = cursorY > (workTop + workBottom) / 2
            && cursorY - cardHeight - edgeGap >= workTop;

        var cardY = openUpward
            ? cursorY - cardHeight - 8
            : cursorY + 6;

        var maxCardY = Math.Max(workTop + edgeGap, workBottom - cardHeight - edgeGap);
        cardY = Math.Clamp(cardY, workTop + edgeGap, maxCardY);

        return (cardX - shadowMarginPx, cardY - shadowMarginPx);
    }

    public void SafeClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SafeClose();
        }
    }

    private void BuildMenuItems(StackPanel panel)
    {
        panel.Children.Add(CreateActionRow(
            tag: "open-library",
            label: "打开生词库",
            icon: CreateSymbolIcon(SymbolRegular.Book24, 15, isAccent: true),
            onClick: _actions.OpenLibrary,
            isPrimaryBold: true));

        panel.Children.Add(CreateActionRow(
            tag: "open-lookup",
            label: "手动查词",
            icon: CreateSymbolIcon(SymbolRegular.Search20, 14),
            onClick: _actions.OpenLookup));

        panel.Children.Add(CreateActionRow(
            tag: "ocr-capture",
            label: "截屏 OCR 取词",
            icon: CreateVectorIcon("M2,5 V3 H5 M11,3 H14 V5 M14,11 V13 H11 M5,13 H2 V11 M5,6 H11 V10 H5 Z", filled: false),
            hintText: _snapshot.OcrHotkeyText,
            onClick: _actions.TriggerOcrCapture));

        panel.Children.Add(CreateSeparator());

        panel.Children.Add(CreateCheckRow(
            tag: "toggle-mouse",
            label: "鼠标划词取词",
            isChecked: _snapshot.MouseSelectionEnabled,
            hintText: null,
            onClick: _actions.ToggleMouseSelection));

        panel.Children.Add(CreateCheckRow(
            tag: "toggle-hotkeys",
            label: "启用全局快捷键",
            isChecked: _snapshot.HotkeysEnabled,
            hintText: _snapshot.HotkeysEnabled ? _snapshot.HotkeyText : "已暂停",
            onClick: _actions.ToggleHotkeys));

        panel.Children.Add(CreateSeparator());

        panel.Children.Add(CreateActionRow(
            tag: "open-settings",
            label: "设置",
            icon: CreateSymbolIcon(SymbolRegular.Settings24, 15),
            onClick: _actions.OpenSettings));

        panel.Children.Add(CreateActionRow(
            tag: "exit",
            label: "退出",
            icon: CreateVectorIcon("M8,2 V7.5 M5.2,4.2 A5,5 0 1 0 10.8,4.2", filled: false, isDanger: true),
            onClick: _actions.ExitApp,
            isDanger: true));
    }

    private Button CreateActionRow(
        string tag,
        string label,
        UIElement icon,
        Action onClick,
        string? hintText = null,
        bool isPrimaryBold = false,
        bool isDanger = false)
    {
        _menuItemLabels.Add(label);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconHost = new ContentPresenter
        {
            Content = icon,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var title = new TextBlock
        {
            Text = label,
            FontSize = 13,
            FontWeight = isPrimaryBold ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, isDanger ? "DangerBrush" : "TextPrimaryBrush");
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(hintText))
        {
            var hint = new TextBlock
            {
                Text = hintText,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(hint, 2);
            grid.Children.Add(hint);
        }

        var button = new Button
        {
            Tag = tag,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = null,
            Content = grid,
            Template = BuildRoundedButtonTemplate(
                cornerRadius: 6,
                normalBgKey: null,
                hoverBgKey: isDanger ? "DangerHoverSurfaceBrush" : "HoverSurfaceBrush")
        };

        button.Click += (_, _) =>
        {
            SafeClose();
            onClick();
        };
        _buttonsByTag[tag] = button;
        return button;
    }

    private Button CreateCheckRow(string tag, string label, bool isChecked, string? hintText, Action onClick)
    {
        var icon = CreateVectorIcon(
            "M3,8.2 L6.5,11.8 L13,4.2",
            filled: false,
            isAccent: true,
            strokeThickness: 1.9);
        icon.Visibility = isChecked ? Visibility.Visible : Visibility.Hidden;
        return CreateActionRow(tag, label, icon, onClick, hintText);
    }

    private static UIElement CreateSymbolIcon(SymbolRegular symbol, double fontSize, bool isAccent = false)
    {
        var icon = new CrispSymbolIcon
        {
            Symbol = symbol,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(Control.ForegroundProperty, isAccent ? "AccentBrush" : "TextSecondaryBrush");
        return icon;
    }

    private static UIElement CreateVectorIcon(
        string pathData,
        bool filled,
        bool isAccent = false,
        bool isDanger = false,
        double strokeThickness = 1.5)
    {
        var path = new PathShape
        {
            Data = Geometry.Parse(pathData),
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            StrokeThickness = filled ? 0 : strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var brushKey = isDanger
            ? "DangerBrush"
            : isAccent
                ? "AccentBrush"
                : "TextSecondaryBrush";

        if (filled)
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        else
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);

        return path;
    }

    private static Border CreateSeparator()
    {
        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 5, 8, 5),
            SnapsToDevicePixels = true
        };
        sep.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        return sep;
    }

    private static ControlTemplate BuildRoundedButtonTemplate(
        double cornerRadius,
        string? normalBgKey,
        string hoverBgKey,
        string? normalBorderKey = null)
    {
        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Border), "PART_Root");
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));
        root.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        root.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

        if (!string.IsNullOrEmpty(normalBgKey))
            root.SetResourceReference(Border.BackgroundProperty, normalBgKey);
        else
            root.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        if (!string.IsNullOrEmpty(normalBorderKey))
            root.SetResourceReference(Border.BorderBrushProperty, normalBorderKey);
        else
            root.SetValue(Border.BorderBrushProperty, Brushes.Transparent);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        root.AppendChild(presenter);

        template.VisualTree = root;

        var hoverTrigger = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        var hoverBrushSetter = new Setter { TargetName = "PART_Root", Property = Border.BackgroundProperty };
        hoverBrushSetter.Value = new DynamicResourceExtension(hoverBgKey);
        hoverTrigger.Setters.Add(hoverBrushSetter);
        template.Triggers.Add(hoverTrigger);

        var focusTrigger = new Trigger
        {
            Property = UIElement.IsKeyboardFocusedProperty,
            Value = true
        };
        var focusBrushSetter = new Setter { TargetName = "PART_Root", Property = Border.BackgroundProperty };
        focusBrushSetter.Value = new DynamicResourceExtension(hoverBgKey);
        focusTrigger.Setters.Add(focusBrushSetter);
        template.Triggers.Add(focusTrigger);

        var pressedTrigger = new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressedTrigger.Setters.Add(new Setter
        {
            TargetName = "PART_Root",
            Property = UIElement.OpacityProperty,
            Value = 0.85
        });
        template.Triggers.Add(pressedTrigger);

        return template;
    }

    private static double GetMonitorDpiScale(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return 1.0;
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX >= 96)
                return dpiX / 96.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return 1.0;
    }
}

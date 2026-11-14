using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using ImageControl = System.Windows.Controls.Image;
using PathShape = System.Windows.Shapes.Path;

namespace WordCatcher.App.Controls;

/// <summary>
/// Renders Fluent System Icons using integer-grid-aligned vector outlines and 8x8 linear-light
/// supersampled anti-aliasing at exact physical device pixels, eliminating TrueType hinting steps
/// and WPF sRGB gamma-blending staircase artifacts on curved and diagonal strokes.
/// </summary>
public sealed class CrispSymbolIcon : IconElement
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol),
        typeof(SymbolRegular),
        typeof(CrispSymbolIcon),
        new FrameworkPropertyMetadata(
            SymbolRegular.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnIconPropertyChanged));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled),
        typeof(bool),
        typeof(CrispSymbolIcon),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnIconPropertyChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(CrispSymbolIcon),
        new FrameworkPropertyMetadata(
            16.0,
            FrameworkPropertyMetadataOptions.Inherits
            | FrameworkPropertyMetadataOptions.AffectsMeasure
            | FrameworkPropertyMetadataOptions.AffectsRender,
            OnIconPropertyChanged));

    private Grid? _host;
    private PathShape? _path;
    private ImageControl? _image;

    public CrispSymbolIcon()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Loaded += (_, _) => UpdateGeometry();
    }

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>
    /// Exposes the underlying rendered geometry for layout and verification tests.
    /// </summary>
    internal Geometry? RenderedGeometry => _path?.Data;

    protected override UIElement InitializeChildren()
    {
        _host = new Grid
        {
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        _path = new PathShape
        {
            Stretch = Stretch.None,
            SnapsToDevicePixels = false,
            UseLayoutRounding = false,
            IsHitTestVisible = false,
            Opacity = 0.0,
            Fill = Foreground
        };

        _image = new ImageControl
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);

        _host.Children.Add(_path);
        _host.Children.Add(_image);
        UpdateGeometry();
        return _host;
    }

    protected override void OnForegroundChanged(DependencyPropertyChangedEventArgs args)
    {
        base.OnForegroundChanged(args);
        UpdateGeometry();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty || e.Property == TextElement.ForegroundProperty)
            UpdateGeometry();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateGeometry();
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CrispSymbolIcon icon)
            icon.UpdateGeometry();
    }

    private void UpdateGeometry()
    {
        if (_host is null || _path is null || _image is null)
            return;

        var boxSize = FontSize;
        if (double.IsNaN(boxSize) || boxSize <= 0)
            boxSize = 16.0;

        _host.Width = boxSize;
        _host.Height = boxSize;

        var geometry = FluentGlyphOutlineCache.GetCenteredGeometry(Symbol, Filled, boxSize);
        _path.Fill = Foreground;
        _path.Data = geometry;

        var dpiScale = 1.0;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0 && !double.IsNaN(dpi.DpiScaleX))
                dpiScale = dpi.DpiScaleX;
        }
        catch
        {
            dpiScale = 1.0;
        }

        var color = ResolveForegroundColor(Foreground);
        _image.Width = boxSize;
        _image.Height = boxSize;
        _image.Source = geometry is null
            ? null
            : FluentGlyphOutlineCache.GetCrispBitmap(Symbol, Filled, boxSize, dpiScale, color, geometry);
    }

    private static System.Windows.Media.Color ResolveForegroundColor(System.Windows.Media.Brush? brush)
    {
        if (brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            var opacity = Math.Clamp(solid.Opacity, 0.0, 1.0);
            return System.Windows.Media.Color.FromArgb(
                (byte)Math.Round(c.A * opacity),
                c.R,
                c.G,
                c.B);
        }

        return System.Windows.Media.Color.FromRgb(24, 24, 27);
    }
}

/// <summary>
/// Upgrades internal <see cref="SymbolIcon"/> elements inside third-party control templates
/// (such as <see cref="CardExpander"/> and <see cref="CardAction"/> chevrons) to <see cref="CrispSymbolIcon"/>
/// and stabilizes expander chevron rotation angles.
/// </summary>
public static class CrispIconHost
{
    public static readonly DependencyProperty UpgradeTemplateIconsProperty = DependencyProperty.RegisterAttached(
        "UpgradeTemplateIcons",
        typeof(bool),
        typeof(CrispIconHost),
        new PropertyMetadata(false, OnUpgradeTemplateIconsChanged));

    public static bool GetUpgradeTemplateIcons(DependencyObject obj)
        => (bool)obj.GetValue(UpgradeTemplateIconsProperty);

    public static void SetUpgradeTemplateIcons(DependencyObject obj, bool value)
        => obj.SetValue(UpgradeTemplateIconsProperty, value);

    private static void OnUpgradeTemplateIconsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
            return;

        element.Loaded += OnElementReady;
        element.SizeChanged += OnElementReady;
        if (element.IsLoaded || element.ActualWidth > 0)
            UpgradeElement(element);
    }

    private static void OnElementReady(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            UpgradeElement(element);
    }

    private static void UpgradeElement(FrameworkElement root)
    {
        ReplaceDescendantSymbolIcons(root);

        if (root is CardExpander expander)
        {
            var chevronGrid = FindNamedVisualDescendant<FrameworkElement>(expander, "ChevronGrid");
            if (chevronGrid is not null)
            {
                chevronGrid.RenderTransformOrigin = new Point(0.5, 0.5);
                chevronGrid.RenderTransform = new RotateTransform(expander.IsExpanded ? 180.0 : 0.0);

                var descriptor = DependencyPropertyDescriptor.FromProperty(Expander.IsExpandedProperty, typeof(CardExpander));
                descriptor?.RemoveValueChanged(expander, OnCardExpanderIsExpandedChanged);
                descriptor?.AddValueChanged(expander, OnCardExpanderIsExpandedChanged);
            }
        }
    }

    private static void OnCardExpanderIsExpandedChanged(object? sender, EventArgs e)
    {
        if (sender is not CardExpander expander)
            return;

        var chevronGrid = FindNamedVisualDescendant<FrameworkElement>(expander, "ChevronGrid");
        if (chevronGrid is not null)
        {
            chevronGrid.RenderTransformOrigin = new Point(0.5, 0.5);
            chevronGrid.RenderTransform = new RotateTransform(expander.IsExpanded ? 180.0 : 0.0);
        }
    }

    private static void ReplaceDescendantSymbolIcons(DependencyObject current)
    {
        var count = VisualTreeHelper.GetChildrenCount(current);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is SymbolIcon symbolIcon && current is Grid grid)
            {
                var alreadyUpgraded = false;
                foreach (UIElement existing in grid.Children)
                {
                    if (existing is CrispSymbolIcon)
                    {
                        alreadyUpgraded = true;
                        break;
                    }
                }

                if (!alreadyUpgraded)
                {
                    var fontSize = symbolIcon.FontSize > 0 ? symbolIcon.FontSize : 16.0;
                    var symbol = symbolIcon.Symbol;
                    if (symbol == SymbolRegular.ChevronDown24 && fontSize <= 18.0)
                        symbol = SymbolRegular.ChevronDown16;

                    var crisp = new CrispSymbolIcon
                    {
                        Symbol = symbol,
                        Filled = symbolIcon.Filled,
                        FontSize = fontSize,
                        Margin = symbolIcon.Margin,
                        HorizontalAlignment = symbolIcon.HorizontalAlignment,
                        VerticalAlignment = symbolIcon.VerticalAlignment,
                        Opacity = symbolIcon.Opacity,
                        IsHitTestVisible = false
                    };
                    Grid.SetRow(crisp, Grid.GetRow(symbolIcon));
                    Grid.SetColumn(crisp, Grid.GetColumn(symbolIcon));
                    Grid.SetRowSpan(crisp, Grid.GetRowSpan(symbolIcon));
                    Grid.SetColumnSpan(crisp, Grid.GetColumnSpan(symbolIcon));
                    BindingOperations.SetBinding(crisp, IconElement.ForegroundProperty, new System.Windows.Data.Binding
                    {
                        Path = new PropertyPath(TextElement.ForegroundProperty),
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Control), 1)
                    });
                    symbolIcon.Opacity = 0.0;
                    symbolIcon.IsHitTestVisible = false;
                    grid.Children.Add(crisp);
                }

                continue;
            }

            ReplaceDescendantSymbolIcons(child);
        }
    }

    private static T? FindNamedVisualDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && string.Equals(match.Name, name, StringComparison.Ordinal))
                return match;

            var nested = FindNamedVisualDescendant<T>(child, name);
            if (nested is not null)
                return nested;
        }

        return null;
    }
}

/// <summary>
/// XAML markup extension that produces a <see cref="CrispSymbolIcon"/> with optional size and fill weight.
/// </summary>
[MarkupExtensionReturnType(typeof(CrispSymbolIcon))]
public sealed class CrispIconExtension : MarkupExtension
{
    public CrispIconExtension()
    {
    }

    public CrispIconExtension(SymbolRegular symbol)
    {
        Symbol = symbol;
    }

    public SymbolRegular Symbol { get; set; }
    public bool Filled { get; set; }
    public double? FontSize { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var icon = new CrispSymbolIcon
        {
            Symbol = Symbol,
            Filled = Filled
        };

        if (FontSize.HasValue)
            icon.FontSize = FontSize.Value;

        return icon;
    }
}

internal static class FluentGlyphOutlineCache
{
    private const int SupersampleScale = 8;
    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<(uint Codepoint, bool Filled, double BoxSize), Geometry?> GeometryCache = new();
    private static readonly ConcurrentDictionary<(uint Codepoint, bool Filled, int PixelSize, uint ColorArgb), BitmapSource?> BitmapCache = new();
    private static readonly Lazy<GlyphTypeface?> RegularTypeface = new(() => LoadTypeface("resources/fonts/fluentsystemicons-regular.ttf"));
    private static readonly Lazy<GlyphTypeface?> FilledTypeface = new(() => LoadTypeface("resources/fonts/fluentsystemicons-filled.ttf"));

    public static Geometry? GetCenteredGeometry(SymbolRegular symbol, bool filled, double boxSize)
    {
        if (symbol == SymbolRegular.Empty)
            return null;

        var roundedBox = Math.Round(boxSize, 2);
        var key = ((uint)symbol, filled, roundedBox);
        if (GeometryCache.TryGetValue(key, out var cached))
            return cached;

        var geometry = BuildCenteredGeometry(symbol, filled, roundedBox);
        GeometryCache[key] = geometry;
        return geometry;
    }

    public static BitmapSource? GetCrispBitmap(
        SymbolRegular symbol,
        bool filled,
        double boxSize,
        double dpiScale,
        System.Windows.Media.Color fg,
        Geometry geometry)
    {
        if (symbol == SymbolRegular.Empty || fg.A == 0)
            return null;

        var pixelSize = Math.Max(1, (int)Math.Round(boxSize * dpiScale));
        var argb = ((uint)fg.A << 24) | ((uint)fg.R << 16) | ((uint)fg.G << 8) | fg.B;
        var key = ((uint)symbol, filled, pixelSize, argb);
        if (BitmapCache.TryGetValue(key, out var cached))
            return cached;

        var bitmap = BuildSupersampledBitmap(geometry, boxSize, pixelSize, dpiScale, fg);
        BitmapCache[key] = bitmap;
        return bitmap;
    }

    private static BitmapSource BuildSupersampledBitmap(
        Geometry geometry,
        double boxSize,
        int pixelSize,
        double dpiScale,
        System.Windows.Media.Color fg)
    {
        var hiSize = pixelSize * SupersampleScale;
        var scaleFactor = hiSize / boxSize;

        byte[] hiPixels;
        var stride = hiSize * 4;
        lock (SyncRoot)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(scaleFactor, scaleFactor));
                dc.DrawGeometry(System.Windows.Media.Brushes.Black, null, geometry);
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(hiSize, hiSize, 96.0, 96.0, PixelFormats.Pbgra32);
            rtb.Render(visual);
            hiPixels = new byte[hiSize * stride];
            rtb.CopyPixels(hiPixels, stride, 0);
        }

        // Thin 1.5px outline icons need mild coverage boost on both light and dark surfaces
        // so half-covered curve pixels do not drop below 50% opacity and break into beads.
        var lum = (0.2126 * fg.R + 0.7152 * fg.G + 0.0722 * fg.B) / 255.0;
        var isDarkForeground = lum < 0.5;
        const double darkOnLightPower = 0.85;
        const double lightOnDarkPower = 0.82;

        var outPixels = new byte[pixelSize * pixelSize * 4];
        var invArea = 1.0 / (SupersampleScale * SupersampleScale);

        for (var y = 0; y < pixelSize; y++)
        {
            for (var x = 0; x < pixelSize; x++)
            {
                var sumAlpha = 0.0;
                for (var sy = 0; sy < SupersampleScale; sy++)
                {
                    var rowOffset = ((y * SupersampleScale + sy) * hiSize + x * SupersampleScale) * 4;
                    for (var sx = 0; sx < SupersampleScale; sx++)
                    {
                        sumAlpha += hiPixels[rowOffset + sx * 4 + 3] / 255.0;
                    }
                }

                var coverage = sumAlpha * invArea;
                if (coverage <= 0.0)
                    continue;

                var correctedAlpha = isDarkForeground
                    ? Math.Pow(coverage, darkOnLightPower)
                    : Math.Pow(coverage, lightOnDarkPower);

                var alphaByte = (byte)Math.Clamp(Math.Round(correctedAlpha * fg.A), 0, 255);
                if (alphaByte == 0)
                    continue;

                var premult = alphaByte / 255.0;
                var outIndex = (y * pixelSize + x) * 4;
                outPixels[outIndex + 0] = (byte)Math.Round(fg.B * premult);
                outPixels[outIndex + 1] = (byte)Math.Round(fg.G * premult);
                outPixels[outIndex + 2] = (byte)Math.Round(fg.R * premult);
                outPixels[outIndex + 3] = alphaByte;
            }
        }

        var dpi = 96.0 * dpiScale;
        var result = BitmapSource.Create(
            pixelSize,
            pixelSize,
            dpi,
            dpi,
            PixelFormats.Pbgra32,
            null,
            outPixels,
            pixelSize * 4);
        result.Freeze();
        return result;
    }

    private static Geometry? BuildCenteredGeometry(SymbolRegular symbol, bool filled, double boxSize)
    {
        if (filled && TryBuildCrispFilledBadgeGeometry(symbol, boxSize, out var badgeGeometry))
            return badgeGeometry;

        GlyphTypeface? typeface = null;
        var codepoint = (int)symbol;

        if (filled)
        {
            typeface = FilledTypeface.Value;
            if (typeface is not null && Enum.TryParse<SymbolFilled>(symbol.ToString(), out var filledSymbol))
            {
                var filledCodepoint = (int)filledSymbol;
                if (typeface.CharacterToGlyphMap.ContainsKey(filledCodepoint))
                    codepoint = filledCodepoint;
                else
                    typeface = null;
            }
            else
            {
                typeface = null;
            }
        }

        typeface ??= RegularTypeface.Value;
        codepoint = typeface == RegularTypeface.Value ? (int)symbol : codepoint;
        if (typeface is null || !typeface.CharacterToGlyphMap.TryGetValue(codepoint, out var glyphIndex))
            return null;

        Geometry outline;
        lock (SyncRoot)
        {
            outline = typeface.GetGlyphOutline(glyphIndex, boxSize, boxSize);
        }

        var bounds = outline.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var scale = 1.0;
        if (bounds.Width > boxSize || bounds.Height > boxSize)
            scale = Math.Min(boxSize / bounds.Width, boxSize / bounds.Height);

        var dx = Math.Round((boxSize - bounds.Width * scale) / 2.0 - bounds.X * scale);
        var dy = Math.Round((boxSize - bounds.Height * scale) / 2.0 - bounds.Y * scale);

        var transform = new TransformGroup();
        if (scale < 1.0)
            transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(dx, dy));

        var positioned = new GeometryGroup
        {
            FillRule = FillRule.Nonzero,
            Transform = transform
        };
        positioned.Children.Add(outline);
        positioned.Freeze();
        return positioned;
    }

    private static bool TryBuildCrispFilledBadgeGeometry(SymbolRegular symbol, double boxSize, out Geometry? geometry)
    {
        geometry = null;
        if (symbol != SymbolRegular.Info24 && symbol != SymbolRegular.ErrorCircle24)
            return false;

        var center = boxSize / 2.0;
        var radius = Math.Max(2.0, (boxSize - 2.0) / 2.0);
        var outerCircle = new EllipseGeometry(new Point(center, center), radius, radius);
        var innerCutout = new GeometryGroup();

        if (symbol == SymbolRegular.Info24)
        {
            var dotRadius = Math.Max(0.85, boxSize * 0.052);
            var dot = new EllipseGeometry(new Point(center, boxSize * 0.31), dotRadius, dotRadius);
            var stemWidth = Math.Max(1.35, boxSize * 0.082);
            var stemTop = Math.Round(boxSize * 0.44);
            var stemBottom = Math.Round(boxSize * 0.76);
            var stem = new RectangleGeometry(
                new Rect(center - stemWidth / 2.0, stemTop, stemWidth, Math.Max(1.0, stemBottom - stemTop)),
                0.4,
                0.4);
            innerCutout.Children.Add(dot);
            innerCutout.Children.Add(stem);
        }
        else if (symbol == SymbolRegular.ErrorCircle24)
        {
            var stemWidth = Math.Max(1.35, boxSize * 0.082);
            var stemTop = Math.Round(boxSize * 0.24);
            var stemBottom = Math.Round(boxSize * 0.56);
            var stem = new RectangleGeometry(
                new Rect(center - stemWidth / 2.0, stemTop, stemWidth, Math.Max(1.0, stemBottom - stemTop)),
                0.4,
                0.4);
            var dotRadius = Math.Max(0.85, boxSize * 0.052);
            var dot = new EllipseGeometry(new Point(center, boxSize * 0.69), dotRadius, dotRadius);
            innerCutout.Children.Add(stem);
            innerCutout.Children.Add(dot);
        }

        var combined = new CombinedGeometry(GeometryCombineMode.Exclude, outerCircle, innerCutout);
        combined.Freeze();
        geometry = combined;
        return true;
    }

    private static GlyphTypeface? LoadTypeface(string resourceKey)
    {
        try
        {
            var wpfUiAssembly = typeof(SymbolIcon).Assembly;
            using var resourceStream = wpfUiAssembly.GetManifestResourceStream("Wpf.Ui.g.resources");
            if (resourceStream is null)
                return null;

            using var reader = new ResourceReader(resourceStream);
            foreach (System.Collections.DictionaryEntry entry in reader)
            {
                if (entry.Key is string key
                    && string.Equals(key, resourceKey, StringComparison.OrdinalIgnoreCase)
                    && entry.Value is Stream fontStream)
                {
                    var version = wpfUiAssembly.GetName().Version?.ToString() ?? "4.3.0";
                    var cacheDir = Path.Combine(Path.GetTempPath(), "WordCatcher", "FluentIcons", version);
                    Directory.CreateDirectory(cacheDir);
                    var fontPath = Path.Combine(cacheDir, Path.GetFileName(resourceKey));
                    EnsureFontFileExtracted(fontStream, fontPath);
                    var glyphTypeface = new GlyphTypeface(new Uri(fontPath, UriKind.Absolute));
                    _ = glyphTypeface.CharacterToGlyphMap;
                    return glyphTypeface;
                }
            }
        }
        catch
        {
            // FluentSystemIcons-Filled.ttf in Wpf.Ui has an OpenType layout table defect in WPF FontFaceLayoutInfo;
            // callers gracefully fall back to RegularTypeface vector outlines.
        }

        return null;
    }

    private static void EnsureFontFileExtracted(Stream fontStream, string fontPath)
    {
        using var memory = new MemoryStream();
        fontStream.CopyTo(memory);
        var bytes = memory.ToArray();

        if (File.Exists(fontPath) && new FileInfo(fontPath).Length == bytes.Length)
            return;

        var tempPath = Path.Combine(Path.GetDirectoryName(fontPath)!, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Move(tempPath, fontPath, overwrite: true);
        }
        catch (IOException) when (File.Exists(fontPath) && new FileInfo(fontPath).Length == bytes.Length)
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

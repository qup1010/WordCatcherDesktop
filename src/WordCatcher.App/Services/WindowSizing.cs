using System;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

public readonly record struct WindowSize(double Width, double Height, double MinWidth, double MinHeight);

public readonly record struct PhysicalRectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class WindowSizing
{
    /// <summary>
    /// Places a popup in physical desktop pixels. The anchor and work area must
    /// use the same physical coordinate space (including negative monitor origins).
    /// </summary>
    public static PhysicalRectangle PlacePopup(
        ScreenPoint physicalAnchor,
        PhysicalRectangle physicalWorkArea,
        double targetDpiScale,
        double popupWidthDip,
        double popupHeightDip,
        bool preserveOrigin = false)
    {
        if (!double.IsFinite(targetDpiScale) || targetDpiScale <= 0
            || physicalWorkArea.Width <= 0 || physicalWorkArea.Height <= 0
            || !double.IsFinite(popupWidthDip) || popupWidthDip <= 0
            || !double.IsFinite(popupHeightDip) || popupHeightDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetDpiScale), "浮窗尺寸、目标 DPI 与物理工作区必须有效。");

        var width = Math.Clamp((int)Math.Ceiling(popupWidthDip * targetDpiScale), 1, physicalWorkArea.Width);
        var height = Math.Clamp((int)Math.Ceiling(popupHeightDip * targetDpiScale), 1, physicalWorkArea.Height);
        var gap = (int)Math.Ceiling(16 * targetDpiScale);
        var flipGap = (int)Math.Ceiling(8 * targetDpiScale);

        double left = preserveOrigin ? physicalAnchor.X : physicalAnchor.X + (double)gap;
        double top = preserveOrigin ? physicalAnchor.Y : physicalAnchor.Y + (double)gap;

        if (!preserveOrigin && left + width > physicalWorkArea.Right)
            left = physicalAnchor.X - (double)width - flipGap;
        if (!preserveOrigin && top + height > physicalWorkArea.Bottom)
            top = physicalAnchor.Y - (double)height - flipGap;

        left = Math.Clamp(left, physicalWorkArea.Left, physicalWorkArea.Right - (double)width);
        top = Math.Clamp(top, physicalWorkArea.Top, physicalWorkArea.Bottom - (double)height);

        var x = (int)Math.Round(left, MidpointRounding.AwayFromZero);
        var y = (int)Math.Round(top, MidpointRounding.AwayFromZero);
        return new PhysicalRectangle(x, y, x + width, y + height);
    }

    public static (double Width, double MaxHeight) FitPopup(double workWidthPixels, double workHeightPixels, double dpiScale)
    {
        var work = Fit(double.MaxValue, double.MaxValue, workWidthPixels, workHeightPixels, dpiScale);
        return (Math.Min(420, Math.Max(1, work.Width - 24)), Math.Min(680, Math.Max(1, work.Height - 24)));
    }

    public static WindowSize Fit(double width, double height, double workWidthPixels, double workHeightPixels, double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0
            || !double.IsFinite(workWidthPixels) || workWidthPixels <= 0
            || !double.IsFinite(workHeightPixels) || workHeightPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScale), "显示器工作区与缩放比例必须为有效正数。");

        var availableWidth = workWidthPixels / dpiScale;
        var availableHeight = workHeightPixels / dpiScale;
        return new WindowSize(
            Math.Min(width, availableWidth), Math.Min(height, availableHeight),
            Math.Min(900, availableWidth), Math.Min(600, availableHeight));
    }
}

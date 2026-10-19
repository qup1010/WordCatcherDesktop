using System;

namespace WordCatcher.App.Services;

public readonly record struct WindowSize(double Width, double Height, double MinWidth, double MinHeight);

public static class WindowSizing
{
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

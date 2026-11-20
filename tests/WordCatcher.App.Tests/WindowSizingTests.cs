using WordCatcher.App.Services;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Tests;

public sealed class WindowSizingTests
{
    [Theory]
    [InlineData(1366, 728, 1.0)]
    [InlineData(1366, 728, 1.25)]
    [InlineData(1920, 1040, 1.5)]
    [InlineData(1920, 1040, 2.0)]
    [InlineData(1280, 680, 2.0)]
    public void LaptopWindowAndMinimumSizeFitPhysicalWorkArea(double width, double height, double scale)
    {
        var size = WindowSizing.Fit(1180, 780, width, height, scale);
        Assert.InRange(size.Width * scale, 0, width);
        Assert.InRange(size.Height * scale, 0, height);
        Assert.InRange(size.MinWidth, 0, size.Width);
        Assert.InRange(size.MinHeight, 0, size.Height);
    }

    [Fact]
    public void LargeDisplayRetainsPreferredWindowSize()
    {
        Assert.Equal(new WindowSize(1180, 780, 900, 600), WindowSizing.Fit(1180, 780, 2560, 1400, 1.5));
    }

    [Theory]
    [InlineData(800, 560, 2.0)]
    [InlineData(1280, 680, 2.0)]
    [InlineData(1366, 728, 1.25)]
    public void PopupRemainsInsideSmallHighDpiWorkArea(double width, double height, double scale)
    {
        var fit = WindowSizing.FitPopup(width, height, scale);
        Assert.InRange(fit.Width, 1, 420);
        Assert.InRange(fit.Width * scale, 1, width);
        Assert.InRange(fit.MaxHeight * scale, 1, height);
    }

    [Fact]
    public void PopupOnLeft100PercentMonitorUsesNegativePhysicalCoordinatesAndItsOwnDpi()
    {
        // Primary is 150%; the left secondary display is 100%.
        var workArea = new PhysicalRectangle(-1920, 0, 0, 1040);
        var placement = WindowSizing.PlacePopup(new ScreenPoint(-1800, 200), workArea, 1.0, 420, 360);

        Assert.Equal(new PhysicalRectangle(-1784, 216, -1364, 576), placement);
        AssertInside(placement, workArea);
    }

    [Fact]
    public void PopupOnRightUpper150PercentMonitorUsesItsOwnDpiAndVirtualDesktopOrigin()
    {
        // Primary is 100%; the 150% secondary display is right and above it.
        var workArea = new PhysicalRectangle(1920, -1440, 3840, 0);
        var placement = WindowSizing.PlacePopup(new ScreenPoint(2000, -1400), workArea, 1.5, 420, 300);

        Assert.Equal(new PhysicalRectangle(2024, -1376, 2654, -926), placement);
        AssertInside(placement, workArea);
    }

    [Fact]
    public void PopupFlipsLeftAndUpAtRightBottomOfTaskbarAdjustedWorkArea()
    {
        // The monitor extends to y=1080, while the taskbar ends the work area at y=1040.
        var workArea = new PhysicalRectangle(0, 0, 1920, 1040);
        var placement = WindowSizing.PlacePopup(new ScreenPoint(1880, 1010), workArea, 1.0, 420, 400);

        Assert.Equal(new PhysicalRectangle(1452, 602, 1872, 1002), placement);
        AssertInside(placement, workArea);
    }

    [Fact]
    public void PopupGrowthAfterTranslationRepositionsAndClipsWithinSamePhysicalWorkArea()
    {
        var workArea = new PhysicalRectangle(0, 0, 1600, 800);
        var anchor = new ScreenPoint(1500, 740);
        var beforeGrowth = WindowSizing.PlacePopup(anchor, workArea, 1.0, 420, 240);
        var afterGrowth = WindowSizing.PlacePopup(
            new ScreenPoint(beforeGrowth.Left, beforeGrowth.Top), workArea, 1.0, 420, 680, preserveOrigin: true);

        AssertInside(beforeGrowth, workArea);
        AssertInside(afterGrowth, workArea);
        Assert.Equal(680, afterGrowth.Height);
        Assert.Equal(beforeGrowth.Left, afterGrowth.Left);
        Assert.True(afterGrowth.Top < beforeGrowth.Top);
    }

    [Fact]
    public void PopupLargerThanWorkAreaIsClippedToAvailablePhysicalBounds()
    {
        var workArea = new PhysicalRectangle(-300, -100, 500, 500);
        var placement = WindowSizing.PlacePopup(new ScreenPoint(499, 499), workArea, 1.5, 900, 700);

        Assert.Equal(workArea, placement);
    }

    [Fact]
    public void PinnedPopupPreservesItsPhysicalOriginWhenContentChanges()
    {
        var workArea = new PhysicalRectangle(-1920, 0, 0, 1040);
        var currentOrigin = new ScreenPoint(-800, 300);
        var placement = WindowSizing.PlacePopup(currentOrigin, workArea, 1.5, 420, 360, preserveOrigin: true);

        Assert.Equal(-800, placement.Left);
        Assert.Equal(300, placement.Top);
        AssertInside(placement, workArea);
    }

    private static void AssertInside(PhysicalRectangle rectangle, PhysicalRectangle workArea)
    {
        Assert.InRange(rectangle.Left, workArea.Left, workArea.Right);
        Assert.InRange(rectangle.Top, workArea.Top, workArea.Bottom);
        Assert.InRange(rectangle.Right, rectangle.Left, workArea.Right);
        Assert.InRange(rectangle.Bottom, rectangle.Top, workArea.Bottom);
    }
}

using WordCatcher.App.Services;

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
}

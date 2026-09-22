using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.App.Windows;

namespace WordCatcher.App.Tests;

public class LookupLayoutTests
{
    [Fact]
    public void RealDigitalEntryRendersCompactAndExpandedWithoutLosingFooter()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (vm, _, _, _) = LookupViewModelTests.Create();
                vm.StartLookupAsync(LookupViewModelTests.Capture()).GetAwaiter().GetResult();
                var window = new LookupWindow(vm);
                var root = (FrameworkElement)window.Content;
                foreach (var mode in new[] { "compact", "expanded", "sense" })
                {
                    if (mode == "expanded") vm.ToggleDetailsCommand.Execute(null);
                    root.Measure(new Size(420, double.PositiveInfinity));
                    root.Arrange(new Rect(0, 0, 420, root.DesiredSize.Height));
                    root.UpdateLayout();
                    if (mode == "sense")
                    {
                        var sense = Descendants(root).OfType<Expander>().First();
                        sense.IsExpanded = true;
                        root.Measure(new Size(420, double.PositiveInfinity));
                        root.Arrange(new Rect(0, 0, 420, root.DesiredSize.Height));
                        root.UpdateLayout();
                        var scroll = (ScrollViewer)window.FindName("ResultScroll");
                        scroll.ScrollToVerticalOffset(230);
                        root.UpdateLayout();
                    }
                    Assert.InRange(root.ActualHeight, 200, 680);
                    if (mode == "compact") Assert.True(root.ActualHeight < 460);
                    var save = Descendants(root).OfType<Button>().Single(b => b.Content as string == "存入单词本 ↵");
                    var position = save.TransformToAncestor(root).Transform(new Point(0, 0));
                    Assert.True(position.Y + save.ActualHeight <= root.ActualHeight);
                    var bitmap = new RenderTargetBitmap(420, (int)Math.Ceiling(root.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(Path.GetTempPath(), $"wordcatcher-digital-{mode}.png"));
                    encoder.Save(file);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

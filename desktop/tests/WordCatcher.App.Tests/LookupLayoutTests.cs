using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.App.Windows;

namespace WordCatcher.App.Tests;

public class LookupLayoutTests
{
    [Fact]
    public void PopupDragHandleIsSeparateFromHeaderActions()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (vm, _, _, _) = LookupViewModelTests.Create();
                var window = new LookupWindow(vm);
                var handle = Assert.IsType<Border>(window.FindName("HeaderDragHandle"));
                var dragArea = Assert.IsType<Border>(window.FindName("HeaderDragArea"));
                var speaker = Assert.IsAssignableFrom<ButtonBase>(window.FindName("SpeakButton"));

                Assert.Equal(72, handle.Width);
                Assert.Equal(18, handle.Height);
                Assert.Equal(Cursors.SizeAll, handle.Cursor);
                Assert.Equal(Cursors.SizeAll, dragArea.Cursor);
                Assert.DoesNotContain(speaker, Descendants(handle));
                Assert.DoesNotContain(speaker, Descendants(dragArea));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

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
                foreach (var mode in new[] { "compact", "expanded", "sense", "saved", "long" })
                {
                    if (mode == "expanded") vm.ToggleDetailsCommand.Execute(null);
                    if (mode == "saved") vm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                    if (mode == "long")
                    {
                        vm.SelectedText = string.Join(" ", Enumerable.Repeat("This is a long sentence with enough detail to require wrapping.", 6));
                        vm.Word = vm.SelectedText;
                        vm.Definition = "这是一段需要换行显示的长句译文。";
                        vm.DisplayPosGroups = null;
                        vm.MemoryHook = null;
                        vm.IsDetailsExpanded = false;
                        vm.CanExpandDetails = false;
                    }
                    root.Measure(new Size(420, double.PositiveInfinity));
                    root.Arrange(new Rect(0, 0, 420, root.DesiredSize.Height));
                    root.UpdateLayout();
                    if (mode == "sense")
                    {
                        root.Measure(new Size(420, double.PositiveInfinity));
                        root.Arrange(new Rect(0, 0, 420, root.DesiredSize.Height));
                        root.UpdateLayout();
                        var scroll = (ScrollViewer)window.FindName("ResultScroll");
                        scroll.ScrollToVerticalOffset(230);
                        root.UpdateLayout();
                    }
                    Assert.InRange(root.ActualHeight, 200, 680);
                    if (mode == "compact") Assert.True(root.ActualHeight < 460);
                    var save = Descendants(root).OfType<Button>().Single(b => b.Name == "SaveWordButton");
                    if (mode == "saved") Assert.Equal("已收藏 ✓", save.Content);
                    if (mode == "long") Assert.Contains(Descendants(root).OfType<TextBlock>(), text => text.Text == vm.SelectedText && text.TextWrapping == TextWrapping.Wrap);
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

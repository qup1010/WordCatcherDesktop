using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.Core.Models;
using WordCatcher.App.Windows;

namespace WordCatcher.App.Tests;

[Collection("Application resource rendering")]
public class LookupLayoutTests
{
    [Fact]
    public void OutsideMouseDownDismissesVisibleUnpinnedCardWithoutActivation()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            LookupWindow? window = null;
            try
            {
                var (vm, _, _, _) = LookupViewModelTests.Create();
                window = new LookupWindow(vm) { ShowActivated = false };
                window.Show();
                Assert.True(window.PositionNearCursor(new ScreenPoint(100, 100)));
                Assert.False(window.IsActive);
                var origin = window.PointToScreen(new Point(0, 0));
                var inside = new ScreenPoint((int)origin.X + 8, (int)origin.Y + 8);
                var outside = new ScreenPoint(inside.X - 1000, inside.Y);
                bool ShouldQueue(ScreenPoint point) =>
                    Task.Run(() => window.ShouldQueueDismissForMouseDown(point)).GetAwaiter().GetResult();

                Assert.True(window.ContainsPhysicalPoint(inside));
                Assert.False(window.ContainsPhysicalPoint(outside));
                Assert.False(ShouldQueue(inside));
                window.DismissForMouseDown(inside);
                Assert.True(window.IsVisible);

                var menu = new ContextMenu
                {
                    PlacementTarget = (FrameworkElement)window.Content,
                    Placement = PlacementMode.Bottom,
                    VerticalOffset = 6,
                    Items = { new MenuItem { Header = "Copy" } }
                };
                menu.IsOpen = true;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                menu.UpdateLayout();
                try
                {
                    var menuOrigin = menu.PointToScreen(new Point(0, 0));
                    var menuPoint = new ScreenPoint((int)menuOrigin.X + 10, (int)menuOrigin.Y + 10);
                    Assert.False(window.ContainsPhysicalPoint(menuPoint));
                    Assert.True(ShouldQueue(menuPoint));
                    window.DismissForMouseDown(menuPoint);
                    Assert.True(window.IsVisible);
                }
                finally { menu.IsOpen = false; }

                var pin = (ToggleButton)window.FindName("PinToggle");
                pin.IsChecked = true;
                Assert.False(ShouldQueue(outside));
                window.DismissForMouseDown(outside);
                Assert.True(window.IsVisible);
                pin.IsChecked = false;

                var closed = false;
                window.Closed += (_, _) => closed = true;
                var dismissal = Task.Run(() =>
                {
                    Assert.True(window.ShouldQueueDismissForMouseDown(outside));
                    return window.Dispatcher.InvokeAsync(() => window.DismissForMouseDown(outside));
                }).GetAwaiter().GetResult();
                dismissal.Wait();
                Assert.True(closed);
                Assert.False(window.ContainsPhysicalPoint(inside));
                Assert.False(ShouldQueue(outside));
                window = null;
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

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
                Assert.Equal(Cursors.SizeAll.ToString(), handle.Cursor.ToString());
                Assert.Equal(Cursors.SizeAll.ToString(), dragArea.Cursor.ToString());
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
                foreach (var mode in new[] { "compact", "pinned", "expanded", "sense", "saved", "long" })
                {
                    if (mode == "pinned") ((ToggleButton)window.FindName("PinToggle")).IsChecked = true;
                    if (mode == "expanded") ((ToggleButton)window.FindName("PinToggle")).IsChecked = false;
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
                    if (mode == "long") Assert.Contains(Descendants(root).OfType<TextBox>(), text => text.Text == vm.SelectedText && text.TextWrapping == TextWrapping.Wrap && text.IsReadOnly);
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

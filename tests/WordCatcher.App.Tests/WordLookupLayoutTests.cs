using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Views;

namespace WordCatcher.App.Tests;

[Collection("Application resource rendering")]
public sealed class WordLookupLayoutTests
{
    [Fact]
    public void LookupPageRendersResultAtDesktopWidth()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (lookupVm, _, _, _) = LookupViewModelTests.Create();
                var pageVm = new WordLookupViewModel(lookupVm) { Query = "digital" };
                pageVm.SearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();

                var view = new WordLookupView { DataContext = pageVm };
                view.Measure(new Size(880, 620));
                view.Arrange(new Rect(0, 0, 880, 620));
                view.UpdateLayout();

                Assert.True(view.ActualWidth > 0);
                Assert.True(lookupVm.HasResult);
                Assert.Equal(Visibility.Visible, view.FindName("QueryBox") is TextBox queryBox
                    ? queryBox.Visibility
                    : Visibility.Collapsed);

                var bitmap = new RenderTargetBitmap(880, 620, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(Path.GetTempPath(), "wordcatcher-WordLookup-880.png"));
                encoder.Save(file);

                view.Measure(new Size(560, 620));
                view.Arrange(new Rect(0, 0, 560, 620));
                view.UpdateLayout();
                var contextPanel = (FrameworkElement)view.FindName("ContextPanel");
                Assert.Equal(1, Grid.GetRow(contextPanel));
                Assert.Equal(0, Grid.GetColumn(contextPanel));
                var narrow = new RenderTargetBitmap(560, 620, 96, 96, PixelFormats.Pbgra32);
                narrow.Render(view);
                var narrowEncoder = new PngBitmapEncoder();
                narrowEncoder.Frames.Add(BitmapFrame.Create(narrow));
                using var narrowFile = File.Create(Path.Combine(Path.GetTempPath(), "wordcatcher-WordLookup-560.png"));
                narrowEncoder.Save(narrowFile);

                var input = Assert.IsType<TextBox>(view.FindName("QueryBox"));
                input.Text = "digital 数字化";
                input.SelectAll();
                Assert.Equal(input.Text, input.SelectedText);
                foreach (var scale in new[] { 1.25, 1.5, 2.0 })
                {
                    view.UpdateLayout();
                    var scaled = new RenderTargetBitmap((int)(560 * scale), (int)(620 * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    scaled.Render(view);
                    var scaledEncoder = new PngBitmapEncoder();
                    scaledEncoder.Frames.Add(BitmapFrame.Create(scaled));
                    using var scaledFile = File.Create(Path.Combine(Path.GetTempPath(), $"wordcatcher-WordLookup-{scale:0.00}.png"));
                    scaledEncoder.Save(scaledFile);
                }

                lookupVm.MemoryHook = null;
                lookupVm.ContextTranslation = string.Empty;
                view.Measure(new Size(880, 620));
                view.Arrange(new Rect(0, 0, 880, 620));
                view.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, contextPanel.Visibility);
                var columns = (Grid)view.FindName("ResultColumns");
                Assert.Equal(0, columns.ColumnDefinitions[1].ActualWidth);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}

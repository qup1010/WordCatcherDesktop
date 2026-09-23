using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Views;

namespace WordCatcher.App.Tests;

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
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}

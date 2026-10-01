using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WordCatcher.App.Services;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "capture-result.json");
        var service = new SelectionCaptureService();
        using var hotkey = new HotkeyManager();
        // 保存并恢复原有剪贴板的受支持格式，不读取或输出原有内容。
        var snapshotMethod = typeof(SelectionCaptureService).GetMethod("CaptureClipboardSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
        var restoreMethod = typeof(SelectionCaptureService).GetMethod("RestoreClipboardSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!;
        var original = snapshotMethod.Invoke(null, null);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var busy = false;
        var fixture = new DataObject();
        const string text = "WordCatcher acceptance clipboard";
        const string html = "<html><body><!--StartFragment--><b>acceptance</b><!--EndFragment--></body></html>";
        const string rtf = @"{\rtf1\ansi acceptance}";
        var pixels = new byte[] { 12, 34, 56, 255, 65, 43, 21, 255 };
        fixture.SetData(DataFormats.UnicodeText, text);
        fixture.SetData(DataFormats.Html, html);
        fixture.SetData(DataFormats.Rtf, rtf);
        fixture.SetFileDropList(new StringCollection { output });
        fixture.SetImage(BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8));
        Clipboard.SetDataObject(fixture, true);
        hotkey.HotkeyTriggered += async () =>
        {
            if (busy) return;
            busy = true;
            try
            {
                var capture = await service.CaptureSelectionAsync();
                var restored = Clipboard.GetDataObject();
                var restoredPixels = new byte[8];
                var image = Clipboard.GetImage();
                if (image is not null) new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(restoredPixels, 8, 0);
                var result = new
                {
                    Capture = capture,
                    Clipboard = new
                    {
                        Text = Clipboard.GetText() == text,
                        Html = restored?.GetData(DataFormats.Html) as string == html,
                        Rtf = restored?.GetData(DataFormats.Rtf) as string == rtf,
                        Files = Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().SequenceEqual(new[] { output }),
                        Image = image?.PixelWidth == 2 && image.PixelHeight == 1 && restoredPixels.SequenceEqual(pixels)
                    }
                };
                File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(File.ReadAllText(output));
            }
            catch (Exception ex) { Console.WriteLine(ex); }
            finally
            {
                restoreMethod.Invoke(null, new[] { original });
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }
        };
        if (!hotkey.Register("Alt+Q"))
        {
            restoreMethod.Invoke(null, new[] { original });
            throw new InvalidOperationException("Alt+Q is occupied; stop the other capture instance before running the probe.");
        }
        Console.WriteLine("READY: select test text and press Alt+Q.");
        Dispatcher.Run();
    }
}

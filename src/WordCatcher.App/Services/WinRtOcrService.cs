using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Services;

namespace WordCatcher.App.Services;

public sealed class WinRtOcrService : IOcrService
{
    public async Task<string> RecognizePngAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        if (pngBytes == null || pngBytes.Length == 0)
            return string.Empty;

        ct.ThrowIfCancellationRequested();

        OcrEngine? engine;
        try
        {
            engine = CreateOcrEngine();
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            throw new InvalidOperationException("当前 Windows 运行环境暂不支持原生 OCR 引擎。", ex);
        }

        if (engine == null)
        {
            throw new InvalidOperationException("系统未安装可用的 Windows OCR 语言包，请在 Windows 设置 → 时间和语言中添加识别语言。");
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(pngBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            var maxDim = OcrEngine.MaxImageDimension;
            if (decoder.PixelWidth > maxDim || decoder.PixelHeight > maxDim)
            {
                throw new InvalidOperationException($"截图区域过大（{decoder.PixelWidth}×{decoder.PixelHeight}，上限 {maxDim}px），请缩小框选范围后重试。");
            }

            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask(ct).ConfigureAwait(false);

            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(ct).ConfigureAwait(false);
            if (result == null || result.Lines.Count == 0)
                return string.Empty;

            return OcrTextFormatter.JoinLines(result.Lines.Select(line => line.Text));
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new InvalidOperationException("截图解码或 OCR 识别失败，请重新框选清晰区域。", ex);
        }
    }

    private static OcrEngine? CreateOcrEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine != null) return engine;

        var languages = OcrEngine.AvailableRecognizerLanguages;
        var preferred = languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault();
        return preferred != null ? OcrEngine.TryCreateFromLanguage(preferred) : null;
    }
}

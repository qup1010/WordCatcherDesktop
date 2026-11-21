using System;
using System.IO;
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
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace WordCatcher.App.Services;

public sealed class WinRtOcrService : IOcrService
{
    public async Task<string> RecognizePngAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        if (pngBytes == null || pngBytes.Length == 0)
            return string.Empty;

        ct.ThrowIfCancellationRequested();

        OcrEngine? primary;
        OcrEngine? fallback;
        try
        {
            (primary, fallback) = ResolveEngines();
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or TypeInitializationException)
        {
            throw new InvalidOperationException("当前 Windows 运行环境暂不支持原生 OCR 引擎。", ex);
        }

        if (primary == null)
        {
            throw new InvalidOperationException("系统未安装可用的 Windows OCR 语言包，请在 Windows 设置 → 时间和语言中添加识别语言。");
        }

        try
        {
            var preparedBytes = PreprocessForOcr(pngBytes, OcrEngine.MaxImageDimension);

            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(preparedBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
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

            var primaryResult = await primary.RecognizeAsync(softwareBitmap).AsTask(ct).ConfigureAwait(false);
            var primaryText = primaryResult != null && primaryResult.Lines.Count > 0
                ? OcrTextFormatter.JoinLines(primaryResult.Lines.Select(line => line.Text))
                : string.Empty;

            if ((!string.IsNullOrWhiteSpace(primaryText) && primaryText.Any(char.IsLetterOrDigit)) || fallback == null)
                return primaryText;

            var fallbackResult = await fallback.RecognizeAsync(softwareBitmap).AsTask(ct).ConfigureAwait(false);
            if (fallbackResult != null && fallbackResult.Lines.Count > 0)
            {
                var fallbackText = OcrTextFormatter.JoinLines(fallbackResult.Lines.Select(line => line.Text));
                if (!string.IsNullOrWhiteSpace(fallbackText))
                    return fallbackText;
            }

            return primaryText;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new InvalidOperationException("截图解码或 OCR 识别失败，请重新框选清晰区域。", ex);
        }
    }

    internal static byte[] PreprocessForOcr(byte[] pngBytes, uint maxDimension)
    {
        using var inputMs = new MemoryStream(pngBytes, writable: false);
        using var src = new Drawing.Bitmap(inputMs);
        if (src.Width <= 0 || src.Height <= 0 || (src.Width >= 200 && src.Height >= 80))
            return pngBytes;

        var scale = src.Height < 40 ? 3 : 2;
        var pad = 16;

        if ((long)src.Width * scale + pad * 2 > maxDimension || (long)src.Height * scale + pad * 2 > maxDimension)
        {
            scale = 2;
            if ((long)src.Width * scale + pad * 2 > maxDimension || (long)src.Height * scale + pad * 2 > maxDimension)
            {
                pad = 0;
                if ((long)src.Width * scale > maxDimension || (long)src.Height * scale > maxDimension)
                    return pngBytes;
            }
        }

        var scaledW = src.Width * scale;
        var scaledH = src.Height * scale;
        var dstW = scaledW + pad * 2;
        var dstH = scaledH + pad * 2;
        var bgColor = SampleCornerBackground(src);

        using var dst = new Drawing.Bitmap(dstW, dstH, DrawingImaging.PixelFormat.Format32bppPArgb);
        using (var g = Drawing.Graphics.FromImage(dst))
        {
            g.Clear(bgColor);
            g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(
                src,
                new Drawing.Rectangle(pad, pad, scaledW, scaledH),
                new Drawing.Rectangle(0, 0, src.Width, src.Height),
                Drawing.GraphicsUnit.Pixel);
        }

        using var outMs = new MemoryStream();
        dst.Save(outMs, DrawingImaging.ImageFormat.Png);
        return outMs.ToArray();
    }

    private static Drawing.Color SampleCornerBackground(Drawing.Bitmap bitmap)
    {
        var maxX = Math.Max(0, bitmap.Width - 1);
        var maxY = Math.Max(0, bitmap.Height - 1);
        Drawing.Color[] samples =
        [
            bitmap.GetPixel(0, 0),
            bitmap.GetPixel(maxX, 0),
            bitmap.GetPixel(0, maxY),
            bitmap.GetPixel(maxX, maxY)
        ];
        var r = (int)samples.Average(c => c.R);
        var g = (int)samples.Average(c => c.G);
        var b = (int)samples.Average(c => c.B);
        return Drawing.Color.FromArgb(255, r, g, b);
    }

    private static (OcrEngine? Primary, OcrEngine? Fallback) ResolveEngines()
    {
        var languages = OcrEngine.AvailableRecognizerLanguages;
        var enLang = languages.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        var enEngine = enLang != null ? OcrEngine.TryCreateFromLanguage(enLang) : null;
        var userEngine = OcrEngine.TryCreateFromUserProfileLanguages();

        if (enEngine != null)
        {
            var fallback = userEngine != null && !string.Equals(
                userEngine.RecognizerLanguage.LanguageTag,
                enEngine.RecognizerLanguage.LanguageTag,
                StringComparison.OrdinalIgnoreCase)
                ? userEngine
                : null;
            return (enEngine, fallback);
        }

        if (userEngine != null)
            return (userEngine, null);

        var firstLang = languages.FirstOrDefault();
        return (firstLang != null ? OcrEngine.TryCreateFromLanguage(firstLang) : null, null);
    }
}

using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WpfImaging = System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ReadNow.Services;

/// <summary>
/// 截图 OCR：调用 Windows 10/11 内置的 Windows.Media.Ocr 引擎（离线、免费、中文支持好）。
/// </summary>
public static class OcrService
{
    /// <summary>识别剪贴板中的图片文字。无图片或识别失败时返回 null。必须在 UI(STA) 线程调用。</summary>
    public static async Task<string?> RecognizeClipboardImageAsync()
    {
        if (!System.Windows.Clipboard.ContainsImage()) return null;
        WpfImaging.BitmapSource? image;
        try { image = System.Windows.Clipboard.GetImage(); }
        catch { return null; }
        if (image == null) return null;
        return await RecognizeImageAsync(image);
    }

    /// <summary>识别 WPF 位图（内置区域截图裁切结果、剪贴板图片等）。</summary>
    public static async Task<string?> RecognizeImageAsync(WpfImaging.BitmapSource image)
    {
        byte[] png;
        try { png = EncodePng(image); }
        catch { return null; }
        return await RecognizePngAsync(png);
    }

    /// <summary>识别 GDI 位图（内置区域截图的结果）。</summary>
    public static async Task<string?> RecognizeBitmapAsync(Drawing.Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, Drawing.Imaging.ImageFormat.Png);
            return await RecognizePngAsync(ms.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static byte[] EncodePng(WpfImaging.BitmapSource image)
    {
        using var ms = new MemoryStream();
        var encoder = new WpfImaging.PngBitmapEncoder();
        encoder.Frames.Add(WpfImaging.BitmapFrame.Create(image));
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static async Task<string?> RecognizePngAsync(byte[] png)
    {
        try
        {
            // PNG 字节流 → WinRT IRandomAccessStream → SoftwareBitmap
            using var ras = new InMemoryRandomAccessStream();
            await ras.WriteAsync(png.AsBuffer());
            ras.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(ras);
            using var raw = await decoder.GetSoftwareBitmapAsync();
            using var bitmap = SoftwareBitmap.Convert(raw,
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            // 优先中文引擎（中英文混排也能识别），不可用则用系统默认
            var engine = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans-CN"))
                         ?? (OcrEngine.IsLanguageSupported(new Windows.Globalization.Language("en-US"))
                             ? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                             : null)
                         ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) return null;

            var result = await engine.RecognizeAsync(bitmap);
            var text = result.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}

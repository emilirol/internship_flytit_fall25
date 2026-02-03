using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;
using FlytIT.Chatbot.Utils;

namespace FlytIT.Chatbot.Indexing;

public static class PdfImageExtractor
{
    public static IEnumerable<byte[]> RenderPagesAsPng(
    string pdfPath,
    int? targetWidth = 1280,
    int? renderWidth = null,
    int? renderHeight = null)
{
    var pdfBytes = File.ReadAllBytes(pdfPath);
    using var docLib = DocLib.Instance;

    // bruk renderWidth/renderHeight hvis gitt, ellers defaults
    int rw = renderWidth ?? ConfigHelper.GetIntOrDefault("OCR_RENDER_WIDTH", AppConstants.DEFAULT_RENDER_WIDTH);
    int rh = renderHeight ?? ConfigHelper.GetIntOrDefault("OCR_RENDER_HEIGHT", AppConstants.DEFAULT_RENDER_HEIGHT);

    using var docReader = docLib.GetDocReader(pdfBytes, new PageDimensions(rw, rh));
    var pageCount = docReader.GetPageCount();

    for (int i = 0; i < pageCount; i++)
    {
        using var pageReader = docReader.GetPageReader(i);
        var rawBgra = pageReader.GetImage();
        var width   = pageReader.GetPageWidth();
        var height  = pageReader.GetPageHeight();

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        Marshal.Copy(rawBgra, 0, bitmap.GetPixels(), rawBgra.Length);

        SKBitmap finalBmp = bitmap;
        if (targetWidth.HasValue && width > targetWidth.Value)
        {
            var newWidth  = targetWidth.Value;
            var newHeight = Math.Max(1, (int)Math.Round(height * (newWidth / (double)width)));
            var resized = new SKBitmap(newWidth, newHeight, bitmap.ColorType, bitmap.AlphaType);
            using (var canvas = new SKCanvas(resized))
            {
                canvas.DrawBitmap(bitmap, new SKRect(0, 0, newWidth, newHeight));
                canvas.Flush();
            }
            finalBmp = resized;
        }

        using var image = SKImage.FromBitmap(finalBmp);
        using var png   = image.Encode(SKEncodedImageFormat.Png, 90);
        yield return png.ToArray();

        if (!ReferenceEquals(finalBmp, bitmap))
            finalBmp.Dispose();

        GC.KeepAlive(image);
        GC.KeepAlive(png);
    }
}

}

using PDFtoImage;
using SkiaSharp;

namespace MgtOcr.Ocr;

// Shrinks an uploaded file into `output` using only libraries already in the project (PDFtoImage /
// PDFium + SkiaSharp) - no external program on the server.
//   PDF       -> every page is rendered at `dpi` and rebuilt as a PDF of JPEG pages at the same
//                page size. Text in the file becomes part of the picture (not selectable/copyable
//                any more); readable on screen and for OCR. Agreed with the user, 2 Oct 2026.
//   JPG/PNG   -> longest side capped at `maxPx`; JPG re-encoded at `quality`, PNG stays PNG.
// Returns null when the file type is not handled (the caller keeps the original).
public static class FileCompressor
{
    public static string? Compress(string input, string output, int dpi, int quality, int maxPx)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf": CompressPdf(input, output, dpi, quality); return "pdf";
            case ".jpg" or ".jpeg": return ResizeImage(input, output, SKEncodedImageFormat.Jpeg, quality, maxPx) ? "jpeg" : null;
            case ".png": return ResizeImage(input, output, SKEncodedImageFormat.Png, 100, maxPx) ? "png" : null;
            default: return null;
        }
    }

    private static void CompressPdf(string input, string output, int dpi, int quality)
    {
        var bytes = File.ReadAllBytes(input);
        var pages = Conversion.GetPageCount(bytes, password: PdfPassword.Current);
        if (pages <= 0) throw new InvalidOperationException("PDF has no pages");

        using var fs = File.Create(output);
        using (var doc = SKDocument.CreatePdf(fs, new SKDocumentPdfMetadata { Producer = "MGT Document OCR" }))
        {
            for (var i = 0; i < pages; i++)
            {
                // White background (no transparency) so the page is a plain opaque JPEG.
                using var bmp = Conversion.ToImage(bytes, page: i, password: PdfPassword.Current,
                    options: new RenderOptions(Dpi: dpi, WithAnnotations: true, BackgroundColor: SKColors.White));
                // Page size in points (1/72 inch) from the rendered pixels -> same physical size as
                // the original page, rotation already applied by the renderer.
                float w = bmp.Width * 72f / dpi, h = bmp.Height * 72f / dpi;
                using var jpeg = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
                using var img = SKImage.FromEncodedData(jpeg);      // embedded as-is (JPEG), not re-encoded
                using var canvas = doc.BeginPage(w, h);
                canvas.DrawImage(img, new SKRect(0, 0, w, h));
                doc.EndPage();
            }
            doc.Close();
        }
    }

    private static SKBitmap? Scale(SKBitmap src, int maxPx)
    {
        var scale = Math.Min(1.0, (double)maxPx / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        return scale < 1.0 ? src.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell)) : src.Copy();
    }

    private static bool ResizeImage(string input, string output, SKEncodedImageFormat fmt, int quality, int maxPx)
    {
        using var src = SKBitmap.Decode(input);
        if (src is null) return false;
        using var dst = Scale(src, maxPx);
        if (dst is null) return false;
        using var data = dst.Encode(fmt, quality);
        using var fs = File.Create(output);
        data.SaveTo(fs);
        return true;
    }
}

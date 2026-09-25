using PDFtoImage;
using SkiaSharp;

namespace MgtOcr.Ocr;

// Shared PDF-page-to-PNG rendering used by Tesseract/Typhoon/Claude/Gemini/OpenAI vision calls —
// replaces PyMuPDF's page.get_pixmap(dpi=...) from the Python side. PDFtoImage wraps PDFium
// natively; a different rendering engine than MuPDF, so anti-aliasing/hinting differences are a
// real (usually small) source of OCR-accuracy drift — validated against the real PDF corpus
// during Phase 2 verification, not assumed.
public static class PdfRasterizer
{
    // JPEG variant for multi-page bundles sent inline to a vision model: a scanned A4 page as PNG
    // can be 1-2 MB, which blows past Gemini's ~20 MB inline-request limit on a 15-20 page file.
    public static int PageCount(string path)
    {
        try { return Conversion.GetPageCount(File.ReadAllBytes(path), password: PdfPassword.Current); }
        catch { return 0; }
    }

    // One page by index — used to pull in a summary sheet that sits beyond the page cap.
    public static byte[]? RenderPageToJpeg(string path, int pageIndex, int dpi, int quality = 80)
    {
        var bytes = File.ReadAllBytes(path);
        if (pageIndex < 0 || pageIndex >= Conversion.GetPageCount(bytes, password: PdfPassword.Current)) return null;
        using var bmp = Conversion.ToImage(bytes, page: pageIndex, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
        using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    public static List<byte[]> RenderPagesToJpeg(string path, int maxPages, int dpi, int quality = 80)
    {
        var bytes = File.ReadAllBytes(path);
        var n = Math.Min(maxPages, Conversion.GetPageCount(bytes, password: PdfPassword.Current));
        var results = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            using var bmp = Conversion.ToImage(bytes, page: i, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
            using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
            results.Add(data.ToArray());
        }
        return results;
    }

    public static List<byte[]> RenderPagesToPng(string path, int maxPages, int dpi)
    {
        var bytes = File.ReadAllBytes(path);
        var pageCount = Conversion.GetPageCount(bytes, password: PdfPassword.Current);
        var n = Math.Min(maxPages, pageCount);
        var results = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            using var bmp = Conversion.ToImage(bytes, page: i, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            results.Add(data.ToArray());
        }
        return results;
    }
}

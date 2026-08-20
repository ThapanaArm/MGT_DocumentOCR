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
    public static List<byte[]> RenderPagesToPng(string path, int maxPages, int dpi)
    {
        var bytes = File.ReadAllBytes(path);
        var pageCount = Conversion.GetPageCount(bytes);
        var n = Math.Min(maxPages, pageCount);
        var results = new List<byte[]>();
        for (var i = 0; i < n; i++)
        {
            using var bmp = Conversion.ToImage(bytes, page: i, options: new RenderOptions(Dpi: dpi));
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            results.Add(data.ToArray());
        }
        return results;
    }
}

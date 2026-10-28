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
    /// <summary>Every call into PDFium goes through here, one at a time.
    ///
    /// PDFtoImage wraps PDFium, which is native and NOT thread-safe, while the OCR queue reads up
    /// to three documents at once. Two rasterisations overlapping corrupts PDFium's own state and
    /// takes the whole process down with a System.ExecutionEngineException in "Unknown Module" —
    /// an error no try/catch can stop, because the CLR itself is the casualty.
    ///
    /// Rendering is CPU-bound, so serialising it costs little: three documents rasterising in
    /// parallel were competing for the same cores anyway. The waiting happens here rather than in
    /// the queue worker so every caller is covered, including the vision providers that render a
    /// single extra page on their own.</summary>
    private static readonly object PdfiumGate = new();

    // JPEG variant for multi-page bundles sent inline to a vision model: a scanned A4 page as PNG
    // can be 1-2 MB, which blows past Gemini's ~20 MB inline-request limit on a 15-20 page file.
    public static int PageCount(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            lock (PdfiumGate) return Conversion.GetPageCount(bytes, password: PdfPassword.Current);
        }
        catch { return 0; }
    }

    // One page by index — used to pull in a summary sheet that sits beyond the page cap.
    public static byte[]? RenderPageToJpeg(string path, int pageIndex, int dpi, int quality = 80)
    {
        var bytes = File.ReadAllBytes(path);
        lock (PdfiumGate)
        {
            if (pageIndex < 0 || pageIndex >= Conversion.GetPageCount(bytes, password: PdfPassword.Current)) return null;
            using var bmp = Conversion.ToImage(bytes, page: pageIndex, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
            using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data.ToArray();
        }
    }

    public static List<byte[]> RenderPagesToJpeg(string path, int maxPages, int dpi, int quality = 80)
    {
        var bytes = File.ReadAllBytes(path);
        var results = new List<byte[]>();
        lock (PdfiumGate)
        {
            var n = Math.Min(maxPages, Conversion.GetPageCount(bytes, password: PdfPassword.Current));
            for (var i = 0; i < n; i++)
            {
                using var bmp = Conversion.ToImage(bytes, page: i, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
                using var data = bmp.Encode(SKEncodedImageFormat.Jpeg, quality);
                results.Add(data.ToArray());
            }
        }
        return results;
    }

    /// <summary>The top part of every page (heading, issuer, document title) as a small JPEG — what
    /// the page classifier looks at. 90 dpi makes an A4 page ~745 px wide, which a vision model
    /// bills as one image tile, and the title of a scanned page is still easy to read at that size.
    /// <paramref name="fraction"/> is the share of the page height kept, from the top.</summary>
    public static List<byte[]> RenderHeaderStripsToJpeg(string path, int maxPages, double fraction = 0.35, int dpi = 90, int quality = 70)
    {
        var bytes = File.ReadAllBytes(path);
        var results = new List<byte[]>();
        lock (PdfiumGate)
        {
            var n = Math.Min(maxPages, Conversion.GetPageCount(bytes, password: PdfPassword.Current));
            for (var i = 0; i < n; i++) results.Add(HeaderStrip(bytes, i, fraction, dpi, quality));
        }
        return results;
    }

    /// <summary>One page's header strip (see RenderHeaderStripsToJpeg); null when the page does not exist.</summary>
    public static byte[]? RenderHeaderStripToJpeg(string path, int pageIndex, double fraction = 0.35, int dpi = 90, int quality = 70)
    {
        var bytes = File.ReadAllBytes(path);
        lock (PdfiumGate)
        {
            if (pageIndex < 0 || pageIndex >= Conversion.GetPageCount(bytes, password: PdfPassword.Current)) return null;
            return HeaderStrip(bytes, pageIndex, fraction, dpi, quality);
        }
    }

    // Caller holds PdfiumGate.
    private static byte[] HeaderStrip(byte[] pdf, int pageIndex, double fraction, int dpi, int quality)
    {
        using var bmp = Conversion.ToImage(pdf, page: pageIndex, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
        var h = Math.Max(1, (int)(bmp.Height * Math.Clamp(fraction, 0.1, 1.0)));
        using var strip = new SKBitmap();
        // ExtractSubset shares the page's pixels (no copy); it only fails on an empty bitmap, and then
        // the whole page is encoded instead.
        var src = bmp.ExtractSubset(strip, new SKRectI(0, 0, bmp.Width, h)) ? strip : bmp;
        using var data = src.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    public static List<byte[]> RenderPagesToPng(string path, int maxPages, int dpi)
    {
        var bytes = File.ReadAllBytes(path);
        var results = new List<byte[]>();
        lock (PdfiumGate)
        {
            var n = Math.Min(maxPages, Conversion.GetPageCount(bytes, password: PdfPassword.Current));
            for (var i = 0; i < n; i++)
            {
                using var bmp = Conversion.ToImage(bytes, page: i, password: PdfPassword.Current, options: new RenderOptions(Dpi: dpi));
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                results.Add(data.ToArray());
            }
        }
        return results;
    }
}

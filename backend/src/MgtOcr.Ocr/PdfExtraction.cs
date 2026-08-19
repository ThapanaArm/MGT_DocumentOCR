using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace MgtOcr.Ocr;

public class PdfBlocksResult
{
    public List<string> Left { get; set; } = [];
    public List<string> Right { get; set; } = [];
}

public static class PdfExtraction
{
    // pdf_text(): first 20 pages of embedded PDF text, "" on any failure (e.g. a scanned PDF with
    // no text layer, or a corrupt file) — mirrors Python's broad try/except.
    public static string PdfText(string path)
    {
        try
        {
            using var doc = PdfDocument.Open(path);
            var pages = doc.GetPages().Take(20).Select(p => p.Text ?? "");
            return string.Join("\n", pages);
        }
        catch
        {
            return "";
        }
    }

    // pdf_blocks(): most PO forms are 2-column (VENDOR ADDRESS | SHIP TO ADDRESS) — uses word
    // coordinates to split left/right blocks, instead of reading line-by-line (which interleaves
    // the two columns in reading order and comes out scrambled).
    //
    // Coordinate-space note: pdfplumber's `word["top"]` is the distance from the TOP of the page,
    // increasing downward. PdfPig's `Word.BoundingBox` is in native PDF space, which is BOTTOM-UP
    // (Y increases upward from the page's bottom edge) — so every Y coordinate read from PdfPig
    // here is converted with `pageHeight - word.BoundingBox.Top` before use, to match pdfplumber's
    // convention that the rest of this function's logic (row grouping by "top", "marker_y" compare)
    // was written against.
    public static PdfBlocksResult PdfBlocks(string path)
    {
        List<(double Top, double X0, string Text)> words;
        double pageWidth;
        try
        {
            using var doc = PdfDocument.Open(path);
            var page = doc.GetPage(1);
            pageWidth = page.Width;
            words = page.GetWords()
                .Select(w => (Top: page.Height - w.BoundingBox.Top, X0: w.BoundingBox.Left, Text: w.Text))
                .ToList();
        }
        catch
        {
            return new PdfBlocksResult();
        }
        if (words.Count == 0) return new PdfBlocksResult();

        var lines = new Dictionary<double, List<(double X0, string Text)>>();
        foreach (var w in words)
        {
            var key = Math.Round(w.Top / 4) * 4;
            if (!lines.TryGetValue(key, out var list)) { list = []; lines[key] = list; }
            list.Add((w.X0, w.Text));
        }
        var ordered = lines.OrderBy(kv => kv.Key).ToList();

        double? markerY = null, markerX = null, endY = null;
        foreach (var (y, ws) in ordered)
        {
            var txt = string.Join(" ", ws.OrderBy(w => w.X0).Select(w => w.Text)).ToUpperInvariant();
            if (markerY == null && Regex.IsMatch(txt, @"SHIP\s*-?\s*TO"))
            {
                markerY = y;
                foreach (var w in ws.OrderBy(w => w.X0))
                {
                    if (w.Text.ToUpperInvariant().StartsWith("SHIP")) { markerX = w.X0; break; }
                }
            }
            else if (markerY != null && endY == null &&
                     Regex.IsMatch(txt, @"DUE\s*DATE|DESCRIPTION|QUANTITY|TERMS\b|รายการ"))
            {
                endY = y;
            }
        }
        if (markerY == null || markerX == null) return new PdfBlocksResult();
        var endYVal = endY ?? (markerY.Value + 400);

        // If "SHIP TO" sits close to the left edge, it's not a 2-column form — keep the whole
        // line from the marker down as the delivery-address block instead.
        if (markerX.Value < pageWidth * 0.33)
        {
            var rows = ordered
                .Where(kv => markerY.Value <= kv.Key && kv.Key < endYVal)
                .Select(kv => string.Join(" ", kv.Value.OrderBy(w => w.X0).Select(w => w.Text)))
                .ToList();
            rows = rows.Select(r => Regex.Replace(r, @"^\s*(SHIP\s*-?\s*TO)\s*(ADDRESS)?\s*:?\s*", "", RegexOptions.IgnoreCase).Trim()).ToList();
            return new PdfBlocksResult { Left = [], Right = rows.Where(r => r != "").ToList() };
        }

        var left = new List<string>(); var right = new List<string>();
        foreach (var (y, ws) in ordered)
        {
            if (!(markerY.Value <= y && y < endYVal)) continue;
            var l = ws.OrderBy(w => w.X0).Where(w => w.X0 < markerX.Value - 12).ToList();
            var r = ws.OrderBy(w => w.X0).Where(w => w.X0 >= markerX.Value - 12).ToList();
            if (l.Count > 0) left.Add(string.Join(" ", l.Select(w => w.Text)));
            if (r.Count > 0) right.Add(string.Join(" ", r.Select(w => w.Text)));
        }

        static List<string> Clean(List<string> rows) => rows
            .Select(x => Regex.Replace(x, @"^(VENDOR|SHIP\s*-?\s*TO)\s*ADDRESS\s*:?\s*", "", RegexOptions.IgnoreCase).Trim())
            .ToList();
        return new PdfBlocksResult { Left = Clean(left).Where(x => x != "").ToList(), Right = Clean(right).Where(x => x != "").ToList() };
    }
}

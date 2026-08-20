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
    //
    // Deliberately NOT using PdfPig's Page.Text here: it concatenates every text run with no
    // layout-aware spacing or line-break reconstruction at all (confirmed empirically — it glues
    // adjacent-but-visually-separate content together, e.g. a 13-digit tax ID immediately followed
    // by an unrelated document/page reference number with no space, and a label glued directly to
    // its value with no space, e.g. "Total25,680.00"), unlike pdfplumber's extract_text() which
    // clusters words into lines by Y-position and inserts spaces/newlines accordingly. Since
    // almost every regex in AmountFinder/HeaderParser depends on a plausible separator between a
    // label and its value, reproducing that layout-aware reconstruction here is required for
    // parity, not optional — reuses the same Y-bucketing approach as PdfBlocks().
    public static string PdfText(string path)
    {
        try
        {
            using var doc = PdfDocument.Open(path);
            var pages = doc.GetPages().Take(20).Select(ReconstructPageText);
            return string.Join("\n", pages);
        }
        catch
        {
            return "";
        }
    }

    // PdfPig's page.GetWords() word-boundary detection is unreliable for this corpus's PDFs —
    // for some fonts/generators (notably Thai text) it splits nearly every glyph into its own
    // single-character "word" instead of clustering them into whole words, which is a much worse
    // failure mode than pdfplumber's default behavior. Reconstruct at the LETTER level instead,
    // matching pdfplumber's own approach (its extract_text() clusters characters using an
    // x_tolerance gap, not pre-segmented words) — group letters into lines by Y-proximity, then
    // within a line join consecutive letters directly (no space) when the horizontal gap between
    // them is small, and insert a space when the gap is large enough to be a real word boundary.
    private const double XGapIsSpace = 1.5; // points; empirically tuned against this project's real PDF corpus

    // Fixed-grid Y-bucketing (round(top/N)*N) can split one visual row into two buckets when a
    // glyph's baseline lands right on a bucket boundary (common when Thai/Latin glyphs in the same
    // row have slightly different vertical metrics) — pdfplumber avoids this with tolerance-based
    // clustering instead of a fixed grid. Approximate that here: sort all letters by Y first, then
    // start a new line only when the gap to the previous letter's Y exceeds the tolerance, rather
    // than snapping every Y to a rigid grid.
    private const double YGapNewLine = 3.0; // points

    private static string ReconstructPageText(Page page)
    {
        var rows = LetterRows(page);
        var outLines = new List<string>();
        foreach (var row in rows)
            outLines.Add(string.Join(" ", RowToWords(row).Select(w => w.Text)));
        return string.Join("\n", outLines);
    }

    // Shared building block for both PdfText (whole-page transcription) and PdfBlocks (2-column
    // layout splitting): cluster letters into rows by Y-tolerance, in top-down page order.
    private static List<List<UglyToad.PdfPig.Content.Letter>> LetterRows(Page page)
    {
        var withTop = page.Letters.Select(l => (Top: page.Height - l.GlyphRectangle.Top, Letter: l))
            .OrderBy(x => x.Top).ToList();
        var rows = new List<List<UglyToad.PdfPig.Content.Letter>>();
        double? lastTop = null;
        foreach (var (top, letter) in withTop)
        {
            if (lastTop == null || top - lastTop.Value > YGapNewLine) rows.Add([]);
            rows[^1].Add(letter);
            lastTop = top;
        }
        return rows;
    }

    // Within one row of letters, cluster into words by X-gap tolerance (pdfplumber's approach —
    // glyphs close together join into one word with no space, a larger gap starts a new word).
    private static List<(double X0, string Text)> RowToWords(List<UglyToad.PdfPig.Content.Letter> row)
    {
        var sorted = row.OrderBy(l => l.GlyphRectangle.Left).ToList();
        var words = new List<(double X0, string Text)>();
        var sb = new System.Text.StringBuilder();
        double? wordStart = null, prevRight = null;
        foreach (var l in sorted)
        {
            if (prevRight != null && l.GlyphRectangle.Left - prevRight.Value > XGapIsSpace)
            {
                words.Add((wordStart!.Value, sb.ToString()));
                sb.Clear(); wordStart = null;
            }
            wordStart ??= l.GlyphRectangle.Left;
            sb.Append(l.Value);
            prevRight = l.GlyphRectangle.Right;
        }
        if (sb.Length > 0) words.Add((wordStart!.Value, sb.ToString()));
        return words;
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
        List<(double Top, List<(double X0, string Text)> Words)> ordered;
        double pageWidth;
        try
        {
            using var doc = PdfDocument.Open(path);
            var page = doc.GetPage(1);
            pageWidth = page.Width;
            // Same letter-level clustering as PdfText — page.GetWords() unreliably splits Thai
            // text into single-character "words" for some fonts in this corpus (see PdfText's notes).
            var rows = LetterRows(page);
            ordered = rows.Select(row =>
            {
                var top = page.Height - row[0].GlyphRectangle.Top;
                return (Top: Math.Round(top / 4) * 4, Words: RowToWords(row));
            }).OrderBy(x => x.Top).ToList();
        }
        catch
        {
            return new PdfBlocksResult();
        }
        if (ordered.Count == 0) return new PdfBlocksResult();

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
                .Where(kv => markerY.Value <= kv.Top && kv.Top < endYVal)
                .Select(kv => string.Join(" ", kv.Words.OrderBy(w => w.X0).Select(w => w.Text)))
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

using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

public static class AmountFinder
{
    public const string Num = @"[\d,]+(?:\.\d+)?";
    private static readonly Regex IsolatedNum = new(
        @"(?<![A-Za-z0-9./,])" + Num + @"(?![A-Za-z0-9./%])", RegexOptions.Compiled);

    // _plausible_amount(): real amounts usually have a decimal point or thousands separator, or
    // are at least a reasonably large value — guards against picking up a row/sequence number
    // that happens to sit near a label (e.g. a stray "1" right after a column header).
    public static bool PlausibleAmount(string v) => v.Contains('.') || v.Contains(',') || TextHelpers.F(v) >= 10;

    // find_amount(): find an amount near a label — skips tax-rate numbers (e.g. "VAT 7%  4,690.00")
    // and numbers glued inside a code (e.g. "QP03209", no letter immediately before). Tries
    // "label...value" first; if not found, tries the value appearing BEFORE the label instead
    // (some OCR output has columns swapped). If a label repeats (e.g. table header + real total),
    // picks the LAST plausible match, since the real total is usually at the end of the document.
    public static double FindAmount(string text, IEnumerable<string> keys)
    {
        foreach (var k in keys)
        {
            string? best = null;
            foreach (Match m in Regex.Matches(text,
                         k + @"[^0-9\-]{0,40}(?:\d{1,2}(?:\.\d+)?\s*%[^0-9\-]{0,20})?(?<![A-Za-z])(" + Num + ")",
                         RegexOptions.IgnoreCase))
            {
                if (PlausibleAmount(m.Groups[1].Value)) best = m.Groups[1].Value;
            }
            if (best != null) return TextHelpers.F(best);

            var lm = Regex.Matches(text, k, RegexOptions.IgnoreCase);
            if (lm.Count > 0)
            {
                var lastStart = lm[^1].Index;
                var window = text.Substring(Math.Max(0, lastStart - 60), Math.Min(60, lastStart));
                var nums = IsolatedNum.Matches(window).Select(m => m.Value).Where(PlausibleAmount).ToList();
                if (nums.Count > 0) return TextHelpers.F(nums[^1]);
            }
        }
        return 0.0;
    }

    // find_doc_no(): the separator between label and value deliberately excludes '.' — including
    // it would skip past a sentence boundary and grab the next sentence's word/number instead.
    public static string FindDocNo(string text, IEnumerable<string> keys)
    {
        var fallback = "";
        var dateFull = DateFinder.DateRe;
        foreach (var k in keys)
        {
            foreach (Match m in Regex.Matches(text, k + @"[\s:#\-]*([A-Z0-9][A-Z0-9\-/_.]{3,24})", RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value.Trim(' ', '.', '-', '/');
                if (TextHelpers.FullMatch(dateFull, v)) continue;
                if (Regex.IsMatch(v, @"^[\d/.\-]{8,10}$") && v.Count(c => c == '/') == 2) continue; // looks like a date, not a doc no
                if (Regex.IsMatch(v, "^(NO|NUMBER|DATE|ADDRESS)$", RegexOptions.IgnoreCase)) continue;
                // A label with "RECEIPT" nearby (e.g. "RECEIPT/TAX INVOICE NO") usually means the
                // receipt number, not the real invoice number — prefer a label elsewhere without it.
                var ctxStart = Math.Max(0, m.Index - 20);
                if (Regex.IsMatch(text.Substring(ctxStart, m.Index - ctxStart), "RECEIPT", RegexOptions.IgnoreCase))
                {
                    fallback = fallback.Length > 0 ? fallback : v;
                    continue;
                }
                return v;
            }
        }
        return fallback;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

// Small ported helpers from ocr_engine.py used throughout the parsing pipeline.
public static class TextHelpers
{
    // th(): builds a Thai-literal regex pattern tolerant of Tesseract's habit of inserting a
    // space between every character (e.g. "ภาษีมูลค่าเพิ่ม" -> also matches "ภา ษ ี มู ล ค ่ า เพ ิ ่ ม").
    public static string Th(string s) => string.Join(@"\s*", s.Select(c => Regex.Escape(c.ToString())));

    // _clean_desc(): collapses Tesseract's inter-character spacing in Thai text, and removes a
    // stray "." OCR sometimes inserts between Thai words, while leaving English/number spacing intact.
    public static string CleanDesc(string s)
    {
        s = Regex.Replace(s, @"(?<=[ก-๙])\s*\.\s*(?=[ก-๙])", "");
        return Regex.Replace(s, @"(?<=[ก-๙])\s+(?=[ก-๙])", "").Trim();
    }

    // _f(): parse a numeric string (commas stripped), 0.0 on failure — mirrors Python's silent-fallback float().
    public static double F(object? v)
    {
        if (v == null) return 0.0;
        var s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Replace(",", "").Trim();
        return double.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var d) ? d : 0.0;
    }

    // _iso_date(): Buddhist-era (พ.ศ.) to Gregorian (ค.ศ.) year conversion, "" on invalid date.
    public static string IsoDate(int day, int month, int year)
    {
        if (year < 100) year += year > 40 ? 2500 : 2000;
        if (year > 2400) year -= 543;
        try
        {
            var dt = new DateTime(year, month, day);
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    // Python's re.fullmatch has no direct .NET equivalent — Regex.Match + checking the match
    // spans the entire string (NOT "^...$", since .NET's $ tolerates a trailing '\n' that
    // Python's fullmatch does not).
    public static bool FullMatch(Regex re, string s)
    {
        var m = re.Match(s);
        return m.Success && m.Index == 0 && m.Length == s.Length;
    }

    // Mirrors Python's str.split() with no separator: splits on any whitespace run, drops empties.
    public static string[] SplitWs(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // Mirrors Python's str.splitlines() closely enough for OCR/pdfplumber output (which only ever
    // contains \n or \r\n) — a deliberate simplification vs. Python's broader line-separator set.
    // Two behaviors of Regex.Split that differ from splitlines() and need correcting here:
    // "".splitlines() -> [] (not [""]), and a trailing terminator does NOT produce a trailing "".
    public static string[] SplitLines(string s)
    {
        if (s.Length == 0) return [];
        var parts = Regex.Split(s, "\r\n|\r|\n");
        if (parts.Length > 0 && parts[^1] == "" && (s.EndsWith('\n') || s.EndsWith('\r')))
            return parts[..^1];
        return parts;
    }
}

using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

public static class DateFinder
{
    public static readonly Dictionary<string, int> ThaiMonths = new()
    {
        ["ม.ค"] = 1, ["มกราคม"] = 1, ["ก.พ"] = 2, ["กุมภาพันธ์"] = 2, ["มี.ค"] = 3, ["มีนาคม"] = 3,
        ["เม.ย"] = 4, ["เมษายน"] = 4, ["พ.ค"] = 5, ["พฤษภาคม"] = 5, ["มิ.ย"] = 6, ["มิถุนายน"] = 6,
        ["ก.ค"] = 7, ["กรกฎาคม"] = 7, ["ส.ค"] = 8, ["สิงหาคม"] = 8, ["ก.ย"] = 9, ["กันยายน"] = 9,
        ["ต.ค"] = 10, ["ตุลาคม"] = 10, ["พ.ย"] = 11, ["พฤศจิกายน"] = 11, ["ธ.ค"] = 12, ["ธันวาคม"] = 12,
    };

    public static readonly Regex DateRe = new(
        @"(?<![\d\-/])(\d{1,2})\s*[/\-.]\s*(\d{1,2})\s*[/\-.]\s*(\d{4}|\d{2})(?![\d\-/])",
        RegexOptions.Compiled);

    public static bool ValidDmy(int d, int m, int y) =>
        d is >= 1 and <= 31 && m is >= 1 and <= 12 && (y >= 1900 || y < 100 || y > 2400);

    // find_dates(): every date found, in order of appearance — avoids matching codes like T11-26-03-16-09.
    public static List<string> FindDates(string text)
    {
        var outList = new List<string>();
        foreach (Match m in DateRe.Matches(text))
        {
            var d = int.Parse(m.Groups[1].Value); var mo = int.Parse(m.Groups[2].Value); var y = int.Parse(m.Groups[3].Value);
            if (!ValidDmy(d, mo, y)) continue;
            var iso = TextHelpers.IsoDate(d, mo, y);
            if (iso != "") outList.Add(iso);
        }
        foreach (Match m in Regex.Matches(text, @"(\d{1,2})\s+([ก-๙.]{2,12})\s+(\d{2,4})"))
        {
            var monthText = m.Groups[2].Value;
            var mon = ThaiMonths.FirstOrDefault(kv => monthText.StartsWith(kv.Key)).Value;
            if (mon != 0)
            {
                var iso = TextHelpers.IsoDate(int.Parse(m.Groups[1].Value), mon, int.Parse(m.Groups[3].Value));
                if (iso != "") outList.Add(iso);
            }
        }
        return outList;
    }

    // find_date(): if labels given, look for a date right after the label first.
    public static string FindDate(string text, IEnumerable<string>? labels = null)
    {
        foreach (var lb in labels ?? [])
        {
            var m = Regex.Match(text, lb + @"[^0-9]{0,20}" + DateRe.ToString(), RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var d = int.Parse(m.Groups[1].Value); var mo = int.Parse(m.Groups[2].Value); var y = int.Parse(m.Groups[3].Value);
                if (ValidDmy(d, mo, y)) return TextHelpers.IsoDate(d, mo, y);
            }
        }
        var ds = FindDates(text);
        return ds.Count > 0 ? ds[0] : "";
    }
}

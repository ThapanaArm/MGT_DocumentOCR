using System.Globalization;
using System.Text.RegularExpressions;

namespace MgtOcr.Core.Mapping;

// Ported from app/mapping.py's digits()/norm()/sim()/num() (lines 15-52) — function-for-function,
// exact thresholds/behavior preserved per the approved migration plan. Not configurable by design.
public static partial class MappingHelpers
{
    [GeneratedRegex(@"บริษัท|จำกัด|มหาชน|หจก\.|ห้างหุ้นส่วนจำกัด|co\.,?\s*ltd\.?|company|limited|public|pcl\.?|corp\.?|inc\.?")]
    private static partial Regex StripRegex();

    // _KEEP = re.compile(r"[^a-z0-9฀-๿]") — ฀-๿ is the Thai Unicode block (U+0E00-U+0E7F).
    [GeneratedRegex(@"[^a-z0-9฀-๿]")]
    private static partial Regex KeepRegex();

    [GeneratedRegex(@"\D")]
    private static partial Regex NonDigitRegex();

    [GeneratedRegex(@"[, ]")]
    private static partial Regex CommaSpaceRegex();

    public static string Digits(object? s) => NonDigitRegex().Replace(ToStr(s), "");

    public static string Norm(object? s) => KeepRegex().Replace(StripRegex().Replace(ToStr(s).ToLowerInvariant(), ""), "");

    // sim(): Dice coefficient over bigrams — tolerant of spelling/spacing differences.
    // Preserves the hardcoded 0.92 substring shortcut verbatim (not derived from real bigram math).
    public static double Sim(object? av, object? bv)
    {
        var a = Norm(av);
        var b = Norm(bv);
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.92;

        var bigramsA = Bigrams(a);
        var bigramsB = Bigrams(b);
        if (bigramsA.Count == 0 || bigramsB.Count == 0) return 0.0;

        var pool = new List<string>(bigramsB);
        var hit = 0;
        foreach (var x in bigramsA)
        {
            var idx = pool.IndexOf(x);
            if (idx >= 0)
            {
                pool.RemoveAt(idx);
                hit++;
            }
        }
        return 2.0 * hit / (bigramsA.Count + bigramsB.Count);
    }

    private static List<string> Bigrams(string s)
    {
        var list = new List<string>();
        for (var i = 0; i < s.Length - 1; i++) list.Add(s.Substring(i, 2));
        return list;
    }

    // num(): strips commas/spaces, 0.0 on parse failure or null — mirrors Python's num() exactly.
    public static double Num(object? v)
    {
        var s = v == null ? "0" : ToStr(v);
        s = CommaSpaceRegex().Replace(s, "");
        if (s.Length == 0) return 0.0;
        return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)
            ? d : 0.0;
    }

    private static string ToStr(object? v) => v switch
    {
        null => "",
        double d => d.ToString(CultureInfo.InvariantCulture),
        float f => f.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}

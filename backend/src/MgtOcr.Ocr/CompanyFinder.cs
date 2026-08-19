using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

public static class CompanyFinder
{
    public static readonly string TaxLabel =
        TextHelpers.Th("เลขประจำตัวผู้เสียภาษี") + "|" + TextHelpers.Th("เลขทะเบียนนิติบุคคล") +
        @"|Tax\s*(?:ID|Id|No)(?:\s*Number)?|TIN";
    public static readonly Regex Tax13 = new(@"(?<!\d)(\d[\s\-]?){13}(?!\d)", RegexOptions.Compiled);

    // find_tax_id(): 13-digit tax id after a "Tax ID" label, tolerating OCR junk chars in between.
    public static string FindTaxId(string text)
    {
        foreach (Match m in Regex.Matches(text, TaxLabel, RegexOptions.IgnoreCase))
        {
            var window = text.Substring(m.Index + m.Length, Math.Min(250, text.Length - (m.Index + m.Length)));
            var t = Tax13.Match(window);
            if (t.Success) return Regex.Replace(t.Value, @"\D", "");
        }
        var t2 = Tax13.Match(Regex.Replace(text, @"[\s\-]", ""));
        return t2.Success ? Regex.Replace(t2.Value, @"\D", "") : "";
    }

    // Pull out just the "legal-entity-name span" from a line — English side requires uppercase
    // to avoid a plain sentence like "... given to a company" being read as a company name.
    public static readonly Regex NameEn = new(
        @"[A-Z][A-Z0-9&.,'()\-/ ]{3,60}?" +
        @"(?:PUBLIC\s+COMPANY\s+LIMITED|COMPANY\s+LIMITED|CO\.\s*,?\s*LTD\.?|CORPORATION|" +
        @"LIMITED\s+PARTNERSHIP|PCL\.?|LTD\.?|LIMITED\b)", RegexOptions.Compiled);
    public static readonly Regex NameTh = new(
        @"(?:บริษัท|ห้างหุ้นส่วนจำกัด|หจก\.)[^\n]{3,70}?(?:จำกัด(?:\s*\(มหาชน\))?|จก\.)", RegexOptions.Compiled);
    // Title-Case names e.g. "Universal Chemical Supply Co., Ltd." — only used when no all-caps
    // match is found (case-sensitive, so it won't match "... to a company").
    public static readonly Regex NameMixed = new(
        @"[A-Z][A-Za-z0-9&.,'()\-/ ]{3,60}?" +
        @"(?:Co\.\s*,?\s*Ltd\.?|Public\s+Company\s+Limited|Company\s+Limited|Corporation|PCL\.?|" +
        @"Limited\b|Ltd\.?\b)", RegexOptions.Compiled);

    public static List<string> CompanyCandidates(string line)
    {
        var outList = NameTh.Matches(line).Select(m => m.Value).ToList();
        outList.AddRange(NameEn.Matches(line).Select(m => m.Value));
        if (outList.Count == 0) outList.AddRange(NameMixed.Matches(line).Select(m => m.Value));
        return outList;
    }

    public static bool IsOwnCompany(string? s, IEnumerable<string> ownCompanyKeywords)
    {
        var up = (s ?? "").ToUpperInvariant();
        return ownCompanyKeywords.Where(k => !string.IsNullOrEmpty(k)).Any(k => up.Contains(k.ToUpperInvariant()));
    }

    public static string CleanCompany(string s)
    {
        // (?![A-Za-z]) guards "TOYO"/"TOA" from having their leading "TO" stripped.
        s = Regex.Replace(s,
            @"^\s*(ATTN|SHIP\s*-?\s*TO|BILL\s*-?\s*TO|SOLD\s*-?\s*TO|VENDOR|TO|FOR|เรียน|ถึง)(?![A-Za-z])\s*[:.]?\s*",
            "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\((HEAD\s*OFFICE|BRANCH[^)]*|สำนักงานใหญ่|สาขา[^)]*)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\(\s*\d{6,}\s*\)", ""); // counterparty's own internal code
        s = Regex.Replace(Regex.Replace(s, @"\s{2,}", " "), @"[,\s]+$", "").Trim();
        return s.Length > 200 ? s[..200] : s;
    }

    // find_company_with_pos(): pick the counterparty's legal-entity name — excludes our own
    // company and ATTN lines, then picks the most-frequently-seen name (bonus score for a name
    // ending in a legal-entity suffix). Also returns the line number it was found at, so the
    // caller can look for an address following it.
    public static (string Name, int Pos) FindCompanyWithPos(
        string text, IEnumerable<string> ownCompanyKeywords, bool excludeOwn = true)
    {
        var score = new Dictionary<string, (int Cnt, string Name, int First)>();
        var lines = TextHelpers.SplitLines(text);
        for (var pos = 0; pos < lines.Length; pos++)
        {
            var raw = lines[pos];
            // Skip bank-transfer-details lines (e.g. "Bank Name: Siam Commercial Bank Public
            // Company Limited") which aren't the counterparty but end in a legal-entity suffix.
            if (Regex.IsMatch(raw, @"Bank\s*(?:Name|Account)|Account\s*(?:No|Name)|Swift\s*Code|" +
                    TextHelpers.Th("ชื่อธนาคาร") + "|" + TextHelpers.Th("เลขที่บัญชี"), RegexOptions.IgnoreCase))
                continue;
            foreach (var cand in CompanyCandidates(CleanCompany(raw)))
            {
                var s = CleanCompany(cand);
                if (s.Length < 6) continue;
                if (excludeOwn && IsOwnCompany(s, ownCompanyKeywords)) continue;
                if (Regex.IsMatch(s, "^Bank\\b", RegexOptions.IgnoreCase)) continue;
                var key = Regex.Replace(s.ToUpperInvariant(), "[^A-Z0-9ก-๙]", "");
                var (cnt, name, first) = score.TryGetValue(key, out var v) ? v : (0, s, pos);
                // A name appearing near the top of the document (letterhead) is more likely the counterparty.
                score[key] = (cnt + 1, name.Length >= s.Length ? name : s, first);
            }
        }
        if (score.Count == 0) return ("", -1);
        var best = score.Values.OrderByDescending(v => v.Cnt).ThenBy(v => v.First).First();
        return (best.Name, best.First);
    }

    public static string FindCompany(string text, IEnumerable<string> ownCompanyKeywords, bool excludeOwn = true) =>
        FindCompanyWithPos(text, ownCompanyKeywords, excludeOwn).Name;

    // _address_after(): grab the 2-3 lines after the company name as its address — stop at another label.
    public static string AddressAfter(string[] textLines, int pos)
    {
        var outList = new List<string>();
        if (pos < 0) return "";
        for (var i = pos + 1; i < Math.Min(textLines.Length, pos + 6); i++)
        {
            var s = textLines[i].Trim();
            if (s.Length == 0) break;
            if (Regex.IsMatch(s, @"TAX\s*ID|เลขประจำตัว|BRANCH|สาขา|^SHIP\b|VENDOR|ATTN", RegexOptions.IgnoreCase)) break;
            outList.Add(s);
            if (outList.Count >= 3) break;
        }
        var joined = string.Join(" ", outList);
        return joined.Length > 400 ? joined[..400] : joined;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

/// <summary>
/// A shipping bundle for several purchase orders carries one FORM SHIPPING EXPENSE per PO, and
/// each form's rows settle that PO's own accrual. Read by the AI, the forms came back merged —
/// every "STORAGE CHARGE" summed across the three forms into one row tagged with the first PO
/// (1,436.15 = 1,331.62 + 63.00 + 41.53 on PILOT INCI26080148) — and PO 2230000604 was left with
/// no rows at all. The forms are generated sheets with a real text layer, so when there are two
/// or more of them their tables are read here directly, one row per line per form, and replace
/// the AI's cost rows. VAT, withholding and duty rows (from the receipts) stay as the AI read them.
/// A scanned form has no text layer; then nothing here applies and the AI's read stands.
/// </summary>
public static class ShippingFormText
{
    public sealed record FormRow(string Desc, string Vendor, double Amount, string CostType);
    public sealed record Form(int Page, string Po, string InvoiceNo, string ShippingCode, double ImportTax, List<FormRow> Rows);

    private static readonly Regex FormSheet = new(@"FORM\s*SHIPPING\s*EXPENSE", RegexOptions.IgnoreCase);
    private static readonly Regex PoNo = new(@"PO\.?\s*NO\.?\s*[:.]?\s*(\d{10})", RegexOptions.IgnoreCase);
    private static readonly Regex InvoiceNo = new(@"INVOICE\s*NO\.?\s*[:.]?\s*([A-Z0-9][A-Z0-9\-/]{3,})", RegexOptions.IgnoreCase);
    private static readonly Regex Shipping = new(@"^\s*SHIPPING\b(?!\s*(SERVICE|EXPENSE)).*?\b(\d{7})\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ImportTax = new(@"Import\s*Tax\s+(\d[\d,]*\.\d{2})", RegexOptions.IgnoreCase);
    // "DO 4000358 17,276.11 Local" · "OTHER : Oversea Charge (USD) 2000267 70.14 Overseas". After
    // Normalize the vendor and the amount run together ("400035817,276.11"): the vendor column is
    // always a 7-digit code, so it is split off the front (see SplitVendor).
    private static readonly Regex Row = new(
        @"^[ \t]*(?<desc>\S.*?)[ \t]+(?<num>(?:#N/A[ \t]*)?\d[\d,]*\.\d{2})[ \t]*(?<cost>Local|Insurance|Fee|Overseas)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>The text layer spaces digits apart, and differently from one machine to the next —
    /// "2230000511" reads "223000051 1" on the Windows host, "7,276.11" reads "7,276. 1 1",
    /// "4000197" reads "40001 97". Every gap between digits, commas and points is closed up, so a
    /// number reads the same whatever the spacing was.</summary>
    public static string Normalize(string text) =>
        Regex.Replace(text ?? "", @"(?<=[\d,.])[ \t]+(?=[\d,.])", "");

    /// <summary>"400035817,276.11" -> ("4000358", 17276.11); "#N/A5.78" -> ("", 5.78);
    /// "1,612.42" -> ("", 1612.42).</summary>
    private static (string Vendor, double Amount)? SplitVendor(string num)
    {
        var s = num.Replace(" ", "").Replace("\t", "");
        var vendor = "";
        if (s.StartsWith("#N/A", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        else if (Regex.Match(s, @"^(\d{7})(\d[\d,]*\.\d{2})$") is { Success: true } m) { vendor = m.Groups[1].Value; s = m.Groups[2].Value; }
        return double.TryParse(s.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var amt)
            ? (vendor, amt) : null;
    }

    /// <summary>The forms of the file, in page order. Empty when the file has none with a text layer.</summary>
    public static List<Form> ReadForms(string path) => ParseForms(PdfExtraction.PageTexts(path));

    /// <summary>The forms among these page texts (one string per page), in page order.</summary>
    public static List<Form> ParseForms(IReadOnlyList<string> pages)
    {
        var forms = new List<Form>();
        for (var i = 0; i < pages.Count; i++)
        {
            var text = Normalize(pages[i]);
            if (!FormSheet.IsMatch(text)) continue;
            var po = PoNo.Match(text).Groups[1].Value;
            if (po.Length == 0) continue;
            var rows = new List<FormRow>();
            foreach (Match m in Row.Matches(text))
            {
                var desc = m.Groups["desc"].Value.Trim();
                if (Regex.IsMatch(desc, @"^(TOTAL|DESCRIPTION)\b", RegexOptions.IgnoreCase)) continue;
                if (SplitVendor(m.Groups["num"].Value) is not { } va || va.Amount == 0) continue;
                rows.Add(new FormRow(desc, va.Vendor, va.Amount, Cap(m.Groups["cost"].Value)));
            }
            var tax = ImportTax.Match(text) is { Success: true } t
                && double.TryParse(t.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var it) ? it : 0;
            forms.Add(new Form(i, po, InvoiceNo.Match(text).Groups[1].Value,
                Shipping.Match(text).Groups[2].Value, tax, rows));
        }
        return forms;
    }

    private static string Cap(string v) => v.Length == 0 ? v : char.ToUpperInvariant(v[0]) + v[1..].ToLowerInvariant();

    /// <summary>Two or more forms with distinct POs: replace the AI's cost rows with the forms'
    /// own rows (each tagged with its PO), and record the forms on the header (poList, poRef).
    /// Anything else is returned untouched.</summary>
    public static (ParsedDocument? Doc, string? Error) ApplyMultiForm(string path, string module, (ParsedDocument? Doc, string? Error) read)
    {
        var doc = read.Doc;
        if (doc == null || module is not ("AP" or "II")) return read;
        // What happened is written on the header (formTextNote) and shown with the confidence
        // note, so a read that fell back to the AI's rows says why instead of looking normal.
        void Note(string msg)
        {
            doc.Header["formTextNote"] = msg;
            Console.Error.WriteLine($"[ShippingForm] {DateTime.Now:HH:mm:ss} {Path.GetFileName(path)}: {msg}");
        }
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return read;
       List<Form> forms;
        try { forms = ReadForms(path); }
        catch (Exception ex) { Note($"FORM text not read ({ex.GetType().Name}: {ex.Message})"); return read; }
        forms = forms.GroupBy(f => f.Po).Select(g => g.First()).ToList();
        if (forms.Count < 2)
        {
            var formPages = PdfExtraction.PageTexts(path).Count(t => FormSheet.IsMatch(t));
            Note($"FORM text: {forms.Count} form(s) with a PO found in the text layer ({formPages} FORM page(s)) — AI rows kept");
            return read;
        }
        if (forms.FirstOrDefault(f => f.Rows.Count == 0) is { } empty)
        {
            Note($"FORM text: no rows read on the form of PO {empty.Po} (page {empty.Page + 1}) — AI rows kept");
            return read;
        }

        // The order the MEMO lists the POs in ("CONSOLIDATED 3 Pos #2230000511, 2230000604
        // 2230000605"), which is the order Accounting keys them; page order otherwise.
        var memo = Normalize(PdfExtraction.PageTexts(path).ElementAtOrDefault(forms[0].Page) ?? "");
        var memoAt = memo.IndexOf("CONSOLIDATED", StringComparison.OrdinalIgnoreCase);
        if (memoAt >= 0)
        {
            var order = Regex.Matches(memo[memoAt..], @"\d{10}").Select(m => m.Value).Distinct().ToList();
            if (forms.All(f => order.Contains(f.Po)))
                forms = forms.OrderBy(f => order.IndexOf(f.Po)).ToList();
        }

        static bool IsTaxOrDuty(LineItem l) =>
            (l.ExtCode ?? "").Trim().ToUpperInvariant() is "VAT" or "WHT" or "DUTY";
        // Only the receipts' VAT, withholding and duty rows stay; every cost row comes from the forms.
        var kept = doc.Lines.Where(IsTaxOrDuty).ToList();
        foreach (var f in forms)
            foreach (var r in f.Rows)
                kept.Add(new LineItem
                {
                    ExtCode = "COST", Desc = r.Desc, Qty = 1, Uom = "EA",
                    Price = r.Amount, Amount = r.Amount,
                    VendorCode = r.Vendor, CostType = r.CostType, PoRef = f.Po,
                });
        // The import duty on the customs receipt belongs to the PO whose form's MEMO carries it
        // (Import Tax 8,415.77 on PO 2230000604; PRD books ZIM1 8,415.75 on that PO).
        foreach (var l in kept.Where(l => (l.ExtCode ?? "").Trim().Equals("DUTY", StringComparison.OrdinalIgnoreCase)))
        {
            var owners = forms.Where(f => f.ImportTax > 0 && Math.Abs(f.ImportTax - l.Amount) <= 1.0).ToList();
            if (owners.Count == 1) l.PoRef = owners[0].Po;
        }
        doc.Lines = kept;

        doc.Header["poList"] = forms.Select(f => (object?)new Dictionary<string, object?>
        {
            ["po"] = f.Po, ["goodsInvoiceNo"] = f.InvoiceNo, ["grDoc"] = "",
        }).ToList();
        doc.Header["poRef"] = string.Join(", ", forms.Select(f => f.Po));
        // The goods supplier's invoice of the first form; each PO keeps its own in poList.
        if (forms[0].InvoiceNo.Length > 0) doc.Header["goodsInvoiceNo"] = forms[0].InvoiceNo;

        VisionPrompt.FillLineVatFromSourceDocs(doc);
        VisionPrompt.FillLineVatFromTaxRows(doc);
        Note($"{forms.Count} FORM SHIPPING EXPENSE read from the text layer ({string.Join(", ", forms.Select(f => $"{f.Po}: {f.Rows.Count} rows"))})");
        return read;
    }
}

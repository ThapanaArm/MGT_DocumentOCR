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
    /// <param name="Total">The form's own TOTAL row (sum of the AMOUNT column); null when not found.</param>
    public sealed record Form(int Page, string Po, string InvoiceNo, string ShippingCode, double ImportTax, List<FormRow> Rows, double? Total = null);

    /// <summary>A FORM SHIPPING EXPENSE page whose heading was found but whose PO NO. was not.</summary>
    public sealed record FormProblem(int Page, string Reason);

    // The heading may come out of the text layer with every letter spaced apart
    // ("F O R M  S H I P P I N G  E X P E N S E") on the Windows host, where the form's Arial is
    // not embedded and the installed font's metrics widen the gaps. Spaced like that it is still the
    // form (AP, 2026-11-19), so whitespace is allowed between every letter.
    public static readonly Regex FormSheet = new(
        @"F\s*O\s*R\s*M\s*S\s*H\s*I\s*P\s*P\s*I\s*N\s*G\s*E\s*X\s*P\s*E\s*N\s*S\s*E", RegexOptions.IgnoreCase);
    private static readonly Regex PoNo = new(@"PO\.?\s*NO\.?\s*[:.]?\s*(\d{10})", RegexOptions.IgnoreCase);
    private static readonly Regex InvoiceNo = new(@"INVOICE\s*NO\.?\s*[:.]?\s*([A-Z0-9][A-Z0-9\-/]{3,})", RegexOptions.IgnoreCase);
    private static readonly Regex Shipping = new(@"^\s*SHIPPING\b(?!\s*(SERVICE|EXPENSE)).*?\b(\d{7})\b", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex ImportTax = new(@"Import\s*Tax\s+(\d[\d,]*\.\d{2})", RegexOptions.IgnoreCase);
    // "TOTAL   17,397.78   -   -" — the sum of the AMOUNT column, printed under the table.
    private static readonly Regex TotalRow = new(@"^[ \t]*TOTAL[ \t]+(\d[\d,]*\.\d{2})", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    // Looser: TOTAL anywhere on a line (the row can pick up a stray cell in front of it, or its
    // amount can land on the next line when the bold row sits a little off the amount's baseline).
    private static readonly Regex TotalLoose = new(@"\bTOTAL\b[^\d]{0,80}?(\d[\d,]*\.\d{2})", RegexOptions.IgnoreCase);
    private static readonly Regex Money = new(@"\d[\d,]*\.\d{2}");
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

    /// <summary>Joins runs of letters spaced one by one back into words, line by line:
    /// "S T O R A G E  C H A R G E  4 0 0 0 3 5 7 … L o c a l" -> "STORAGECHARGE 4000357… Local".
    /// A run is three or more single characters each followed by one blank; ordinary text has no
    /// such runs and is returned unchanged. Used only on a page whose heading was found spaced.</summary>
    public static string Despace(string text) =>
        Regex.Replace(text ?? "", @"(?<![^\s])(?:[^\s][ \t]+){2,}[^\s](?![^\s])",
            m => Regex.Replace(Regex.Replace(m.Value, @"[ \t]+", ""),
                // a word and a number spaced alike run together once joined: keep them apart
                // ("STORAGECHARGE4000357285.00Local" -> "STORAGECHARGE 4000357285.00 Local")
                @"(?<=\p{L})(?=\d)|(?<=\d)(?=\p{L})", " "));

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

    /// <summary>True when any page of the PDF's text layer carries a FORM SHIPPING EXPENSE heading.</summary>
    public static bool HasFormPage(string path) => PdfExtraction.PageTexts(path).Any(t => FormSheet.IsMatch(Normalize(t)));

    /// <summary>The forms of the file, in page order. Empty when the file has none with a text layer.</summary>
    public static List<Form> ReadForms(string path) => ParseForms(PdfExtraction.PageTexts(path));

    /// <summary>The forms among these page texts (one string per page), in page order.</summary>
    public static List<Form> ParseForms(IReadOnlyList<string> pages) => ParseForms(pages, []);

    /// <summary>As above, and every FORM page that could not be used (no PO NO.) with why.</summary>
    public static List<Form> ParseForms(IReadOnlyList<string> pages, List<FormProblem> problems)
    {
        var forms = new List<Form>();
        for (var i = 0; i < pages.Count; i++)
        {
            var text = Normalize(pages[i]);
            if (!FormSheet.IsMatch(text)) continue;
            // Spaced letters (Windows host): read the page with the runs joined back into words,
            // so "P O .  N O ." / "T O T A L" / "L o c a l" match the patterns below.
            if (!Regex.IsMatch(text, @"FORM\s*SHIPPING\s*EXPENSE", RegexOptions.IgnoreCase)) text = Normalize(Despace(pages[i]));
            var po = PoNo.Match(text).Groups[1].Value;
            if (po.Length == 0) { problems.Add(new FormProblem(i, "PO NO. not found on the form")); continue; }
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
            static double? Num(string v) =>
                double.TryParse(v.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
            var rowSum = Math.Round(rows.Sum(r => r.Amount), 2);
            // The form's TOTAL: the TOTAL row; else TOTAL with its amount further along (or on the
            // next line) when that amount is the rows' sum; else the rows' sum printed anywhere
            // below the last row — on the Windows host the TOTAL row of TSH26207S1 (17,397.78) was
            // not found as one line (AP, 2026-11-19). Anything else stays "not found" -> Needs Review.
            double? total = TotalRow.Match(text) is { Success: true } tm ? Num(tm.Groups[1].Value) : null;
            if (total == null && TotalLoose.Match(text) is { Success: true } tl && Num(tl.Groups[1].Value) is { } lv
                && Math.Abs(lv - rowSum) <= 0.01) total = lv;
            if (total == null && rows.Count > 0)
            {
                var lastRow = Row.Matches(text).Cast<Match>().LastOrDefault();
                var below = lastRow == null ? text : text[(lastRow.Index + lastRow.Length)..];
                if (Money.Matches(below).Select(m => Num(m.Value)).FirstOrDefault(v => v is { } x && Math.Abs(x - rowSum) <= 0.01) is { } found)
                    total = found;
            }
            forms.Add(new Form(i, po, InvoiceNo.Match(text).Groups[1].Value,
                Shipping.Match(text).Groups[2].Value, tax, rows, total));
        }
        return forms;
    }

    private static string Cap(string v) => v.Length == 0 ? v : char.ToUpperInvariant(v[0]) + v[1..].ToLowerInvariant();

    private static readonly Regex CustomsIssuer = new(@"CUSTOMS|ศุลกากร", RegexOptions.IgnoreCase);
    private static readonly Regex FeeDesc = new(@"FEE|FORMALIT|ค่าธรรมเนียม", RegexOptions.IgnoreCase);
    private static readonly Regex LongControlNo = new(@"^\d{15,}$");

    /// <summary>Two checks on the Customs Department's part of a shipping read (AP, 2026-11-19):
    /// <list type="bullet">
    /// <item>The customs processing fee is already a cost row of the form (OTHER : CUSTOMS FEE 200,
    /// vendor 4000002); a DUTY row of the same fee from the receipt ("Customs Formalities Fee"
    /// 200) doubled it to 400 on the 4000002 invoice. Such a DUTY row is dropped.</item>
    /// <item>The receipt's number is the "เลขที่ชำระอากร" (0152-091679/16-11-69), never the long
    /// control number at the top left (69111650364600000177). When the read still came back with
    /// the long number the document is marked Needs Review (header.customsReview): it becomes the
    /// SAP Reference and the Input tax line's Assignment.</item>
    /// </list></summary>
    public static void CheckCustoms(ParsedDocument doc)
    {
        static bool Is(LineItem l, string code) => (l.ExtCode ?? "").Trim().Equals(code, StringComparison.OrdinalIgnoreCase);
        var feeCosts = doc.Lines.Where(l => !Is(l, "VAT") && !Is(l, "WHT") && !Is(l, "DUTY")
            && (string.Equals(l.CostType, "Fee", StringComparison.OrdinalIgnoreCase) || (l.Desc ?? "").Contains("CUSTOMS FEE", StringComparison.OrdinalIgnoreCase)))
            .Select(l => l.Amount).ToList();
        var dropped = doc.Lines.Where(l => Is(l, "DUTY") && FeeDesc.IsMatch(l.Desc ?? "")
            && feeCosts.Any(a => Math.Abs(a - l.Amount) <= 0.01)).ToList();
        foreach (var l in dropped)
        {
            doc.Lines.Remove(l);
            Console.Error.WriteLine($"[ShippingForm] dropped DUTY \"{l.Desc}\" {l.Amount:N2} — the same fee is a cost row already");
        }

        var longNos = new List<string>();
        foreach (var l in doc.Lines.Where(l => Is(l, "VAT") || Is(l, "DUTY")))
            if (CustomsIssuer.IsMatch((l.IssuerName ?? "") + " " + (l.Desc ?? "")) && LongControlNo.IsMatch((l.TaxDocNo ?? "").Trim()))
                longNos.Add(l.TaxDocNo.Trim());
        if (doc.Header.TryGetValue("sourceDocs", out var v) && v is List<object?> docs)
            foreach (var d in docs.OfType<Dictionary<string, object?>>())
                if (CustomsIssuer.IsMatch(d.GetValueOrDefault("issuerName")?.ToString() ?? "")
                    && LongControlNo.IsMatch((d.GetValueOrDefault("docNo")?.ToString() ?? "").Trim()))
                    longNos.Add((d.GetValueOrDefault("docNo")?.ToString() ?? "").Trim());
        if (longNos.Count > 0)
        {
            doc.Header["customsReview"] = "Customs receipt number read as the long control number ("
                + string.Join(", ", longNos.Distinct()) + ") instead of เลขที่ชำระอากร (e.g. 0152-091679) — fix the Reference / Tax Invoice No.";
            doc.Confidence = Math.Min(doc.Confidence, 0.6);
        }
    }

    /// <summary>The cost rows of a shipping document come from its FORM SHIPPING EXPENSE page(s)
    /// read from the text layer — one form or several (AP, 2026-11-19). Each form's rows are tagged
    /// with its own PO NO.; they replace the AI's cost rows only when every form's rows add up to
    /// its own TOTAL row. VAT, WHT and duty rows stay as the AI read them from the receipts.
    /// Anything that cannot be trusted (no form, PO missing, rows missing, total off) keeps the
    /// AI's rows and marks the document Needs Review (header.formTextReview) with the reason and
    /// page. A goods (purchase order) read is left alone: its lines are the PO's items.</summary>
    /// <remarks>Why one form too: TSH26207S1 / PO 2230000675 (form on page 19) came back from the
    /// AI with STORAGE CHARGE 304.95 from a receipt instead of the form's 285.00, and DO 13,612.78
    /// missing; the text-layer read was used only when there were two or more forms.</remarks>
    public static (ParsedDocument? Doc, string? Error) ApplyMultiForm(string path, string module, (ParsedDocument? Doc, string? Error) read, bool isPoRead = false)
    {
        var doc = read.Doc;
        if (doc == null || module is not ("AP" or "II") || isPoRead) return read;
        CheckCustoms(doc);
        // What happened is written on the header (formTextNote) and shown with the confidence
        // note, so a read that fell back to the AI's rows says why instead of looking normal.
        void Note(string msg)
        {
            doc.Header["formTextNote"] = msg;
            Console.Error.WriteLine($"[ShippingForm] {DateTime.Now:HH:mm:ss} {Path.GetFileName(path)}: {msg}");
        }
        // Needs Review: the AI's rows are kept, the reason is shown, and the read is marked below
        // 90% so the badge turns amber and the user checks the rows before sending to SAP.
        void Review(string reason)
        {
            doc.Header["formTextReview"] = reason;
            doc.Confidence = Math.Min(doc.Confidence, 0.6);
            Note($"Needs Review — {reason} — AI rows kept");
        }
        // Only a shipping document is expected to carry a form; an ordinary supplier invoice is not
        // flagged for not having one.
        bool LooksLikeShipping() =>
            doc.Header.GetValueOrDefault("documentType")?.ToString()?.Contains("SHIPPING", StringComparison.OrdinalIgnoreCase) == true
            || doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.CostType));
        if (!Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return read;
        List<Form> forms;
        var problems = new List<FormProblem>();
        IReadOnlyList<string> pageTexts;
        try { pageTexts = PdfExtraction.PageTexts(path); forms = ParseForms(pageTexts, problems); }
        catch (Exception ex) { Note($"FORM text not read ({ex.GetType().Name}: {ex.Message})"); return read; }
        forms = forms.GroupBy(f => f.Po).Select(g => g.First()).ToList();
        string Pg(Form f) => $"page {f.Page + 1}";
        if (problems.Count > 0)
        {
            Review(string.Join("; ", problems.Select(p => $"FORM SHIPPING EXPENSE page {p.Page + 1}: {p.Reason}")));
            return read;
        }
        if (forms.Count == 0)
        {
            // Always said, so a read that did not use the form shows why (and whether the PDF's
            // text could be read at all: 0 pages = the file's text layer was not readable).
            // Diagnostics: on the Windows host TSH26207S1 gave 19 pages of text yet no page matched
            // the heading, while the same file matches elsewhere. Log how many pages carry any text
            // and the start of each page that mentions SHIPPING with spaces removed.
            var withText = pageTexts.Count(t => !string.IsNullOrWhiteSpace(t));
            Console.Error.WriteLine($"[ShippingForm] {Path.GetFileName(path)}: no form heading — {withText}/{pageTexts.Count} page(s) carry text");
            for (var pi = 0; pi < pageTexts.Count; pi++)
            {
                var flat = Regex.Replace(pageTexts[pi] ?? "", @"\s+", "");
                if (flat.Contains("SHIPPING", StringComparison.OrdinalIgnoreCase) && flat.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase))
                    Console.Error.WriteLine($"[ShippingForm]   page {pi + 1}: {(pageTexts[pi] ?? "").Replace("\n", " | ")[..Math.Min(300, (pageTexts[pi] ?? "").Length)]}");
            }
            if (LooksLikeShipping())
                Review($"FORM SHIPPING EXPENSE not found in the PDF text ({pageTexts.Count} page(s) of text read) — check the cost rows against the form");
            else
                Note($"FORM text: no FORM SHIPPING EXPENSE page in the PDF text ({pageTexts.Count} page(s) of text read)");
            return read;
        }
        if (forms.FirstOrDefault(f => f.Rows.Count == 0) is { } empty)
        {
            Review($"no rows read on the FORM of PO {empty.Po} ({Pg(empty)})");
            return read;
        }
        if (forms.FirstOrDefault(f => f.Total == null) is { } noTotal)
        {
            Console.Error.WriteLine($"[ShippingForm] page {noTotal.Page + 1} text as read:\n{pageTexts.ElementAtOrDefault(noTotal.Page)}");
            Review($"TOTAL row not found on the FORM of PO {noTotal.Po} ({Pg(noTotal)})");
            return read;
        }
        if (forms.FirstOrDefault(f => Math.Abs(f.Rows.Sum(r => r.Amount) - f.Total!.Value) > 0.01) is { } off)
        {
            Review($"FORM of PO {off.Po} ({Pg(off)}): rows add up to {off.Rows.Sum(r => r.Amount):N2} but TOTAL is {off.Total:N2}");
            return read;
        }

        // The order the MEMO lists the POs in ("CONSOLIDATED 3 Pos #2230000511, 2230000604
        // 2230000605"), which is the order Accounting keys them; page order otherwise.
        var memo = Normalize(pageTexts.ElementAtOrDefault(forms[0].Page) ?? "");
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
        CheckCustoms(doc); // the form's own CUSTOMS FEE row now stands for the receipt's fee

        if (forms.Count > 1)
        {
            doc.Header["poList"] = forms.Select(f => (object?)new Dictionary<string, object?>
            {
                ["po"] = f.Po, ["goodsInvoiceNo"] = f.InvoiceNo, ["grDoc"] = "",
            }).ToList();
            doc.Header["poRef"] = string.Join(", ", forms.Select(f => f.Po));
        }
        else doc.Header["poRef"] = forms[0].Po;
        // The goods supplier's invoice of the first form; each PO keeps its own in poList.
        if (forms[0].InvoiceNo.Length > 0) doc.Header["goodsInvoiceNo"] = forms[0].InvoiceNo;
        doc.Header.Remove("formTextReview");

        VisionPrompt.FillLineVatFromSourceDocs(doc);
        VisionPrompt.FillLineVatFromTaxRows(doc);
        Note($"{forms.Count} FORM SHIPPING EXPENSE read from the text layer ({string.Join(", ", forms.Select(f => $"{f.Po}: {f.Rows.Count} rows = TOTAL {f.Total:N2}, {Pg(f)}"))})");
        return read;
    }
}

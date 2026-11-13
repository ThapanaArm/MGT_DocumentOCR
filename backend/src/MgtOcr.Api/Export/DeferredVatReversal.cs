using MgtOcr.Core;

namespace MgtOcr.Api.Export;

/// <summary>
/// The posting that moves a supplier invoice's VAT out of deferred input tax once the supplier's
/// own tax invoice has arrived: the claimable account debited, the deferred account credited, for
/// the same amount, one pair per tax invoice.
///
/// It lives on its own because the same entry goes out two ways — SAP's "Upload General Journal
/// Entry" workbook (JournalEntryAccruedExcel) and the RPA Journal Voucher workbook
/// (JournalVoucherExcel). Accounting asked for the RPA file to carry exactly what the SAP one
/// does, so both read their rows from here and neither can drift from the other.
/// </summary>
public static class DeferredVatReversal
{
    // From posted document 5100001129 (11730000 with D1) and the chart of accounts.
    public const string GlInputTax = "11720000";
    public const string GlDeferredInputTax = "11730000";
    public const string InputTaxCode = "V1";
    public const string DeferredTaxCode = "D1";

    /// <summary>The rows of the entry, in the same shape as the G/L Account Items table
    /// (glAccount / drCr / amount / taxCode / assignment / itemText), so every writer that already
    /// renders those rows can render these. Empty when the document has no deferred VAT.</summary>
    public static List<Dictionary<string, object?>> Rows(Dictionary<string, object?> doc)
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach (var (text, assignment, amount) in Pairs(doc))
        {
            rows.Add(Row(GlInputTax, "D", amount, InputTaxCode, assignment, text));
            rows.Add(Row(GlDeferredInputTax, "C", amount, DeferredTaxCode, assignment, text));
        }
        return rows;
    }

    /// <summary>One entry per tax invoice whose VAT is still deferred: the text SAP's own documents
    /// carry (the issuer's name -- no invoice number since 2026-11-11), that invoice's number, and its VAT amount.</summary>
    public static List<(string Text, string Assignment, double Amount)> Pairs(
        Dictionary<string, object?> doc)
    {
        var pairs = new List<(string, string, double)>();
        foreach (var t in EffectiveTaxRows(doc))
        {
            var amount = Num(t.Get("docCurrencyAmt"));
            if (amount == 0) continue;
            var code = t.GetStr("taxCode").Trim();
            var kind = t.GetStr("taxKind").Trim();
            var deferred = kind.Length > 0
                ? string.Equals(kind, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                : code.StartsWith('D') || code.StartsWith('d');
            if (!deferred || !ClaimedNow(t)) continue;

            // AP, 2026-11-11: Input tax text = the issuer's name only (Master 8 "Tax Invoice
            // Issuer" name, max 50), no ",INV.<no>"; the tax invoice number stays the Assignment.
            var issuer = MgtOcr.Ocr.VisionPrompt.EnglishIssuerName(t.GetStr("issuerName").Trim()).ToUpperInvariant();
            if (issuer.Length > 50) issuer = issuer[..50].TrimEnd();
            var docNo = t.GetStr("taxDocNo").Trim();
            var header = doc.Get("header") as Dictionary<string, object?> ?? doc;
            var text = IssuerInvText(header, issuer, t.GetStr("issuerTaxId"), docNo);
            pairs.Add((text, docNo, amount));
        }
        return pairs;
    }

    /// <summary>The Tax tab's rows — from the document when they were saved, and otherwise derived
    /// from the read's own VAT lines exactly as the screen derives them.
    ///
    /// This matters: the Tax tab is SEEDED in the browser from the VAT lines and is only written to
    /// the database once somebody saves the document. An export read the stored header, found no
    /// tax rows on a document nobody had saved yet, and answered "no deferred input tax" while the
    /// screen was showing three tax rows — which is exactly as confusing as it sounds.</summary>
    /// <summary>
    /// Tax rows that cannot go out because the issuer's Tax ID is missing or not 13 digits.
    /// Required by Accounting (2026-11-12): every tax invoice on the input tax report carries its
    /// issuer's Tax ID, and it is printed on every one of these papers. "PI-2608011 (PROMPT …)".
    /// </summary>
    public static List<string> MissingTaxIds(Dictionary<string, object?> doc)
    {
        var missing = new List<string>();
        var header = doc.Get("header") as Dictionary<string, object?> ?? doc;
        var invoiceNo = header.GetStr("invoiceNo").Trim();
        static string First(string? n)
        {
            var v = (n ?? "").Trim();
            var cut = v.IndexOfAny([' ', ',']);
            return (cut > 0 ? v[..cut] : v).ToUpperInvariant();
        }
        var vendorWord = First(header.GetStr("vendorName"));
        foreach (var t in TaxRows(doc))
        {
            if (Num(t.Get("docCurrencyAmt")) == 0) continue;
            var code = t.GetStr("taxCode").Trim().ToUpperInvariant();
            if (code is "VX" or "") continue;
            // The Customs Department's import VAT has no Tax ID on its receipt and the input tax
            // report leaves it blank (Accounting, 2026-11-12) — it is never asked for.
            if (IsImportVat(t)) continue;
            // The invoicing party's OWN tax invoice (a goods invoice like Namsiang NS00-ITD26091326,
            // whose tax row is computed from the header and names no issuer): the supplier invoice
            // posts against that supplier's Business Partner, which carries the Tax ID, so the
            // report has it. Only tax invoices from somebody else need the ID on the row.
            var rowNo = t.GetStr("taxDocNo").Trim();
            var rowIssuer = First(t.GetStr("issuerName"));
            if (rowNo.Length == 0 || rowIssuer.Length == 0
                || string.Equals(rowNo, invoiceNo, StringComparison.OrdinalIgnoreCase)
                || (vendorWord.Length > 0 && rowIssuer == vendorWord)) continue;
            var digits = new string(t.GetStr("issuerTaxId").Where(char.IsDigit).ToArray());
            if (digits.Length == 13) continue;
            var doc_ = t.GetStr("taxDocNo").Trim();
            var who = t.GetStr("issuerName").Trim();
            missing.Add($"{(doc_.Length > 0 ? doc_ : "(no tax invoice no.)")}{(who.Length > 0 ? $" ({who})" : "")}");
        }
        return missing;
    }

    public static string MissingTaxIdMessage(List<string> missing) =>
        "ยังไม่มีเลขประจำตัวผู้เสียภาษี (Tax ID 13 หลัก) ของผู้ออกใบกำกับภาษี: " + string.Join(", ", missing)
        + " — ตรวจในเอกสารอีกครั้ง (มักพิมพ์ใต้ชื่อผู้ออกใบหรือมุมบน) แล้วกรอกในแท็บ Tax ให้ครบก่อนส่ง";

    public static List<Dictionary<string, object?>> TaxRows(Dictionary<string, object?> doc)
    {
        var header = doc.Get("header") as Dictionary<string, object?> ?? doc;
        var saved = ReadRows(header, "taxItems");
        if (saved.Count > 0) return saved;

        // Same rule as the screen: V0/V1/V2 when the tax invoice is in hand, D0/D1/D2 while it is
        // not, at the rate the document states.
        var rate = Num(header.Get("vatRate"));
        if (rate == 0) rate = 7;
        string CodeFor(string kind) =>
            (string.Equals(kind, "DEFERRED", StringComparison.OrdinalIgnoreCase) ? "D" : "V")
            + (rate == 10 ? "2" : rate == 0 ? "0" : "1");

        var rows = new List<Dictionary<string, object?>>();
        if (doc.Get("lines") is not System.Collections.IEnumerable lines) return rows;
        foreach (var raw in lines)
        {
            if (raw is not Dictionary<string, object?> l) continue;
            if (!string.Equals(l.GetStr("extCode").Trim(), "VAT", StringComparison.OrdinalIgnoreCase)) continue;
            var amount = Num(l.Get("amount"));
            if (amount == 0) continue;
            var extra = l.Get("extra") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            var kind = extra.GetStr("taxKind").Trim();
            rows.Add(new Dictionary<string, object?>
            {
                ["label"] = "Input VAT",
                ["drCr"] = "S",
                ["docCurrencyAmt"] = amount,
                ["taxCode"] = CodeFor(kind),
                ["taxKind"] = kind,
                ["vendorCode"] = extra.GetStr("vendorCode").Trim(),
                ["issuerName"] = extra.GetStr("issuerName").Trim(),
                ["issuerTaxId"] = extra.GetStr("issuerTaxId").Trim(),
                ["issuerBranch"] = extra.GetStr("issuerBranch").Trim(),
                ["taxDocNo"] = extra.GetStr("taxDocNo").Trim(),
                ["taxDocDate"] = extra.GetStr("taxDocDate").Trim(),
                ["baseAmount"] = Num(extra.Get("baseAmount")),
            });
        }
        return rows;
    }

    /// <summary>The tax rows as they are POSTED. On a shipping bundle (planned delivery costs) every
    /// VAT the agent bills or paid on our behalf goes in deferred — D1, 11730000 — whatever the paper
    /// is, and is moved to V1 by the weekly Input VAT voucher (SRS Record Automatic Journal Voucher,
    /// topic 3) once the tax invoice is filed. That is how Accounting posts it (PRD 5100001131:
    /// PROMPT 105.00 D1 and Cargoport + Port Authority 782.99 D1). The one exception is the Customs
    /// Department's import VAT, which goes straight to V1 (PRD 5105604031) and stays as read.</summary>
    public static List<Dictionary<string, object?>> EffectiveTaxRows(Dictionary<string, object?> doc)
    {
        var rows = TaxRows(doc);
        if (!IsShippingBundle(doc)) return rows;
        var vendorOfDoc = VendorBySourceDoc(doc);
        var result = new List<Dictionary<string, object?>>();
        foreach (var raw in rows)
        {
            var t = raw;
            // A tax row the read left untagged belongs to the supplier whose cost it taxes, not by
            // default to the agent: Bangkok Insurance's VAT 138.81 (2370333) had no vendor code, so
            // it was posted on PILOT's invoice and the insurance invoice went to SAP with no VAT
            // (PRD 5100001118 books it on 4000415: 1,983.05 + D1 138.81 = 2,121.81). The bundle's
            // document list says which heading that tax invoice is filed under (Insurance), and the
            // form's cost lines under that heading name its supplier.
            if (t.GetStr("vendorCode").Trim().Length == 0
                && vendorOfDoc.TryGetValue(t.GetStr("taxDocNo").Trim().ToUpperInvariant(), out var v))
                t = new Dictionary<string, object?>(t) { ["vendorCode"] = v };
            var code = t.GetStr("taxCode").Trim().ToUpperInvariant();
            if (code.Length == 2 && code[0] == 'V' && code[1] != 'X' && !IsImportVat(t))
                result.Add(new Dictionary<string, object?>(t)
                {
                    ["taxCode"] = "D" + code[1],
                    ["taxKind"] = "DEFERRED",
                    ["_readAs"] = code,
                });
            else result.Add(t);
        }
        return result;
    }

    /// <summary>Tax-invoice number → the supplier code of the form's cost lines filed under the same
    /// COSTS heading, when exactly one supplier has costs there. Built from header.sourceDocs (each
    /// document with its costType) and the cost lines (extra.costType / extra.vendorCode).</summary>
    private static Dictionary<string, string> VendorBySourceDoc(Dictionary<string, object?> doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var header = doc.Get("header") as Dictionary<string, object?> ?? doc;
        static string Heading(string? v)
        {
            var s = (v ?? "").Trim().ToLowerInvariant();
            return s.StartsWith("loc") ? "LOCAL" : s.StartsWith("ins") ? "INSURANCE"
                : s.StartsWith("fee") ? "FEE" : s.StartsWith("over") ? "OVERSEAS" : "";
        }
        var vendorsByHeading = new Dictionary<string, HashSet<string>>();
        if (doc.Get("lines") is System.Collections.IEnumerable lines)
            foreach (var raw in lines)
            {
                if (raw is not Dictionary<string, object?> l) continue;
                if (l.GetStr("extCode").Trim().ToUpperInvariant() is "VAT" or "WHT" or "DUTY") continue;
                var extra = l.Get("extra") as Dictionary<string, object?>;
                var h = Heading(extra?.GetStr("costType"));
                var vc = (extra?.GetStr("vendorCode") ?? "").Trim();
                if (h.Length == 0 || vc.Length == 0) continue;
                (vendorsByHeading.TryGetValue(h, out var set) ? set : vendorsByHeading[h] = []).Add(vc);
            }
        foreach (var d in ReadRows(header, "sourceDocs"))
        {
            var no = d.GetStr("docNo").Trim().ToUpperInvariant();
            var h = Heading(d.GetStr("costType"));
            if (no.Length == 0 || h.Length == 0 || h == "LOCAL") continue;   // Local is the agent's own
            if (vendorsByHeading.TryGetValue(h, out var set) && set.Count == 1) map[no] = set.First();
        }
        return map;
    }

    /// <summary>A supplier invoice that settles planned delivery costs on a PO (Reference Document
    /// Category 2 or 3) — the shipping bundles.</summary>
    public static bool IsShippingBundle(Dictionary<string, object?> doc)
    {
        if (SapImportSupplierInvoiceExcel.IsPoKeyed(doc)) return false;
        var header = doc.Get("header") as Dictionary<string, object?> ?? doc;
        return header.GetStr("refDocType").Trim() is "2" or "3";
    }

    /// <summary>Import VAT on a Customs Department receipt.</summary>
    public static bool IsImportVat(Dictionary<string, object?> taxRow)
    {
        var issuer = taxRow.GetStr("issuerName");
        return issuer.Contains("CUSTOMS", StringComparison.OrdinalIgnoreCase)
            || issuer.Contains("ศุลกากร", StringComparison.Ordinal)
            || issuer.Contains("กรมศุล", StringComparison.Ordinal);
    }

    /// <summary>The voucher's own reference line: what this entry is and which invoice it belongs
    /// to, within the 25 characters SAP's header text allows.</summary>
    /// <summary>Whether a posted-deferred tax row is moved to V1 by the Input VAT journal entry.
    /// AP, 2026-11-12: only the rows the user chose as V1 on the Tax tab (posted D1 on the shipping
    /// bundle, see <see cref="EffectiveTaxRows"/>, which keeps the choice in _readAs). A row the user
    /// chose as D1 stays deferred — it is claimed later — so it gets no entry and must not show on
    /// the input tax report (JV 100000762, PROMPT PI-2608011, should not exist).</summary>
    public static bool ClaimedNow(Dictionary<string, object?> taxRow) =>
        taxRow.GetStr("_readAs").Trim().StartsWith("V", StringComparison.OrdinalIgnoreCase);

    /// <summary>"CARGOPORT,INV.RC26070748": the issuer's abbreviation (Vendor master ShortName,
    /// found by Tax ID; otherwise the first word of its English name) + ",INV." + its tax invoice
    /// number. The text of both lines of the Input VAT journal entry (AP, 2026-11-12).</summary>
    public static string IssuerInvText(Dictionary<string, object?> header, string issuerName, string? taxId, string docNo)
    {
        var english = MgtOcr.Ocr.VisionPrompt.EnglishIssuerName((issuerName ?? "").Trim()).ToUpperInvariant();
        var abbr = SupplierShortNames.Lookup(header, taxId);
        if (abbr.Length == 0) abbr = SupplierShortNames.FirstWord(english);
        var no = (docNo ?? "").Trim();
        var text = abbr.Length > 0 && no.Length > 0 ? $"{abbr},INV.{no}" : english.Length > 0 ? english : no;
        return text.Length > 50 ? text[..50] : text;
    }

    public static string Reference(Dictionary<string, object?> docOrHeader)
    {
        var header = docOrHeader.Get("header") as Dictionary<string, object?> ?? docOrHeader;
        var invoiceNo = header.GetStr("invoiceNo").Trim();
        if (invoiceNo.Length == 0) invoiceNo = header.GetStr("poRef").Trim();
        var text = invoiceNo.Length > 0 ? $"DEFERRED VAT {invoiceNo}" : "DEFERRED VAT";
        return text.Length <= 25 ? text : text[..25];
    }

    private static Dictionary<string, object?> Row(
        string account, string drCr, double amount, string taxCode, string assignment, string text) =>
        new()
        {
            ["glAccount"] = account,
            ["drCr"] = drCr,
            ["amount"] = amount,
            ["taxCode"] = taxCode,
            ["assignment"] = assignment,
            ["itemText"] = text,
            ["costCenter"] = "",
            ["vendorCode"] = "",
        };

    // "PROMPT LOGISTICS CO.,LTD" -> "PROMPT", as Finance writes it in the item text.
    private static string ShortName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "";
        var cut = n.IndexOfAny([' ', ',']);
        return (cut > 0 ? n[..cut] : n).Trim().ToUpperInvariant();
    }

    private static List<Dictionary<string, object?>> ReadRows(Dictionary<string, object?> header, string key) =>
        header.Get(key) is System.Collections.IEnumerable rows and not string
            ? rows.OfType<Dictionary<string, object?>>().ToList()
            : [];

    private static double Num(object? v) =>
        v is null ? 0 : double.TryParse(v.ToString(), out var d) ? d : 0;
}

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
    /// carry ("PROMPT,INV.PI-2608010"), that invoice's number, and its VAT amount.</summary>
    public static List<(string Text, string Assignment, double Amount)> Pairs(
        Dictionary<string, object?> doc)
    {
        var pairs = new List<(string, string, double)>();
        foreach (var t in TaxRows(doc))
        {
            var amount = Num(t.Get("docCurrencyAmt"));
            if (amount == 0) continue;
            var code = t.GetStr("taxCode").Trim();
            var kind = t.GetStr("taxKind").Trim();
            var deferred = kind.Length > 0
                ? string.Equals(kind, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                : code.StartsWith('D') || code.StartsWith('d');
            if (!deferred) continue;

            var issuer = ShortName(t.GetStr("issuerName"));
            var docNo = t.GetStr("taxDocNo").Trim();
            var text = issuer.Length > 0 && docNo.Length > 0 ? $"{issuer},INV.{docNo}"
                : issuer.Length > 0 ? issuer
                : docNo;
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

    /// <summary>The voucher's own reference line: what this entry is and which invoice it belongs
    /// to, within the 25 characters SAP's header text allows.</summary>
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

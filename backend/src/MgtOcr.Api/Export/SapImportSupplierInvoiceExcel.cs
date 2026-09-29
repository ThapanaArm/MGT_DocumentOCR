using ClosedXML.Excel;
using MgtOcr.Core;

namespace MgtOcr.Api.Export;

/// <summary>
/// Fills the workbook the SAP Fiori app <b>Import Supplier Invoices</b> accepts.
///
/// This does NOT rebuild the template — it opens the real one the app hands out under
/// "Download Template" (Templates/SupplierInvoice_EN.xlsx, English) and writes the data rows into
/// it. The app reads by column POSITION and the file carries structure that is easy to get wrong
/// and impossible to verify from the outside: a hidden row 5 of technical field names
/// (COMPANYCODE, INVOICINGPARTY, …), 90 hidden columns between the visible ones, the two section
/// bands on row 4, and the label row's exact heights and fills. Starting from the file itself
/// means none of that can drift — an earlier version of this class recreated the sheet by hand and
/// put the header block two rows too high.
///
///   row 1   "Import Supplier Invoices" + the note about unhiding columns
///   row 2   "Last Updated:"
///   row 4   section bands — Header Data (B, blue) / G/L Account Items (BT, amber)
///   row 5   technical field names (hidden)
///   row 6   the column labels
///   row 7+  the data — one row per G/L account item, Header Data repeated on each
///
/// Columns written (the template's own letters; everything else is left exactly as it ships):
///   Header    A Invoice ID · B Company Code · C Transaction · D Invoicing Party · E Reference
///             F Document Date · G Posting Date · H Document Type · I Header Text · J Currency
///             K Gross Amount · L Business Place · M Payment Block · N Baseline Date
///             P Payment Method · U Payment Terms · AF Assignment · AP Tax Determination Date
///             AQ House Bank · AR House Bank Account · AW Tax Reporting Date · AX Tax Fulfillment Date
///   G/L item  BT Company Code · BU Account · BV Item Text · BW Debit/Credit · BX Amount
///             BY Tax Code · CA Assignment · CB Cost Center · CC Profit Center · CE WBS Element
///
/// All rows carry the same Invoice ID, which is how the app groups them back into one invoice.
/// The vendor's credit line is not written: the app posts it itself from Invoicing Party (D) and
/// Gross Invoice Amount (K).
///
/// Note on AC: the manual calls it "Tax Code", but the technical name on row 5 is
/// UNPLANNEDDELIVERYCOSTTAXCODE — it belongs to the unplanned delivery cost in AB, not to the
/// document. It is left empty; the real tax code per line goes in BY.
///
/// This is the "review it first" half of the two routes the user asked for, the other being a
/// direct post through API_SUPPLIERINVOICE_PROCESS_SRV. Same data either way.
/// </summary>
public static class SapImportSupplierInvoiceExcel
{
    private const string SheetName = "Data";
    private const int FirstDataRow = 7;

    private const string TemplateFile = "SupplierInvoice_EN.xlsx";

    /// <summary>The template as downloaded from the app. The build copies it next to the binary
    /// (see the None/CopyToOutputDirectory entry in MgtOcr.Api.csproj); the source tree is checked
    /// too so a run started before that copy happened still works.</summary>
    private static string? FindTemplate()
    {
        foreach (var dir in TemplateSearchPath())
        {
            var path = Path.Combine(dir, "Export", "Templates", TemplateFile);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static IEnumerable<string> TemplateSearchPath()
    {
        yield return AppContext.BaseDirectory;
        // bin/Debug/net10.0 -> the project folder, for a stale or partial build.
        var up = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        yield return up;
    }

    // ---- Header Data -------------------------------------------------------------------------
    private const int C_InvoiceId = 1;      // A
    private const int C_CompanyCode = 2;    // B
    private const int C_Transaction = 3;    // C
    private const int C_Party = 4;          // D
    private const int C_Reference = 5;      // E
    private const int C_DocDate = 6;        // F
    private const int C_PostingDate = 7;    // G
    private const int C_DocType = 8;        // H
    private const int C_HeaderText = 9;     // I
    private const int C_Currency = 10;      // J
    private const int C_GrossAmount = 11;   // K
    private const int C_BusinessPlace = 12; // L
    private const int C_PaymentBlock = 13;  // M
    private const int C_BaselineDate = 14;  // N
    private const int C_PaymentMethod = 16; // P
    private const int C_PaymentTerms = 21;  // U
    private const int C_Assignment = 32;    // AF
    private const int C_TaxDate = 42;       // AP
    private const int C_HouseBank = 43;     // AQ
    private const int C_BankAccountId = 44; // AR
    private const int C_TaxReportingDate = 49; // AW
    private const int C_TaxFulfillDate = 50;   // AX

    // ---- G/L Account Items -------------------------------------------------------------------
    private const int C_ItemCompanyCode = 72;  // BT
    private const int C_ItemAccount = 73;      // BU
    private const int C_ItemText = 74;         // BV
    private const int C_ItemDrCr = 75;         // BW
    private const int C_ItemAmount = 76;       // BX
    private const int C_ItemTaxCode = 77;      // BY
    private const int C_ItemAssignment = 79;   // CA
    private const int C_ItemCostCenter = 80;   // CB
    private const int C_ItemProfitCenter = 81; // CC
    private const int C_ItemWbs = 83;          // CE

    public static byte[] Build(
        Dictionary<string, object?> doc,
        string companyCode,
        PaymentTerms paymentTerms)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var glItems = ReadRows(header, "glItems");
        // A PO-referenced invoice (MIRO) never fills the G/L Account Items table on screen — that
        // table is seeded for FB60 only — so the file used to come out with headers and nothing
        // under them. Fall back to the document's own rows, by the same rule the screen uses.
        if (glItems.Count == 0) glItems = ItemsFromLines(doc);

        // partnerCode is the SAP vendor the mapping step matched; the header copy is the fallback
        // for a document mapped by hand.
        var docParty = doc.GetStr("partnerCode") is { Length: > 0 } p ? p : header.GetStr("vendorCode");
        var headerTaxCode = Cut(header.GetStr("taxCode"), 2);

        // One SAP supplier invoice has exactly one invoicing party, but a shipping bundle routinely
        // carries costs from several suppliers — the screen's "Vendor in this document" filter
        // exists for that very reason. Writing them as one invoice produced a file with no
        // invoicing party at all and two suppliers' costs mixed together, which SAP rejected.
        // So the rows are grouped by supplier, and the template's own Invoice ID column separates
        // them: the Import app treats each distinct Invoice ID as its own invoice, so one file
        // still carries the whole bundle — as several invoices rather than one impossible one.
        var groups = GroupByVendor(glItems, docParty);

        var templatePath = FindTemplate()
            ?? throw new HttpApiException(500,
                $"The SAP import template {TemplateFile} was not found. Looked under: " +
                string.Join(" ; ", TemplateSearchPath().Select(d => Path.Combine(d, "Export", "Templates"))) +
                ". Rebuild the API so the template is copied to the output folder.");

        using var wb = new XLWorkbook(templatePath);
        var ws = wb.Worksheet(SheetName);

        var row = FirstDataRow;
        var invoiceId = 0;
        foreach (var (party, items) in groups)
        {
            // Invoice ID only has to tell one invoice in this file from another — it is a running
            // number, exactly as the manual's sample data uses it, not a key that means anything
            // outside the file. It stays a plain integer: a composite like "703-4000357" was
            // rejected by the app at upload, before any field was even validated. The document is
            // still identifiable from Reference (the supplier's invoice number).
            invoiceId++;
            var gross = GrossAmount(items, headerTaxCode);

            foreach (var g in items)
            {
                var amount = Num(g.Get("amount"));
                if (amount == 0) continue;

                // --- Header Data, repeated on every row ---------------------------------------------
                Code(ws.Cell(row, C_InvoiceId), invoiceId.ToString());
                Code(ws.Cell(row, C_CompanyCode), Cut(companyCode, 4));
                // 1 = Invoice, 2 = Credit Memo.
                Code(ws.Cell(row, C_Transaction), IsCreditMemo(header) ? "2" : "1");
                Code(ws.Cell(row, C_Party), Cut(party, 10));
                Text(ws.Cell(row, C_Reference), Cut(header.GetStr("invoiceNo"), 16));
                Date(ws.Cell(row, C_DocDate), header.GetStr("invoiceDate"));
                Date(ws.Cell(row, C_PostingDate), header.GetStr("postingDate"));
                Text(ws.Cell(row, C_DocType), Cut(Or(header.GetStr("sapDocType"), "RE"), 2));
                Text(ws.Cell(row, C_HeaderText), Cut(header.GetStr("headerText"), 25));
                Text(ws.Cell(row, C_Currency), Cut(Or(header.GetStr("currency"), "THB"), 5));
                Money(ws.Cell(row, C_GrossAmount), gross);
                // Business Place is required for Thai tax reporting and the upload fails without
                // it. Both company codes have head office only, so an empty field is filled with
                // it rather than sent blank for SAP to reject.
                Code(ws.Cell(row, C_BusinessPlace), Cut(Or(header.GetStr("businessPlace"), HeadOfficeBusinessPlace), 4));
                Text(ws.Cell(row, C_PaymentBlock), Cut(header.GetStr("paymentBlock"), 1));
                Date(ws.Cell(row, C_BaselineDate), header.GetStr("baselineDate"));
                Text(ws.Cell(row, C_PaymentMethod), Cut(header.GetStr("paymentMethod"), 1));
                // Payment Terms is a configured SAP key (0001, N030), not free text. The document
                // says things like "30 days", and cutting that to 4 characters produced "30 d" —
                // a value that means nothing to SAP and only fails at posting. Write it only when
                // it already looks like a key, and leave the field for a person otherwise.
                Code(ws.Cell(row, C_PaymentTerms), paymentTerms.Resolve(header.GetStr("paymentTerms")));
                Text(ws.Cell(row, C_Assignment), Cut(header.GetStr("assignmentText"), 18));
                Date(ws.Cell(row, C_TaxDate), Or(header.GetStr("taxDate"), header.GetStr("postingDate")));
                Code(ws.Cell(row, C_HouseBank), Cut(header.GetStr("houseBank"), 5));
                Code(ws.Cell(row, C_BankAccountId), Cut(header.GetStr("bankAccountId"), 5));
                Date(ws.Cell(row, C_TaxReportingDate), header.GetStr("taxReportingDate"));
                Date(ws.Cell(row, C_TaxFulfillDate), header.GetStr("taxFulfillDate"));

                // --- G/L Account Items ---------------------------------------------------------------
                Code(ws.Cell(row, C_ItemCompanyCode), Cut(companyCode, 4));
                Code(ws.Cell(row, C_ItemAccount), Cut(g.GetStr("glAccount"), 10));
                Text(ws.Cell(row, C_ItemText), Cut(g.GetStr("itemText"), 50));
                // GlItemsTable stores D = debit, C = credit; SAP wants S = debit, H = credit.
                Text(ws.Cell(row, C_ItemDrCr),
                    string.Equals(g.GetStr("drCr"), "C", StringComparison.OrdinalIgnoreCase) ? "H" : "S");
                Money(ws.Cell(row, C_ItemAmount), amount);
                // The line's own code when the screen has one, else the document-level default.
                Text(ws.Cell(row, C_ItemTaxCode), Or(Cut(g.GetStr("taxCode"), 2), headerTaxCode));
                Text(ws.Cell(row, C_ItemAssignment), Cut(g.GetStr("assignment"), 18));
                Code(ws.Cell(row, C_ItemCostCenter), Cut(g.GetStr("costCenter"), 10));
                Code(ws.Cell(row, C_ItemProfitCenter), Cut(g.GetStr("profitCenter"), 10));
                Text(ws.Cell(row, C_ItemWbs), Cut(g.GetStr("wbsElement"), 24));

                row++;
            }
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // The G/L accounts Finance posts these documents to (company 2000), the same ones the screen
    // seeds — kept in step with webfront/src/constants/fields.ts. Duty and withholding tax have no
    // account confirmed for this company yet, so those rows go out with the account blank for a
    // person to fill rather than with a guessed number.
    private const string GlFreightHandling = "21229108";   // Local Hand & Freight
    private const string GlInputTax = "11720000";          // Input VAT, claimable this period
    private const string GlDeferredInputTax = "11730000";  // Deferred input VAT (ภาษีซื้อรอเรียกเก็บ)
    private const string GlDuty = "";
    private const string GlWithholdingTax = "";

    /// <summary>The G/L rows for a document whose table on screen was never filled in — the same
    /// rows seedGlItems() builds on the client, so the file matches what a person would have seen.
    ///
    /// Three kinds of row, and the split matters: the costs, each exempt (VX) because the invoice's
    /// real VAT is NOT seven percent of every cost line; then one row per tax invoice from the Tax
    /// tab, carrying that invoice's own code (V1 claimable, D1 deferred) and its number; then the
    /// withholding credit. Coding the cost rows V1 instead — which is what this method used to do —
    /// made SAP calculate 962.62 of VAT on 13,751.73 of costs when the tax invoices in the bundle
    /// add up to 745.62, and left the document out of balance by the difference.</summary>
    private static List<Dictionary<string, object?>> ItemsFromLines(Dictionary<string, object?> doc)
    {
        var items = new List<Dictionary<string, object?>>();
        var header = doc.Get("header") as Dictionary<string, object?> ?? new Dictionary<string, object?>();
        var poRef = header.GetStr("poRef").Trim();

        if (doc.Get("lines") is System.Collections.IEnumerable lines)
        {
            foreach (var raw in lines)
            {
                if (raw is not Dictionary<string, object?> l) continue;
                var ext = l.GetStr("extCode").Trim();
                // VAT and withholding come back below from the tabs, where they have been
                // reviewed — taking them from the raw rows as well would count them twice.
                if (string.Equals(ext, "VAT", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(ext, "WHT", StringComparison.OrdinalIgnoreCase)) continue;
                var amount = Num(l.Get("amount"));
                if (amount == 0) continue;

                var isDuty = string.Equals(ext, "DUTY", StringComparison.OrdinalIgnoreCase)
                    || DutyNames.Any(n => l.GetStr("desc").Trim().Equals(n, StringComparison.OrdinalIgnoreCase));
                items.Add(new Dictionary<string, object?>
                {
                    ["glAccount"] = isDuty ? GlDuty : GlFreightHandling,
                    ["drCr"] = "D",
                    ["amount"] = amount,
                    ["taxCode"] = ExemptTaxCode,
                    ["assignment"] = poRef,
                    ["itemText"] = l.GetStr("desc"),
                    ["costCenter"] = "",
                    // Which supplier this cost belongs to. A shipping bundle carries lines from
                    // several suppliers, and a SAP supplier invoice has exactly one invoicing
                    // party, so this is what splits the file into one invoice per supplier.
                    ["vendorCode"] = VendorOfLine(l),
                });
            }
        }

        // One row per tax invoice. Which account it posts to is decided by the Input Tax Type the
        // person picked in the Tax tab; the code's first letter (D1/D0/D2 are the deferred codes)
        // only stands in for a row nobody has touched yet.
        foreach (var t in ReadRows(header, "taxItems"))
        {
            var amount = Num(t.Get("docCurrencyAmt"));
            if (amount == 0) continue;
            var code = t.GetStr("taxCode").Trim();
            var kind = t.GetStr("taxKind").Trim();
            var deferred = kind.Length > 0
                ? string.Equals(kind, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                : code.StartsWith('D') || code.StartsWith('d');
            items.Add(new Dictionary<string, object?>
            {
                ["glAccount"] = deferred ? GlDeferredInputTax : GlInputTax,
                ["drCr"] = "D",
                ["amount"] = amount,
                ["taxCode"] = code.Length > 0 ? code : ExemptTaxCode,
                ["assignment"] = t.GetStr("taxDocNo"),
                ["itemText"] = t.GetStr("issuerName"),
                ["costCenter"] = "",
                ["vendorCode"] = t.GetStr("vendorCode").Trim(),
            });
        }

        // Withholding is deducted from what we pay, so it is the credit side.
        foreach (var w in ReadRows(header, "whtItems"))
        {
            var amount = Num(w.Get("amtFc"));
            if (amount == 0) continue;
            items.Add(new Dictionary<string, object?>
            {
                ["glAccount"] = GlWithholdingTax,
                ["drCr"] = "C",
                ["amount"] = amount,
                ["taxCode"] = ExemptTaxCode,
                ["assignment"] = poRef,
                ["itemText"] = "WHT",
                ["costCenter"] = "",
                ["vendorCode"] = w.GetStr("vendorCode").Trim(),
            });
        }

        return items;
    }

    // Every cost row goes out exempt: the invoice's VAT is not a percentage of the costs, it is
    // whatever the tax invoices in the bundle say, and those are written as their own rows with
    // their own codes. A tax code on a cost row would have SAP calculate a second, different VAT
    // on top.
    private const string ExemptTaxCode = "VX";

    // Head office. Per Finance both company codes (1000 MGT, 2000 GLC) have only this one.
    private const string HeadOfficeBusinessPlace = "0000";

    // Lines that are a tax in their own right rather than something we bought — recognised by name
    // for a document read before the DUTY tag existed.
    private static readonly string[] DutyNames =
    [
        "import duty", "excise tax", "interior tax", "customs duty",
        "\u0e2d\u0e32\u0e01\u0e23\u0e02\u0e32\u0e40\u0e02\u0e49\u0e32",
        "\u0e20\u0e32\u0e29\u0e35\u0e2a\u0e23\u0e23\u0e1e\u0e2a\u0e32\u0e21\u0e34\u0e15",
        "\u0e20\u0e32\u0e29\u0e35\u0e40\u0e01\u0e47\u0e1a\u0e40\u0e1e\u0e34\u0e48\u0e21\u0e40\u0e1e\u0e37\u0e48\u0e2d\u0e21\u0e2b\u0e32\u0e14\u0e44\u0e17\u0e22",
    ];

    /// <summary>The company's payment-terms keys, straight out of dbo.SysDataMapping
    /// (Subject = 'Payment_Terms') — the table IT maintains and the Zoho sync already reads.
    ///
    /// SAP wants the key (5004), while the document says it in words ("30 days"). Truncating the
    /// words to the field length produced "30 d", which means nothing to SAP and only surfaces as
    /// an error at posting time, so the words are looked up here instead.
    ///
    /// The lookup is deliberately strict: an exact text match and nothing else. The list holds
    /// several terms that all start with "30 Days" — 5004 "30 Days", 5104 "30 Days From Invoice
    /// Date", 6003 "30 Days After B/L Date", 6102 "30 Days End of Month", 4003 "PD 30 Days" — and
    /// choosing between them by guesswork would put a wrong due date on a real payment. Anything
    /// that does not match exactly, or matches more than one code, is left empty for a person to
    /// fill in.</summary>
    public sealed class PaymentTerms
    {
        private readonly HashSet<string> codes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> byText = new(StringComparer.OrdinalIgnoreCase);

        public PaymentTerms(IEnumerable<(string Code, string Text)> rows)
        {
            foreach (var (code, text) in rows)
            {
                var c = (code ?? "").Trim();
                if (c.Length == 0) continue;
                codes.Add(c);

                var key = Normalize(text);
                if (key.Length == 0) continue;
                // null marks a text two codes share, so it is never resolved automatically.
                byText[key] = byText.ContainsKey(key) ? null : c;
            }
        }

        public string Resolve(string value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return "";
            if (codes.Contains(v)) return v;                       // already a key
            return byText.GetValueOrDefault(Normalize(v)) ?? "";   // words -> key, or nothing
        }

        /// <summary>Case and spacing do not distinguish one term from another: "30 Days" and
        /// "30  days" are the same term.</summary>
        private static string Normalize(string? text) =>
            string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Item rows split by invoicing party, in the order they first appear so the file
    /// reads like the document. Rows with no supplier of their own (the FB60 G/L table carries
    /// none) fall in behind the document's own matched vendor, which keeps a single-supplier
    /// invoice exactly as it was.</summary>
    private static List<(string Party, List<Dictionary<string, object?>> Items)> GroupByVendor(
        List<Dictionary<string, object?>> items,
        string docParty)
    {
        var order = new List<string>();
        var byParty = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var g in items)
        {
            var party = g.GetStr("vendorCode").Trim();
            if (party.Length == 0) party = docParty;
            if (!byParty.TryGetValue(party, out var list))
            {
                list = [];
                byParty[party] = list;
                order.Add(party);
            }
            list.Add(g);
        }

        return order.Select(p => (p, byParty[p])).ToList();
    }

    /// <summary>The SAP vendor a document line was matched to, out of the line's extra data —
    /// the same place the screen's "Vendor in this document" filter reads.</summary>
    private static string VendorOfLine(Dictionary<string, object?> line) =>
        line.Get("extra") is Dictionary<string, object?> extra ? extra.GetStr("vendorCode").Trim() : "";

    /// <summary>A code that happens to be all digits goes in as a number, the way it would if
    /// someone typed it into the template — otherwise Excel flags every cell with the green
    /// "number stored as text" triangle. A code with a leading zero stays text, because a number
    /// would silently drop it (0105545044654 is a tax ID, not 105545044654).</summary>
    private static void Code(IXLCell cell, string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return;
        if (v.Length <= 15 && v[0] != '0' && v.All(char.IsDigit) && long.TryParse(v, out var n))
            cell.Value = n;
        else Text(cell, v);
    }

    private static void Text(IXLCell cell, string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return;
        cell.Style.NumberFormat.Format = "@";
        cell.Value = v;
    }

    private static void Date(IXLCell cell, string value)
    {
        if (!DateTime.TryParse(value, out var d)) return;
        cell.Value = d;
        cell.Style.DateFormat.Format = "dd/MM/yyyy";
    }

    private static void Money(IXLCell cell, double value)
    {
        if (value == 0) return;
        cell.Value = value;
    }

    /// <summary>Input-tax rates by SAP tax code, matching the T007A list the screen offers.
    /// V4 is reverse charge: the supplier does not bill that tax, so it adds nothing to what this
    /// invoice pays and counts as 0 here.</summary>
    private static readonly Dictionary<string, double> TaxCodeRate = new(StringComparer.OrdinalIgnoreCase)
    {
        ["V1"] = 7, ["V0"] = 0, ["V2"] = 10, ["V3"] = 0, ["V4"] = 0, ["VX"] = 0,
        ["D1"] = 7, ["D0"] = 0, ["D2"] = 10,
        ["U1"] = 7, ["U2"] = 10, ["N1"] = 7, ["N2"] = 10, ["WP"] = 0,
    };

    /// <summary>Gross Invoice Amount — what the supplier bills, and the figure SAP balances the
    /// document against.
    ///
    /// Two rules, both learned the hard way on the first real import:
    ///
    /// 1. It is derived from the G/L item rows in this very file, never from the header's own
    ///    total. The header total and the line total can disagree (the screen warns when they do),
    ///    and a figure that does not match the lines lands in SAP permanently out of balance.
    ///
    /// 2. The tax is computed from each line's Tax Code, never carried over from the document.
    ///    SAP calculates tax itself from the code — the Tax tab even says so — so a VAT figure
    ///    read off the paper only has to differ by a satang for the invoice to refuse to post.
    ///    The first import sent 244,062.57 against lines worth 122,473.33, because a misread VAT
    ///    had been added on top of a total that already included it.
    ///
    /// So: gross = (debits - credits) + sum(line x its code's rate). A document whose OCR read the
    /// VAT wrongly still produces a postable file; the wrong reading stays a thing to fix on the
    /// screen, which is where it is visible.</summary>
    /// <summary>A row posted to one of the input-tax accounts: the row is the tax itself, not
    /// something taxable.</summary>
    private static bool IsTaxAccountRow(Dictionary<string, object?> g) =>
        g.GetStr("glAccount").Trim() is var a && (a == GlInputTax || a == GlDeferredInputTax);

    private static double GrossAmount(
        List<Dictionary<string, object?>> glItems,
        string headerTaxCode)
    {
        double net = 0, tax = 0;
        foreach (var g in glItems)
        {
            var amount = Num(g.Get("amount"));
            if (amount == 0) continue;

            var signed = string.Equals(g.GetStr("drCr"), "C", StringComparison.OrdinalIgnoreCase)
                ? -amount : amount;
            net += signed;

            // A row that IS the tax carries the tax invoice's own code (V1, D1) so SAP reports it
            // under that code — but its amount is already the tax, so applying the rate to it
            // again would add seven percent of the VAT on top of the VAT.
            if (IsTaxAccountRow(g)) continue;

            var code = Or(Cut(g.GetStr("taxCode"), 2), headerTaxCode);
            if (code.Length > 0 && TaxCodeRate.TryGetValue(code, out var rate) && rate > 0)
                tax += Math.Round(signed * rate / 100, 2);
        }
        return net + tax;
    }

    private static bool IsCreditMemo(Dictionary<string, object?> header)
    {
        if (header.GetStr("transaction").Contains("credit", StringComparison.OrdinalIgnoreCase))
            return true;
        // KG / RS are SAP's credit-memo document types.
        return header.GetStr("sapDocType") is "KG" or "RS";
    }

    private static string Cut(string? v, int n)
    {
        var s = (v ?? "").Trim();
        return s.Length <= n ? s : s[..n];
    }

    private static string Or(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    private static double Num(object? v) =>
        v is not null && double.TryParse(v.ToString(), out var n) ? n : 0;

    private static List<Dictionary<string, object?>> ReadRows(Dictionary<string, object?> header, string key)
    {
        var list = new List<Dictionary<string, object?>>();
        if (header.Get(key) is not System.Collections.IEnumerable rows) return list;
        foreach (var r in rows)
            if (r is Dictionary<string, object?> d) list.Add(d);
        return list;
    }
}

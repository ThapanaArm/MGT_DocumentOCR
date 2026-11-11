using ClosedXML.Excel;
using MgtOcr.Core;

namespace MgtOcr.Api.Export;

/// <summary>
/// The SAP Fiori "Upload General Journal Entry" workbook, used here for one job: moving the VAT of
/// a supplier invoice from DEFERRED input tax to claimable INPUT tax once the supplier's own tax
/// invoice has arrived. The liability itself was already posted by the supplier-invoice file; this
/// is the second half of the same process.
///
/// Laid out exactly like 1.JournalEntry_Accrued.XLSX, which Accounting supplied:
///
///   A5  "Batch ID"                      (left for whoever runs the upload)
///   A8  "1"   B8 "Header"
///   row 9   technical names  BUKRS BLART BLDAT BUDAT MONAT BKTXT WAERS ... BRNCH
///   row 10  the readable labels
///   row 11  the header values            &lt;- written here
///   B13 "Line Items"
///   row 15  technical names  BUKRS HKONT SGTXT WRSOL WRHAB ... ZUONR BUPLA
///   row 16  the readable labels
///   row 17+ one row per posting line     &lt;- written here
///
/// Two lines per tax invoice: the input-tax account debited and the deferred account credited for
/// the same amount, so the entry nets to zero and the VAT lands in the period the tax invoice
/// belongs to. Which accounts and which tax codes those are is Accounting's call — the constants
/// below carry what the posted documents show, and are the only thing to change when they confirm.
/// </summary>
public static class JournalEntryAccruedExcel
{
    // SAP's own template, downloaded from the Upload General Journal Entries app (sheet "Data").
    // The copy Accounting had sent (sheet "Accrued ") was refused by the app with "You've changed
    // the template. Restore the template or download it again" — the app checks the sheet.
    private const string TemplateFile = "JournalEntry_Upload.xlsx";
    private const string SheetName = "Data";
    private const int HeaderRow = 11;
    private const int FirstLineRow = 17;

    // ---- Header (row 9 technical names) ------------------------------------------------------
    private const int H_CompanyCode = 2;    // B  BUKRS
    private const int H_EntryType = 3;      // C  BLART
    private const int H_EntryDate = 4;      // D  BLDAT
    private const int H_PostingDate = 5;    // E  BUDAT
    private const int H_Period = 6;         // F  MONAT
    private const int H_HeaderText = 7;     // G  BKTXT
    private const int H_Currency = 8;       // H  WAERS
    private const int H_Reference = 12;     // L  XBLNR
    private const int H_Branch = 16;        // P  BRNCH

    // ---- Line items (row 15 technical names) -------------------------------------------------
    private const int L_CompanyCode = 2;    // B  BUKRS
    private const int L_Account = 3;        // C  HKONT
    private const int L_ItemText = 4;       // D  SGTXT
    private const int L_Debit = 5;          // E  WRSOL
    private const int L_Credit = 6;         // F  WRHAB
    private const int L_TaxCode = 9;        // I  MWSKZ
    private const int L_CostCenter = 11;    // K  KOSTL
    private const int L_ValueDate = 15;     // O  VALUT
    private const int L_Assignment = 18;    // R  ZUONR
    // Columns S..W are SAP's own (VBUND, SEGMENT, PROF_*). Fields the template does not carry are
    // added after them with their technical names, as the template's note says to.
    private const int L_BusinessPlace = 24; // X  BUPLA
    // A line on a tax account needs its base (F5A375). FWBAS only works together with TXDAT —
    // without the tax date the upload dumps (SAP KBA 3238953).
    private const int L_TaxBase = 25;       // Y  FWBAS
    private const int L_TaxDate = 26;       // Z  TXDAT
    private const int TechRow = 15, LabelRow = 16, HeaderTechRow = 9, HeaderLabelRow = 10;
    // SAP's batch block: "1 | Header" (row 8) down to the line-item labels (row 16).
    private const int BlockFirstRow = 8, LastTemplateColumn = L_TaxDate;

    // Journal entry type for a plain G/L posting, as the sample uses.
    private const string EntryType = "SA";
    private const string HeadOfficeBusinessPlace = "0000";

    /// <summary>Builds the workbook, or returns null when the document has no deferred VAT to
    /// reverse — there is nothing to upload then, and an empty file only confuses.</summary>
    public static byte[]? Build(
        Dictionary<string, object?> doc,
        string companyCode,
        DateTime postingDate)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var rows = BatchRows(doc);
        if (rows.Count == 0) return null;
        return BuildWorkbook(doc, header, rows, companyCode, postingDate);
    }

    /// <summary>One journal entry per deferred tax invoice: Reference = that invoice's number,
    /// Text = its issuer, Assignment = the PO. Shared by the Excel upload file and the direct post
    /// (DocumentsController.PostDeferredVatJv) so the two can never send different entries.</summary>
    public static List<(string Reference, string Text, string Assignment, double Amount, double Base, DateTime? TaxDate)> Batches(
        Dictionary<string, object?> doc)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var reference = Or(header.GetStr("invoiceNo").Trim(), header.GetStr("poRef").Trim());
        return BatchRows(doc)
            .Select(r => (Cut(Or(r.DocNo, reference), 16), r.Text, r.Assignment, r.Amount, r.Base, r.TaxDate))
            .ToList();
    }

    private static List<DeferredRow> BatchRows(Dictionary<string, object?> doc)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var rows = DeferredRows(doc);
        // Item Text = the name of whoever issued THAT tax invoice (AP, 2026-11-11). The Thai input
        // tax report lists this JV, not the supplier invoice, and takes the vendor name per line from
        // here and the tax invoice number from the batch's Reference: JV 100000737 carried PROMPT on
        // all three lines (and one Reference) while two of the receipts were Cargoport's and the
        // Port Authority's, so the report named the wrong vendor. The name is the one in Master 8
        // "Tax Invoice Issuer" (ocr.SupplierTaxMap, swapped in at save), max 50 = SAP's item text.
        // Assignment stays the PO, so the JV still clears against the invoice's D1 lines in
        // 11730000. Reference stays per tax invoice (one batch each).
        var poAssignment = header.GetStr("poRef").Trim();
        rows = rows.Select(r => r with
        {
            // English only (AP, 2026-11-11): a Thai agency name becomes its official English name.
            Text = Cut(MgtOcr.Ocr.VisionPrompt.EnglishIssuerName(r.Text).ToUpperInvariant(), 50),
            Assignment = Or(poAssignment, r.Assignment),
        }).ToList();
        return rows;
    }

    private static byte[] BuildWorkbook(Dictionary<string, object?> doc, Dictionary<string, object?> header,
        List<DeferredRow> rows, string companyCode, DateTime postingDate)
    {

        var templatePath = FindTemplate()
            ?? throw new HttpApiException(500,
                $"The journal-entry template {TemplateFile} was not found. Looked under: " +
                string.Join(" ; ", TemplateSearchPath().Select(d => Path.Combine(d, "Export", "Templates"))) +
                ". Rebuild the API so the template is copied to the output folder.");

        using var wb = new XLWorkbook(templatePath);
        var ws = wb.Worksheet(SheetName);
        // SAP's template ships a second, empty batch as an example (row 19 "2 · Header", its header
        // labels on 20–21, the header values row 22, its line items from row 24). Left in, the
        // upload fails with "Please fill in the header information. (line 22)". This file carries
        // one batch, so everything after its own line-item block goes.
        if (ws.LastRowUsed() is { } last && last.RowNumber() >= FirstLineRow)
            ws.Rows(FirstLineRow, last.RowNumber()).Delete();

        var currency = Or(header.GetStr("currency").Trim(), "THB");
        // The supplier's invoice number identifies the entry, the same way the supplier-invoice
        // file uses it as Reference; the PO number stands in when the read found no invoice number.
        var reference = Or(header.GetStr("invoiceNo").Trim(), header.GetStr("poRef").Trim());

        // Extra columns the template does not carry, named in the FIRST block before it is copied
        // for the next batches.
        Text(ws.Cell(HeaderTechRow, H_Branch), "BRNCH");
        Text(ws.Cell(HeaderLabelRow, H_Branch), "Branch Number (4)");
        foreach (var (col, tech, label) in new[]
                 {
                     (L_BusinessPlace, "BUPLA", "Business Place (4)"),
                     (L_TaxBase, "FWBAS", "Tax Base Amount"),
                     (L_TaxDate, "TXDAT", "Tax Date"),
                 })
        {
            Text(ws.Cell(TechRow, col), tech);
            Text(ws.Cell(LabelRow, col), label);
        }

        // ONE JOURNAL ENTRY PER TAX INVOICE (SRS topic 3: Reference = the template's Invoice No of
        // that row). The Input Tax Report takes "Tax invoice no.", the vendor and the assignment
        // from the document, so three tax invoices in one entry all came out as PI-2608012 /
        // PROMPT. Each batch is SAP's own block (rows 8-16: "n Header", names, labels, values,
        // "Line Items", names, labels) copied below the previous one, then its two lines:
        // Dr 11720000 V1 / Cr 11730000 D1, Item Text = issuer, Assignment = issuer's tax ID.
        var block = ws.Range(BlockFirstRow, 1, LabelRow, LastTemplateColumn);
        var start = BlockFirstRow;
        var batchNo = 0;
        foreach (var r in rows)
        {
            batchNo++;
            if (batchNo > 1)
            {
                block.CopyTo(ws.Cell(start, 1));
                // Copied headers keep only the values the block had; clear the first batch's header
                // values and line data that came along.
                ws.Range(start + (HeaderRow - BlockFirstRow), 1, start + (HeaderRow - BlockFirstRow), LastTemplateColumn).Clear(XLClearOptions.Contents);
            }
            ws.Cell(start, 1).Value = batchNo.ToString();
            var h = start + (HeaderRow - BlockFirstRow);
            Text(ws.Cell(h, H_CompanyCode), Cut(companyCode, 4));
            Text(ws.Cell(h, H_EntryType), EntryType);
            Date(ws.Cell(h, H_EntryDate), postingDate);
            Date(ws.Cell(h, H_PostingDate), postingDate);
            Text(ws.Cell(h, H_Period), postingDate.Month.ToString());
            Text(ws.Cell(h, H_HeaderText), DeferredVatReversal.Reference(header));
            Text(ws.Cell(h, H_Currency), Cut(currency, 5));
            // The tax invoice's own number; the bundle's invoice/PO only when the read has none.
            Text(ws.Cell(h, H_Reference), Cut(Or(r.DocNo, reference), 16));
            Text(ws.Cell(h, H_Branch), HeadOfficeBusinessPlace);

            var line = start + (FirstLineRow - BlockFirstRow);
            var taxDate = r.TaxDate ?? postingDate;
            Line(ws, line++, companyCode, DeferredVatReversal.GlInputTax, r.Text, r.Assignment, r.Amount,
                debit: true, DeferredVatReversal.InputTaxCode, postingDate, r.Base, taxDate);
            Line(ws, line++, companyCode, DeferredVatReversal.GlDeferredInputTax, r.Text, r.Assignment, r.Amount,
                debit: false, DeferredVatReversal.DeferredTaxCode, postingDate, r.Base, taxDate);
            start = line + 1;   // one empty row between batches
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Line(
        IXLWorksheet ws, int row, string companyCode, string account, string text, string assignment,
        double amount, bool debit, string taxCode, DateTime valueDate, double taxBase, DateTime taxDate)
    {
        Text(ws.Cell(row, L_CompanyCode), Cut(companyCode, 4));
        Text(ws.Cell(row, L_Account), Cut(account, 10));
        Text(ws.Cell(row, L_ItemText), Cut(text, 50));
        Money(ws.Cell(row, debit ? L_Debit : L_Credit), amount);
        Text(ws.Cell(row, L_TaxCode), Cut(taxCode, 2));
        Text(ws.Cell(row, L_CostCenter), "");
        Date(ws.Cell(row, L_ValueDate), valueDate);
        Text(ws.Cell(row, L_Assignment), Cut(assignment, 18));
        Text(ws.Cell(row, L_BusinessPlace), HeadOfficeBusinessPlace);
        if (taxBase > 0)
        {
            Money(ws.Cell(row, L_TaxBase), taxBase);
            Date(ws.Cell(row, L_TaxDate), taxDate);
        }
    }

    private sealed record DeferredRow(string Text, string Assignment, double Amount, double Base, DateTime? TaxDate, string DocNo);

    /// <summary>The deferred (D-code) tax rows of the document, one per tax invoice.</summary>
    private static List<DeferredRow> DeferredRows(Dictionary<string, object?> doc)
    {
        var list = new List<DeferredRow>();
        foreach (var t in DeferredVatReversal.EffectiveTaxRows(doc))
        {
            var amount = Num(t.Get("docCurrencyAmt"));
            if (amount == 0) continue;
            var code = t.GetStr("taxCode").Trim();
            var kind = t.GetStr("taxKind").Trim();
            var deferred = kind.Length > 0
                ? string.Equals(kind, "DEFERRED", StringComparison.OrdinalIgnoreCase)
                : code.StartsWith('D') || code.StartsWith('d');
            if (!deferred) continue;
            var issuer = t.GetStr("issuerName").Trim().ToUpperInvariant();
            var docNo = t.GetStr("taxDocNo").Trim();
            var taxId = t.GetStr("issuerTaxId").Trim();
            var baseAmt = Num(t.Get("baseAmount"));
            if (baseAmt <= 0) baseAmt = Math.Round(amount / 0.07, 2);
            DateTime? taxDate = DateTime.TryParse(t.GetStr("taxDocDate").Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
            list.Add(new DeferredRow(
                Text: Or(issuer, docNo),
                Assignment: Or(taxId, docNo),
                Amount: amount, Base: baseAmt, TaxDate: taxDate, DocNo: docNo));
        }
        return list;
    }

    private static void Text(IXLCell cell, string value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0) return;
        cell.Style.NumberFormat.Format = "@";
        cell.Value = v;
    }

    private static void Money(IXLCell cell, double value)
    {
        cell.Value = Math.Round(value, 2);
        cell.Style.NumberFormat.Format = "#,##0.00";
    }

    private static void Date(IXLCell cell, DateTime value)
    {
        cell.Value = value.Date;
        cell.Style.DateFormat.Format = "yyyy-mm-dd";
    }

    private static string Cut(string? v, int n)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(v ?? "", @"\s+", " ").Trim();
        return s.Length <= n ? s : s[..n];
    }

    private static string Or(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

    // "PROMPT LOGISTICS CO.,LTD" -> "PROMPT", the way the invoice's D1 lines write it.
    private static string ShortName(string name)
    {
        var n = (name ?? "").Trim();
        var cut = n.IndexOfAny([' ', ',']);
        return (cut > 0 ? n[..cut] : n).Trim().ToUpperInvariant();
    }

    private static double Num(object? v) =>
        v is null ? 0 : double.TryParse(v.ToString(), out var d) ? d : 0;

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
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    }
}

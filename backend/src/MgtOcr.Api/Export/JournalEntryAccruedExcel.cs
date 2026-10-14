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
    private const string TemplateFile = "JournalEntry_Accrued.xlsx";
    private const string SheetName = "Accrued ";   // the template's own sheet name, trailing space and all
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
    private const int L_BusinessPlace = 19; // S  BUPLA

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
        var rows = DeferredVatReversal.Pairs(doc);
        if (rows.Count == 0) return null;

        var templatePath = FindTemplate()
            ?? throw new HttpApiException(500,
                $"The journal-entry template {TemplateFile} was not found. Looked under: " +
                string.Join(" ; ", TemplateSearchPath().Select(d => Path.Combine(d, "Export", "Templates"))) +
                ". Rebuild the API so the template is copied to the output folder.");

        using var wb = new XLWorkbook(templatePath);
        var ws = wb.Worksheet(SheetName);

        var currency = Or(header.GetStr("currency").Trim(), "THB");
        // The supplier's invoice number identifies the entry, the same way the supplier-invoice
        // file uses it as Reference; the PO number stands in when the read found no invoice number.
        var reference = Or(header.GetStr("invoiceNo").Trim(), header.GetStr("poRef").Trim());

        Text(ws.Cell(HeaderRow, H_CompanyCode), Cut(companyCode, 4));
        Text(ws.Cell(HeaderRow, H_EntryType), EntryType);
        Date(ws.Cell(HeaderRow, H_EntryDate), postingDate);
        Date(ws.Cell(HeaderRow, H_PostingDate), postingDate);
        Text(ws.Cell(HeaderRow, H_Period), postingDate.Month.ToString());
        Text(ws.Cell(HeaderRow, H_HeaderText), DeferredVatReversal.Reference(header));
        Text(ws.Cell(HeaderRow, H_Currency), Cut(currency, 5));
        Text(ws.Cell(HeaderRow, H_Reference), Cut(reference, 16));
        Text(ws.Cell(HeaderRow, H_Branch), HeadOfficeBusinessPlace);

        var row = FirstLineRow;
        foreach (var (text, assignment, amount) in rows)
        {
            // Debit the claimable account …
            Line(ws, row++, companyCode, DeferredVatReversal.GlInputTax, text, assignment, amount,
                debit: true, DeferredVatReversal.InputTaxCode, postingDate);
            // … and credit the deferred one, clearing what the invoice parked there.
            Line(ws, row++, companyCode, DeferredVatReversal.GlDeferredInputTax, text, assignment, amount,
                debit: false, DeferredVatReversal.DeferredTaxCode, postingDate);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Line(
        IXLWorksheet ws, int row, string companyCode, string account, string text, string assignment,
        double amount, bool debit, string taxCode, DateTime valueDate)
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

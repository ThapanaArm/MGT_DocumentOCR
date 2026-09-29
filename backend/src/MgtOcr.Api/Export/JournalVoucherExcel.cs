using ClosedXML.Excel;
using MgtOcr.Core;

namespace MgtOcr.Api.Export;

/// <summary>
/// Builds the "Journal Voucher" workbook Finance uploads into SAP, laid out cell for cell like
/// 2000_Journal Voucher Template.xlsx:
///
///   sheet "Control Sheet"
///     row 1  A: company name                     (centred, bold)
///     row 2  A: "COMPANY CODE : 2000"
///     row 3  A: "JOURNAL VOUCHER CONTROL SHEET"  (underlined across A..F)
///     row 4  A..F: RUNNING | SHEET | SAP DOC. NO. | STATUS | Start Date | Finish Date
///     row 5+ one row per voucher sheet in this file
///
///   sheet "1" (one per voucher)
///     row 1  A: company name
///     row 2  A: "JOURNAL VOUCHER"  B: the running number
///     row 3  A: "Posting Date"     B: dd.MM.yyyy — text, exactly as the template stores it
///     row 4  A: "Reference"        B: the document's reference
///     row 6  A..H: LINE ITEM | G/L ACCOUNT | TAX CODE | DEBIT | CREDIT | ASSIGMENT | TEXT | COST CENTER
///     row 7+ one row per G/L line, then a bold totals row
///
/// The rows come from the G/L Account Items table on screen, so whatever was reviewed there is
/// what lands in the file. SAP DOC. NO. / STATUS / Start / Finish stay empty — they are filled by
/// whoever runs the upload.
/// </summary>
public static class JournalVoucherExcel
{
    private const string Font = "Arial Narrow";
    private const double FontSize = 10;
    private const string MoneyFormat = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)";
    /// <summary>Header fill of the G/L ACCOUNT column, as in the template (indexed colour 45).</summary>
    private const string GlHeaderFill = "#99CCFF";

    private static readonly string[] ControlHeaders =
        ["RUNNING", "SHEET", "SAP DOC. NO.", "STATUS", "Start Date", "Finish Date"];

    private static readonly string[] VoucherHeaders =
        ["LINE ITEM", "G/L ACCOUNT", "TAX CODE", "DEBIT", "CREDIT", "ASSIGMENT", "TEXT", "COST CENTER"];

    public static byte[] Build(
        Dictionary<string, object?> doc,
        string companyName,
        string companyCode,
        long runningNumber)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var glItems = ReadRows(header, "glItems");

        using var wb = new XLWorkbook();
        BuildControlSheet(wb, companyName, companyCode, runningNumber);
        BuildVoucherSheet(wb, header, companyName, runningNumber, glItems);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildControlSheet(XLWorkbook wb, string companyName, string companyCode, long running)
    {
        var ws = wb.Worksheets.Add("Control Sheet");
        ws.Style.Font.FontName = Font;
        ws.Style.Font.FontSize = FontSize;

        Title(ws.Cell(1, 1), companyName);
        Title(ws.Cell(2, 1), $"COMPANY CODE : {companyCode}");
        Title(ws.Cell(3, 1), "JOURNAL VOUCHER CONTROL SHEET");
        // The template underlines the whole title band, not just the cell with the text in it.
        for (var c = 1; c <= ControlHeaders.Length; c++)
            ws.Cell(3, c).Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        for (var c = 0; c < ControlHeaders.Length; c++)
        {
            var cell = ws.Cell(4, c + 1);
            cell.Value = ControlHeaders[c];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        }

        // One voucher per file for now — one row, the running number and sheet 1.
        ws.Cell(5, 1).Value = running;
        ws.Cell(5, 2).Value = 1;
        for (var c = 1; c <= ControlHeaders.Length; c++)
        {
            ws.Cell(5, c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(5, c).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            ws.Cell(5, c).Style.Border.BottomBorder = XLBorderStyleValues.Dotted;
        }

        ws.Column(1).Width = 14.14;
        ws.Column(3).Width = 21.29;
        ws.Column(5).Width = 18.14;
        ws.Column(7).Width = 8.71;
    }

    private static void BuildVoucherSheet(
        XLWorkbook wb,
        Dictionary<string, object?> header,
        string companyName,
        long running,
        List<Dictionary<string, object?>> glItems)
    {
        var ws = wb.Worksheets.Add("1");
        ws.Style.Font.FontName = Font;
        ws.Style.Font.FontSize = FontSize;

        Title(ws.Cell(1, 1), companyName, centred: false);
        Title(ws.Cell(2, 1), "JOURNAL VOUCHER", centred: false);
        Title(ws.Cell(2, 2), running.ToString(), centred: false);
        ws.Cell(2, 2).Value = running;
        ws.Cell(2, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        Title(ws.Cell(3, 1), "Posting Date", centred: false);
        // Text, dd.MM.yyyy — the template stores this as a string, not a date.
        Title(ws.Cell(3, 2), FormatDate(header.GetStr("postingDate")), centred: false);
        ws.Cell(3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        Title(ws.Cell(4, 1), "Reference", centred: false);
        Title(ws.Cell(4, 2), header.GetStr("invoiceNo"), centred: false);
        ws.Cell(4, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 6;
        for (var c = 0; c < VoucherHeaders.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = VoucherHeaders[c];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            Box(cell);
        }
        ws.Cell(headerRow, 2).Style.Fill.BackgroundColor = XLColor.FromHtml(GlHeaderFill);

        var row = headerRow + 1;
        var lineNo = 1;
        double totalDebit = 0, totalCredit = 0;
        foreach (var g in glItems)
        {
            var amount = Num(g.Get("amount"));
            if (amount == 0) continue;
            // GlItemsTable stores D = S-Debit, C = H-Credit.
            var isDebit = !string.Equals(g.GetStr("drCr"), "C", StringComparison.OrdinalIgnoreCase);

            var no = ws.Cell(row, 1);
            no.Value = lineNo++;
            no.Style.Font.Bold = true;
            no.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var gl = ws.Cell(row, 2);
            if (long.TryParse(g.GetStr("glAccount"), out var glNo)) gl.Value = glNo;
            else gl.Value = g.GetStr("glAccount");
            gl.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            var tax = ws.Cell(row, 3);
            tax.Value = g.GetStr("taxCode");
            tax.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            Money(ws.Cell(row, 4), isDebit ? amount : (double?)null);
            Money(ws.Cell(row, 5), isDebit ? (double?)null : amount);
            if (isDebit) totalDebit += amount; else totalCredit += amount;

            ws.Cell(row, 6).Value = g.GetStr("assignment");
            ws.Cell(row, 7).Value = g.GetStr("itemText");
            var cc = ws.Cell(row, 8);
            cc.Value = g.GetStr("costCenter");
            cc.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            for (var c = 1; c <= VoucherHeaders.Length; c++) Box(ws.Cell(row, c));
            row++;
        }

        // Totals row, bold, same box — the template's way of showing the voucher balances.
        Money(ws.Cell(row, 4), totalDebit);
        Money(ws.Cell(row, 5), totalCredit);
        ws.Cell(row, 4).Style.Font.Bold = true;
        ws.Cell(row, 5).Style.Font.Bold = true;
        for (var c = 1; c <= VoucherHeaders.Length; c++) Box(ws.Cell(row, c));

        ws.Column(1).Width = 16.14;
        ws.Column(2).Width = 18.71;
        ws.Column(3).Width = 6.14;
        ws.Column(4).Width = 10.14;
        ws.Column(5).Width = 10.43;
        ws.Column(6).Width = 20.71;
        ws.Column(7).Width = 22.57;
        ws.Column(8).Width = 9.86;
    }

    private static void Title(IXLCell cell, string text, bool centred = true)
    {
        cell.Value = text ?? "";
        cell.Style.Font.Bold = true;
        if (centred) cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void Box(IXLCell cell)
    {
        cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.RightBorder = XLBorderStyleValues.Thin;
    }

    private static void Money(IXLCell cell, double? value)
    {
        if (value.HasValue) cell.Value = value.Value;
        cell.Style.NumberFormat.Format = MoneyFormat;
    }

    private static double Num(object? v) =>
        v is not null && double.TryParse(v.ToString(), out var n) ? n : 0;

    /// <summary>yyyy-MM-dd (how the header stores it) -> dd.MM.yyyy (how the template shows it).</summary>
    private static string FormatDate(string value) =>
        DateTime.TryParse(value, out var d) ? d.ToString("dd.MM.yyyy") : value ?? "";

    private static List<Dictionary<string, object?>> ReadRows(Dictionary<string, object?> header, string key)
    {
        var list = new List<Dictionary<string, object?>>();
        if (header.Get(key) is not System.Collections.IEnumerable rows) return list;
        foreach (var r in rows)
            if (r is Dictionary<string, object?> d) list.Add(d);
        return list;
    }
}

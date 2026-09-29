using ClosedXML.Excel;
using MgtOcr.Core;

namespace MgtOcr.Api.Export;

/// <summary>
/// Builds the "Input VAT" workbook Finance uploads into SAP. The layout copies
/// 2000_Input Vat Template 1.xlsx cell for cell — Finance's macro/upload reads fixed positions,
/// so the rows below are not "roughly like" the template, they are the template:
///
///   row 1   A: company name
///   row 2   A: "Company Code"   B: the code
///   row 3   (left empty on purpose — the template has a blank row here)
///   row 4   A..P: the 16 column headers, bold + thin box borders, centred
///   row 5+  one row per tax invoice
///
/// Everything is Arial Narrow 10 like the original, and the number formats match too, so a file
/// produced here and one typed by hand look identical.
/// </summary>
public static class InputVatExcel
{
    private const string Font = "Arial Narrow";
    private const double FontSize = 10;
    private const int HeaderRow = 4;
    private const int FirstDataRow = 5;

    private static readonly string[] Headers =
    [
        "Header Text", "Document No.", "Doc. Date", "Posting Date", "Tax ID No.", "Branch",
        "Invoice No", "Item text", "Base  amount", "Input  tax", "Line 1", "Tax Code", "Line 2",
        "Status", "Start Date", "Finish Date",
    ];

    // Column widths straight out of the template (D keeps Excel's default).
    private static readonly (int Col, double Width)[] Widths =
    [
        (1, 12.86), (2, 16.29), (3, 9.57), (5, 15.29), (6, 16.57), (7, 21.29), (8, 54.86),
        (9, 12.14), (10, 12.57), (11, 13.14), (12, 16.57), (13, 13.43), (14, 18.43), (15, 18.43),
    ];

    /// <summary>The template shades the two columns the person fills in by hand — Header Text and
    /// Document No. — in "Orange, Accent 2, Lighter 80%". The header row itself is left white.</summary>
    private const string ManualEntryFill = "#FBE5D6";
    private const int ManualEntryLastColumn = 2;  // A (Header Text) and B (Document No.)

    /// <summary>AutoFilter range of the template stops at column M (Line 2), not at P.</summary>
    private const int FilterLastColumn = 13;

    private const string DateFormat = "mm-dd-yy";
    private const string MoneyFormat = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)";

    /// <summary>G/L accounts written into the "Line 1" / "Line 2" columns. Finance has not yet
    /// confirmed what they mean, so they are configuration (Export:InputVat:*) with the values
    /// from the sample file as defaults — no code change needed once they answer.</summary>
    public record Accounts(string Line1, string Line2);

    public static byte[] Build(
        Dictionary<string, object?> doc,
        string companyName,
        string companyCode,
        Accounts accounts)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        var taxItems = ReadTaxItems(header);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Vat Report");
        ws.Style.Font.FontName = Font;
        ws.Style.Font.FontSize = FontSize;

        ws.Cell(1, 1).Value = companyName;
        ws.Cell(1, 1).Style.Font.Bold = true;

        ws.Cell(2, 1).Value = "Company Code";
        ws.Cell(2, 1).Style.Font.Bold = true;
        // The template holds the code as a number, left aligned.
        if (int.TryParse(companyCode, out var cc)) ws.Cell(2, 2).Value = cc;
        else ws.Cell(2, 2).Value = companyCode;
        ws.Cell(2, 2).Style.Font.Bold = true;
        ws.Cell(2, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        for (var c = 0; c < Headers.Length; c++)
        {
            var cell = ws.Cell(HeaderRow, c + 1);
            cell.Value = Headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            Box(cell);
        }

        var row = FirstDataRow;
        foreach (var t in taxItems)
        {
            // A duty row (VX) sits in the same tab on screen but is not a tax invoice.
            if (string.Equals(t.GetStr("taxCode"), "VX", StringComparison.OrdinalIgnoreCase)) continue;

            Text(ws.Cell(row, 1), header.GetStr("headerText"));                    // Header Text
            Text(ws.Cell(row, 2), header.GetStr("sapDocNo"));                      // Document No.
            Date(ws.Cell(row, 3), t.GetStr("taxDocDate"));                         // Doc. Date
            Date(ws.Cell(row, 4), header.GetStr("postingDate"));                   // Posting Date
            // Kept as TEXT, unlike the template's numeric cell: a Thai tax ID often starts with 0
            // (0105528037122) and a number would silently drop it.
            Centred(ws.Cell(row, 5), Digits(t.GetStr("issuerTaxId")));            // Tax ID No.
            Centred(ws.Cell(row, 6), Branch(t.GetStr("issuerBranch")));            // Branch
            var inv = ws.Cell(row, 7);                                             // Invoice No
            Text(inv, t.GetStr("taxDocNo"));
            inv.Style.NumberFormat.Format = "@";
            Text(ws.Cell(row, 8), t.GetStr("issuerName"));                         // Item text
            Money(ws.Cell(row, 9), t.Get("baseAmount"));                           // Base amount
            Money(ws.Cell(row, 10), t.Get("docCurrencyAmt"));                      // Input tax
            Number(ws.Cell(row, 11), accounts.Line1);                              // Line 1
            Centred(ws.Cell(row, 12), t.GetStr("taxCode"));                        // Tax Code
            Number(ws.Cell(row, 13), accounts.Line2);                              // Line 2
            // Status / Start Date / Finish Date stay empty — filled by whoever runs the upload.
            for (var c = 1; c <= Headers.Length; c++) Box(ws.Cell(row, c));
            // Header Text / Document No. are the two columns nobody can fill from the document —
            // the SAP document number only exists after posting — so the template shades them to
            // show they are for hand entry. Same shading here.
            for (var c = 1; c <= ManualEntryLastColumn; c++)
                ws.Cell(row, c).Style.Fill.BackgroundColor = XLColor.FromHtml(ManualEntryFill);
            row++;
        }

        // Same filter the template ships with, over the rows actually written.
        if (row > FirstDataRow)
            ws.Range(HeaderRow, 1, row - 1, FilterLastColumn).SetAutoFilter();

        foreach (var (col, width) in Widths) ws.Column(col).Width = width;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void Box(IXLCell cell)
    {
        cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
        cell.Style.Border.RightBorder = XLBorderStyleValues.Thin;
    }

    private static void Text(IXLCell cell, string? value) => cell.Value = value ?? "";

    private static void Centred(IXLCell cell, string? value)
    {
        cell.Value = value ?? "";
        cell.Style.NumberFormat.Format = "@";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void Number(IXLCell cell, string? value)
    {
        if (long.TryParse(value, out var n)) cell.Value = n;
        else cell.Value = value ?? "";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void Date(IXLCell cell, string? value)
    {
        if (DateTime.TryParse(value, out var d))
        {
            cell.Value = d;
            cell.Style.DateFormat.Format = DateFormat;
        }
        else cell.Value = value ?? "";
        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void Money(IXLCell cell, object? value)
    {
        cell.Value = value is not null && double.TryParse(value.ToString(), out var n) ? n : 0d;
        cell.Style.NumberFormat.Format = MoneyFormat;
    }

    private static List<Dictionary<string, object?>> ReadTaxItems(Dictionary<string, object?> header)
    {
        var list = new List<Dictionary<string, object?>>();
        if (header.Get("taxItems") is not System.Collections.IEnumerable rows) return list;
        foreach (var r in rows)
            if (r is Dictionary<string, object?> d) list.Add(d);
        return list;
    }

    /// <summary>Digits only — whatever the document gave, with dashes and spaces stripped.</summary>
    private static string Digits(string value) =>
        new((value ?? "").Where(char.IsDigit).ToArray());

    // Four digits, head office = 0000. Five is wrong and gets trimmed to its last four.
    private static string Branch(string value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return "0000";
        return digits.Length >= 4 ? digits[^4..] : digits.PadLeft(4, '0');
    }
}

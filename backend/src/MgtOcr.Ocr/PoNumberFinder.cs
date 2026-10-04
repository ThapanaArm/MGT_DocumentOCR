using System.Text.RegularExpressions;

namespace MgtOcr.Ocr;

/// <summary>Finds OUR purchase-order number in a file's text layer, as a safety net for the read.
///
/// Routing to Supplier Invoice (MIRO) or Incoming Invoice (FB60) turns entirely on whether the
/// document references a PO, and a supplier's invoice often does not print one — the PO travels as
/// a separate page attached behind it (our own PURCHASE ORDER sheet). A vision read that only looks
/// at the invoice page returns an empty poRef and the document is filed as "without PO" even though
/// the PO is right there in the file: that is how the V-WISE bundle (PO 2110000036, printed on page
/// 2) came out as an Incoming Invoice.
///
/// So after the read, if poRef is still empty, the text layer is searched for a PO number printed
/// next to a PO label. A bare ten-digit number is never accepted — an invoice number, a tax id or a
/// phone number would match — and the number must also start with one of SAP's purchasing document
/// ranges. Finding nothing leaves poRef empty and the routing unchanged.</summary>
public static partial class PoNumberFinder
{
    // SAP purchasing-document number ranges in use here. 21xxxxxxxx is what Megachem's own POs
    // carry (seen on the V-WISE PO and the one demonstrated in the OCR workshop); the rest are the
    // standard ranges, kept so a document from another range is still recognised.
    private static readonly string[] Prefixes = ["21", "41", "45", "46"];

    [GeneratedRegex(
        @"(?:P\s*[/.]?\s*O\.?\s*(?:Number|No\.?|#)?|Purchase\s*Order\s*(?:Number|No\.?|#)?|เลขที่ใบสั่งซื้อ|ใบสั่งซื้อเลขที่)\s*[:：\-]?\s*(\d{10})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelledPo();

    /// <summary>The first PO number printed next to a PO label, or "" when the text has none.</summary>
    public static string Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        foreach (Match m in LabelledPo().Matches(text))
        {
            var value = m.Groups[1].Value;
            if (Array.Exists(Prefixes, p => value.StartsWith(p, StringComparison.Ordinal)))
                return value;
        }
        return "";
    }
}

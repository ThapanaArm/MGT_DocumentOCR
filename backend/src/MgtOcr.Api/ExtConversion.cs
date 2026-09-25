using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MgtOcr.Ocr;

namespace MgtOcr.Api;

// Bridges MgtOcr.Ocr's typed ParsedDocument/LineItem to the plain Dictionary<string,object?> "ext"
// shape DocumentRepository/MappingEngine work with (matching Python's untyped dict throughout) —
// lives in the API project since MgtOcr.Data intentionally has no reference to MgtOcr.Ocr.
public static partial class ExtConversion
{
    public static Dictionary<string, object?> ToExtDict(ParsedDocument pd) => new()
    {
        ["header"] = pd.Header,
        ["lines"] = pd.Lines.Select(ToLineDict).ToList(),
        ["provider"] = pd.Provider,
        ["confidence"] = pd.Confidence,
        ["confidenceNote"] = pd.ConfidenceNote,
        ["tokensIn"] = pd.TokensIn,
        ["tokensOut"] = pd.TokensOut,
        ["cost"] = pd.Cost,
        ["costIn"] = pd.CostIn,
        ["costOut"] = pd.CostOut,
        ["costCurrency"] = pd.CostCurrency,
        ["rawText"] = pd.RawText,
        ["sampleName"] = pd.SampleName,
        ["_note"] = pd.Note,
    };

    public static Dictionary<string, object?> ToLineDict(LineItem l)
    {
        var d = new Dictionary<string, object?>
        {
            ["extCode"] = l.ExtCode, ["desc"] = l.Desc, ["qty"] = l.Qty, ["uom"] = l.Uom,
            ["price"] = l.Price, ["amount"] = l.Amount,
        };
        // OCR-prefilled Item Note 1 rides in the line's extra bag (same bag the CS-edited
        // salesEmployee/deliveryDate/itemNote1 use); persisted as ExtraJson and read back
        // by SapPayloadBuilder for the to_Text send. Only added when the OCR found a note.
        var extra = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(l.ItemNote)) extra["itemNote1"] = l.ItemNote;
        // Vendor code per line (FORM SHIPPING EXPENSE); read back by the vendor split and shown
        // in the DETAIL table's Vendor column.
        if (!string.IsNullOrWhiteSpace(l.VendorCode)) extra["vendorCode"] = l.VendorCode.Trim();
        // INPUT / DEFERRED for a VAT row — drives the input-VAT report vs ภาษีซื้อรอเรียกเก็บ.
        if (!string.IsNullOrWhiteSpace(l.TaxKind)) extra["taxKind"] = l.TaxKind.Trim();
        // Identity of the tax invoice behind a VAT row — one row per invoice in the Input VAT file
        // Finance sends to SAP, so these travel with the line rather than the document header.
        if (!string.IsNullOrWhiteSpace(l.TaxDocNo)) extra["taxDocNo"] = l.TaxDocNo.Trim();
        if (!string.IsNullOrWhiteSpace(l.TaxDocDate)) extra["taxDocDate"] = l.TaxDocDate.Trim();
        if (!string.IsNullOrWhiteSpace(l.IssuerName)) extra["issuerName"] = l.IssuerName.Trim();
        if (!string.IsNullOrWhiteSpace(l.IssuerTaxId)) extra["issuerTaxId"] = l.IssuerTaxId.Trim();
        if (!string.IsNullOrWhiteSpace(l.IssuerBranch)) extra["issuerBranch"] = l.IssuerBranch.Trim();
        if (l.BaseAmount != 0) extra["baseAmount"] = l.BaseAmount;
        if (extra.Count > 0) d["extra"] = extra;
        return d;
    }

    // safe_name(): mirrors app/main.py's safe_name() — NFC-normalize, replace anything outside
    // word chars/Thai block/dot/dash/space with "_", trim, cap at 120 chars.
    [GeneratedRegex(@"[^\w฀-๿.\- ]")]
    private static partial Regex UnsafeChars();

    public static string SafeName(string? name)
    {
        var n = (name ?? "file").Normalize(NormalizationForm.FormC);
        n = UnsafeChars().Replace(n, "_").Trim();
        if (n.Length == 0) n = "file";
        return n.Length > 120 ? n[..120] : n;
    }
}

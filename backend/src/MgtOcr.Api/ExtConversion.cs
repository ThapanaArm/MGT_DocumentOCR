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

    public static Dictionary<string, object?> ToLineDict(LineItem l) => new()
    {
        ["extCode"] = l.ExtCode, ["desc"] = l.Desc, ["qty"] = l.Qty, ["uom"] = l.Uom,
        ["price"] = l.Price, ["amount"] = l.Amount,
    };

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

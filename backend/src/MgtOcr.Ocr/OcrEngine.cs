using MgtOcr.Core.Config;
using MgtOcr.Ocr.Providers;

namespace MgtOcr.Ocr;

public record OcrProviderInfo(string Id, string Label, string Desc, bool Ready);

// extract(): main entry point. Gemini is the only reading engine (Megachem, 23 Nov 2026 on
// develop-glc, same as develop-mgt 17 Nov) — Tesseract / Typhoon / Azure / Claude / PaddleOCR were
// taken out of the code.
public class OcrEngine(AppConfig config)
{
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp" };

    // The engine list shown in the UI (GET /api/ocr/providers): Gemini only.
    public List<OcrProviderInfo> Providers =>
    [
        new("gemini", "Gemini Vision (AI)",
            "Google's Vision model reads the document image directly and understands context — requires Ocr:GreenleafchemicalGeminiApiKey (billed per call)",
            !string.IsNullOrEmpty(config.GeminiApiKey)),
    ];

    // A real read was attempted but Gemini could not be used — no connection, a bad API key, an
    // HTTP error, an unreadable file, etc. This returns NO fabricated data: an empty document
    // marked Provider="failed" that carries the
    // real reason in Note. The UI then shows the problem plainly (e.g. "Gemini ใช้ไม่ได้ …") instead
    // of silently substituting sample (demo) data the user could mistake for the real document. Per
    // Megachem: on any read failure, surface the error — never quietly fall back to demo.
    private static ParsedDocument FailedResult(string module, string note) => new()
    {
        Provider = "failed",
        Note = note,
        Header = HeaderParser.BlankHeader(module),
        Lines = [],
        Confidence = 0,
    };

    private static readonly Dictionary<string, string> ProviderCaveat = new()
    {
        ["gemini"] = "Read with Gemini Vision from the document image; may misinterpret in some places",
    };

    private static readonly Dictionary<string, string> ApImportant = new()
    {
        ["invoiceNo"] = "Tax Invoice / Invoice No.", ["invoiceDate"] = "Document Date",
        ["vendorName"] = "Vendor Name", ["vendorTaxId"] = "Vendor Tax ID", ["totalAmount"] = "Grand Total",
    };
    private static readonly Dictionary<string, string> SoImportant = new()
    {
        ["poNo"] = "Purchase Order No.", ["poDate"] = "Document Date", ["customerName"] = "Customer Name",
        ["customerTaxId"] = "Customer Tax ID", ["totalAmount"] = "Grand Total",
    };

    // Ported from ocr_engine.py's _confidence_note() (lines 1589-1603).
    private static string ConfidenceNote(string module, Dictionary<string, object?> header, List<LineItem> lines, string provider)
    {
        var important = module == "SO" ? SoImportant : ApImportant;
        var missing = important.Where(kv =>
        {
            var s = (header.TryGetValue(kv.Key, out var v) ? v?.ToString() : "")?.Trim() ?? "";
            return s is "" or "0" or "0.0";
        }).Select(kv => kv.Value).ToList();
        var reasons = new List<string>();
        if (missing.Count > 0) reasons.Add("Missing data: " + string.Join(", ", missing));
        if (lines.Count == 0) reasons.Add("No line items found (Item Detail)");
        if (ProviderCaveat.TryGetValue(provider, out var caveat)) reasons.Add(caveat);
        return string.Join(" / ", reasons);
    }

    // extract(): reads with Gemini, then adds confidenceNote + the cost of every Gemini request made
    // in this read (GeminiRequest scope). providerOverride is kept in the signature so existing callers
    // compile, but it is ignored: every read goes to Gemini.
    /// <param name="salesOrg">Which company's accounting rules to read by — "1000" MGT, "2000" GLC.
    /// Empty reads as GLC (see CompanyRules), so nothing changes for a document with no company.</param>
    public async Task<ParsedDocument> ExtractAsync(string path, string module, string? providerOverride = null, string? password = null, string? salesOrg = null, string? apDocCategory = null)
    {
        PdfPassword.Current = password;
        using var localUsage = GeminiRequest.BeginIfNeeded(module);
        _ = providerOverride;
        var doc = await ReadWithGeminiAsync(path, module, salesOrg ?? "", apDocCategory ?? "");
        if (doc.Provider == "failed" && string.IsNullOrEmpty(doc.Note)) doc.Note = "Failed to read the document";
        // confidenceNote is distinct from Note ("_note" — a fallback/failure explanation): it
        // explains why a normal-but-imperfect read isn't 100% confident.
        doc.ConfidenceNote = doc.Provider == "failed" ? (doc.Note ?? "") : ConfidenceNote(module, doc.Header, doc.Lines, doc.Provider);
        // Multi-PO shipping bundle: whether the forms were read from their text layer (ShippingFormText).
        if (doc.Header.TryGetValue("formTextNote", out var ft) && ft?.ToString() is { Length: > 0 } ftNote)
            doc.ConfidenceNote = doc.ConfidenceNote.Length > 0 ? $"{doc.ConfidenceNote} / {ftNote}" : ftNote;

        if (GeminiRequest.Active is { Requests.Count: > 0 } usage)
        {
            var requests = usage.Requests;
            doc.TokensIn = requests.Any(r => r.Input.HasValue) ? requests.Sum(r => r.Input ?? 0) : null;
            doc.TokensOut = requests.Any(r => r.Output.HasValue) ? requests.Sum(r => (r.Output ?? 0) + (r.Thinking ?? 0)) : null;
            doc.CostIn = requests.All(r => r.InputCost.HasValue) ? requests.Sum(r => r.InputCost) : null;
            doc.CostOut = requests.All(r => r.OutputCost.HasValue) ? requests.Sum(r => r.OutputCost) : null;
            doc.Cost = doc.CostIn + doc.CostOut;
            doc.CostCurrency = doc.Cost.HasValue ? "USD" : null;
        }
        return doc;
    }

    private async Task<ParsedDocument> ReadWithGeminiAsync(string path, string module, string salesOrg, string apDocCategory)
    {
        var (outDoc, gErr) = await GeminiOcr.VisionExtractAsync(path, module, config, salesOrg, apDocCategory);
        if (outDoc != null) return outDoc;
        return FailedResult(module, gErr ?? "Could not connect to Google Gemini Vision");
    }
}

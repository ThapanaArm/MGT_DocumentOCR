using MgtOcr.Core.Config;

using MgtOcr.Ocr.Providers;

namespace MgtOcr.Ocr;

public record OcrProviderInfo(string Id, string Label, string Desc, bool Ready);

// extract(): main entry point. Gemini is the only reading engine (Megachem, 7 Oct 2026; the other
// engines — Tesseract, Typhoon, Azure, Claude, OpenAI, PaddleOCR, text-layer rules, demo — were
// removed from the code on 17 Nov 2026).
public class OcrEngine(AppConfig config)
{
    public const string Gemini = "gemini";

    /// <summary>Image files Gemini can be given directly (everything else must be a PDF).</summary>
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".webp" };

    // The engine list shown in the UI (read-only label) — Gemini only.
    public List<OcrProviderInfo> Providers =>
    [
        new(Gemini, "Gemini Vision (AI)",
            "Google's Vision model reads the document image directly and understands context — requires Ocr:GeminiApiKey (billed per call)",
            !string.IsNullOrEmpty(config.GeminiApiKey)),
    ];

    // A read was attempted but could not be used — no connection, a bad API key, an HTTP error from
    // Gemini, an unreadable file, etc. Returns NO fabricated data: an empty document marked
    // Provider="failed" that carries the real reason in Note, so the UI shows the problem plainly.
    // Per Megachem: on any read failure, surface the error — never quietly fall back to demo data.
    private static ParsedDocument FailedResult(string module, string note) => new()
    {
        Provider = "failed",
        Note = note,
        Header = HeaderParser.BlankHeader(module),
        Lines = [],
        Confidence = 0,
    };

    private const string GeminiCaveat = "Read with Gemini Vision from the document image; may misinterpret in some places";

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
        if (provider == Gemini) reasons.Add(GeminiCaveat);
        return string.Join(" / ", reasons);
    }

    // Gemini token price per 1M tokens (USD).
    private const decimal GeminiPriceIn = 0.75m, GeminiPriceOut = 3.75m;

    /// <param name="providerOverride">Kept for the callers' signature only — every read uses Gemini.</param>
    /// <param name="salesOrg">Which company's accounting rules to read by — "1000" MGT, "2000" GLC.
    /// Empty reads as GLC (see CompanyRules), so nothing changes for a document with no company.</param>
    /// <param name="readHints">Sales Order reading rules (ReadHints) for the Gemini read; null = none.</param>
    public async Task<ParsedDocument> ExtractAsync(string path, string module, string? providerOverride = null, string? password = null, string? salesOrg = null,
        ReadHintRequest? readHints = null)
    {
        PdfPassword.Current = password;
        var doc = await ReadWithGeminiAsync(path, module, salesOrg ?? "", readHints);
        if (doc.Provider == "failed" && string.IsNullOrEmpty(doc.Note)) doc.Note = "Failed to read the document";
        // confidenceNote is distinct from Note ("_note" — a failure explanation): it explains why a
        // normal-but-imperfect read isn't 100% confident.
        doc.ConfidenceNote = doc.Provider == "failed" ? (doc.Note ?? "") : ConfidenceNote(module, doc.Header, doc.Lines, doc.Provider);

        if (doc.Provider == Gemini && doc.TokensIn is { } tin && doc.TokensOut is { } tout)
        {
            var costIn = Math.Round(tin / 1_000_000m * GeminiPriceIn, 4);
            var costOut = Math.Round(tout / 1_000_000m * GeminiPriceOut, 4);
            doc.CostIn = costIn; doc.CostOut = costOut; doc.Cost = Math.Round(costIn + costOut, 4); doc.CostCurrency = "USD";
        }
        return doc;
    }

    private async Task<ParsedDocument> ReadWithGeminiAsync(string path, string module, string salesOrg, ReadHintRequest? readHints)
    {
        var (outDoc, gErr) = await GeminiOcr.VisionExtractAsync(path, module, config, salesOrg, readHints);
        return outDoc ?? FailedResult(module, gErr ?? "Could not connect to Google Gemini Vision");
    }
}

using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

using MgtOcr.Ocr.Providers;

namespace MgtOcr.Ocr;

public record OcrProviderInfo(string Id, string Label, string Desc, bool Ready);

// extract(): main entry point — dispatches to whichever OCR/extraction provider was requested.
public class OcrEngine(AppConfig config)
{
    public static readonly HashSet<string> ImageExt = TesseractOcr.ImageExt;

    // The selectable engine list shown in the UI — id must match a branch in ExtractAsync().
    public List<OcrProviderInfo> Providers =>
    [
        new("auto", "Automatic (recommended)",
            "Reads the file's text layer first; for scanned files it falls back to Tesseract OCR automatically — no cost.", true),
        new("text", "File text only", "Fastest, but does not work with scanned files / images.", true),
        new("tesseract", "Tesseract OCR (local)",
            "Forces OCR even when the file already has a text layer — no cost.", !string.IsNullOrEmpty(config.TesseractCmd)),
        new("typhoon", "Typhoon OCR (Thai-specialized)",
            "SCB 10X's Thai/English OCR model — far more accurate than Tesseract for Thai documents, handwriting and complex tables — " +
            "requires TYPHOON_API_KEY in .env (billed per page, see pricing at opentyphoon.ai)",
            !string.IsNullOrEmpty(config.TyphoonApiKey)),
        new("azure", "Azure Document Intelligence",
            "Much more accurate for forms / tables — requires AZURE_DI_ENDPOINT/AZURE_DI_KEY in .env (billed per page)",
            !string.IsNullOrEmpty(config.AzureDiEndpoint) && !string.IsNullOrEmpty(config.AzureDiKey)),
        new("claude_text", "OCR + Claude structuring (economical)",
            "Reads text with Tesseract/pdfplumber first (free) then sends the text to Claude to structure as JSON — " +
            "much cheaper than Claude Vision, good for repetitive documents (PO/Invoice) whose characters OCR can read reasonably well — " +
            "requires ANTHROPIC_API_KEY in .env (billed per call, but cheaper than Claude Vision)",
            !string.IsNullOrEmpty(config.AnthropicApiKey)),
        new("claude", "Claude Vision (AI)",
            "Most accurate for messy documents / complex tables, understands context — requires ANTHROPIC_API_KEY in .env (billed per call)",
            !string.IsNullOrEmpty(config.AnthropicApiKey)),
        new("gemini", "Gemini Vision (AI)",
            "Google's Vision model reads the document image directly and understands context — requires GEMINI_API_KEY in .env (billed per call)",
            !string.IsNullOrEmpty(config.GeminiApiKey)),
        new("openai", "ChatGPT Vision (AI)",
            "OpenAI's GPT-4o/GPT-5 reads the document image directly and understands context — requires OPENAI_API_KEY in .env (billed per call)",
            !string.IsNullOrEmpty(config.OpenAiApiKey)),
        new("demo", "Sample data (test)", "Does not read a real file; for testing the Mapping / SAP submission steps only.", true),
    ];

    private ParsedDocument DemoFallback(string path, string module, string note)
    {
        var d = DemoData.DemoDoc(module, Math.Abs(Path.GetFileName(path).GetHashCode()) % DemoData.Demo[module].Count);
        d.Provider = "demo";
        d.Note = note;
        return d;
    }

    // A read that did not succeed. Deliberately NOT sample data: substituting a demo document made a
    // failed Gemini/Azure/... call look like a successful read (plausible vendor, amounts, reference)
    // that a user could map and post to SAP without noticing the "demo" badge. A failed read comes
    // back with a blank header, no lines, confidence 0 and provider "failed" — the UI already shows
    // a failure toast and red badge for it, and Note carries the real error so it can be fixed.
    // Sample data is only ever returned when the user explicitly picks the "demo" engine.
    private static ParsedDocument FailedDoc(string module, string note) => new()
    {
        Header = HeaderParser.BlankHeader(module),
        Lines = [],
        Confidence = 0,
        Provider = "failed",
        Note = note,
    };

    private static readonly Dictionary<string, string> ProviderCaveat = new()
    {
        ["ocr"] = "Read with Tesseract OCR from a scanned file, which is less accurate than reading the text layer directly from the source file",
        ["typhoon"] = "Read with Typhoon OCR from the document image; may contain errors from image quality / handwriting",
        ["azure"] = "Read with Azure Document Intelligence from the document image",
        ["claude"] = "Read with Claude Vision from the document image; may misinterpret in some places",
        ["claude_text"] = "OCR reads the text first then Claude structures it; accuracy depends on the text quality from the first OCR pass",
        ["gemini"] = "Read with Gemini Vision from the document image; may misinterpret in some places",
        ["openai"] = "Read with ChatGPT Vision from the document image; may misinterpret in some places",
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

    // Token price per 1M tokens (USD), only for AI/LLM providers that bill per token.
    private static readonly Dictionary<string, (decimal In, decimal Out)> TokenPrice = new()
    {
        ["claude"] = (2.00m, 10.00m), ["claude_text"] = (2.00m, 10.00m),
        ["gemini"] = (0.75m, 3.75m), ["openai"] = (2.50m, 10.00m),
    };

    // extract(): wraps ExtractDispatchAsync to add confidenceNote + estimated cost uniformly for
    // every provider, mirroring ocr_engine.py's extract() (lines 1627-1644) without duplicating
    // this logic into every dispatch branch's return statement.
    public async Task<ParsedDocument> ExtractAsync(string path, string module, string? providerOverride = null, string? password = null)
    {
        PdfPassword.Current = password;
        var doc = await ExtractDispatchAsync(path, module, providerOverride);
        doc.Note = doc.Provider switch
        {
            "demo" => string.IsNullOrEmpty(doc.Note) ? "Using sample data (demo) — did not read a real file" : doc.Note,
            "failed" => string.IsNullOrEmpty(doc.Note) ? "Failed to read the document" : doc.Note,
            _ => doc.Note,
        };
        // confidenceNote is distinct from Note ("_note" — a fallback/failure explanation): it
        // explains why a normal-but-imperfect read isn't 100% confident.
        doc.ConfidenceNote = doc.Provider is "demo" or "failed" ? (doc.Note ?? "") : ConfidenceNote(module, doc.Header, doc.Lines, doc.Provider);

        if (TokenPrice.TryGetValue(doc.Provider, out var price) && doc.TokensIn is { } tin && doc.TokensOut is { } tout)
        {
            var costIn = Math.Round(tin / 1_000_000m * price.In, 4);
            var costOut = Math.Round(tout / 1_000_000m * price.Out, 4);
            doc.CostIn = costIn; doc.CostOut = costOut; doc.Cost = Math.Round(costIn + costOut, 4); doc.CostCurrency = "USD";
        }
        return doc;
    }

    // provider_override: the engine id chosen in the UI — if given, "forces" that provider with
    // no silent fallback to another one. Empty/"auto" uses the normal text->local-OCR chain (does
    // NOT call Azure/Claude/Gemini/OpenAI automatically, since those cost money — must be chosen explicitly).
    private async Task<ParsedDocument> ExtractDispatchAsync(string path, string module, string? providerOverride = null)
    {
        var provider = (providerOverride ?? config.OcrProvider ?? "auto").ToLowerInvariant();
        if (provider == "") provider = "auto";
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (provider == "azure")
        {
            var data = await AzureOcr.ExtractAsync(path, config);
            if (data != null) return AzureOcr.FromAzure(data, module);
            return FailedDoc(module, "Could not connect to Azure Document Intelligence, or AZURE_DI_ENDPOINT/AZURE_DI_KEY is not set in .env");
        }
        if (provider == "claude")
        {
            var outDoc = await ClaudeOcr.VisionExtractAsync(path, module, config);
            if (outDoc != null) return outDoc;
            return FailedDoc(module, "Could not connect to Claude Vision, or ANTHROPIC_API_KEY is not set in .env");
        }
        if (provider == "gemini")
        {
            var (outDoc, gErr) = await GeminiOcr.VisionExtractAsync(path, module, config);
            if (outDoc != null) return outDoc;
            return FailedDoc(module, gErr ?? "Could not connect to Google Gemini Vision");
        }
        if (provider == "openai")
        {
            var (outDoc, oErr) = await OpenAiOcr.VisionExtractAsync(path, module, config);
            if (outDoc != null) return outDoc;
            return FailedDoc(module, oErr ?? "Could not connect to OpenAI Vision");
        }
        if (provider == "claude_text")
        {
            var preText = ext == ".pdf" ? PdfExtraction.PdfText(path) : "";
            if (string.IsNullOrWhiteSpace(preText) && (ImageExt.Contains(ext) || ext == ".pdf"))
                preText = await TesseractOcr.ExtractTextAsync(path, config);
            if (string.IsNullOrWhiteSpace(preText))
                return FailedDoc(module, "OCR could not read text from the file, so it could not be sent to Claude for structuring (try Claude Vision instead)");
            var outDoc = await ClaudeOcr.TextExtractAsync(module, preText, config);
            if (outDoc != null) return outDoc;
            return FailedDoc(module, "Could not connect to Claude (structuring from text), or ANTHROPIC_API_KEY is not set in .env");
        }
        if (provider == "typhoon")
        {
            var (text, err) = await TyphoonOcr.ExtractTextAsync(path, config);
            if (string.IsNullOrWhiteSpace(text))
                return FailedDoc(module, err != "" ? err : "Typhoon OCR returned no text");
            var blocks = ext == ".pdf" ? PdfExtraction.PdfBlocks(path) : null;
            var outDoc = HeaderParser.ParseText(text, module, blocks, "typhoon", config.OwnCompanyKeywords, config.OwnTaxId);
            if (outDoc.Lines.Count > 0 || HasValue(outDoc.Header, "vendorTaxId") || HasValue(outDoc.Header, "customerTaxId"))
                return outDoc;
            outDoc.Confidence = 0.3;
            return outDoc;
        }
        if (provider == "demo") return DemoFallback(path, module, "");

        string mainText = ""; var src = "text";
        if ((provider == "auto" || provider == "text") && ext == ".pdf")
            mainText = PdfExtraction.PdfText(path);
        if (string.IsNullOrWhiteSpace(mainText) && (provider == "auto" || provider == "tesseract") && (ImageExt.Contains(ext) || ext == ".pdf"))
        {
            mainText = await TesseractOcr.ExtractTextAsync(path, config);
            src = "ocr";
        }
        if (!string.IsNullOrWhiteSpace(mainText) && Regex.Replace(mainText, @"\s", "").Length > 40)
        {
            var blocks = ext == ".pdf" ? PdfExtraction.PdfBlocks(path) : null;
            var outDoc = HeaderParser.ParseText(mainText, module, blocks, src, config.OwnCompanyKeywords, config.OwnTaxId);
            if (outDoc.Lines.Count > 0 || HasValue(outDoc.Header, "vendorTaxId") || HasValue(outDoc.Header, "customerTaxId"))
                return outDoc;
            outDoc.Confidence = 0.3;
            return outDoc;
        }

        return FailedDoc(module, "Could not read text from the file (scanned file / no OCR engine configured)");
    }

    private static bool HasValue(Dictionary<string, object?> header, string key) =>
        header.TryGetValue(key, out var v) && !string.IsNullOrEmpty(Convert.ToString(v));
}

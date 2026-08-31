using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr;

public record OcrProviderInfo(string Id, string Label, string Desc, bool Ready);

// extract(): main entry point — dispatches to whichever OCR/extraction provider was requested.
public class OcrEngine(AppConfig config)
{
    public static readonly HashSet<string> ImageExt = TesseractOcr.ImageExt;

    // The selectable engine list shown in the UI — id must match a branch in ExtractAsync().
    public List<OcrProviderInfo> Providers =>
    [
        new("auto", "อัตโนมัติ (แนะนำ)",
            "อ่านข้อความในไฟล์ก่อน ถ้าเป็นไฟล์สแกนจะใช้ Tesseract OCR ให้อัตโนมัติ — ไม่มีค่าใช้จ่าย", true),
        new("text", "ข้อความในไฟล์เท่านั้น", "เร็วที่สุด แต่ใช้ไม่ได้กับไฟล์สแกน/รูปภาพ", true),
        new("tesseract", "Tesseract OCR (ในเครื่อง)",
            "บังคับอ่านด้วย OCR แม้ไฟล์จะมีชั้นข้อความอยู่แล้ว — ไม่มีค่าใช้จ่าย", !string.IsNullOrEmpty(config.TesseractCmd)),
        new("typhoon", "Typhoon OCR (ไทยโดยเฉพาะ)",
            "โมเดล OCR ไทย/อังกฤษของ SCB 10X แม่นกว่า Tesseract มากสำหรับเอกสารไทย ลายมือ/ตารางซับซ้อน — " +
            "ต้องตั้งค่า TYPHOON_API_KEY ใน .env (มีค่าใช้จ่ายต่อหน้า ดูราคาที่ opentyphoon.ai)",
            !string.IsNullOrEmpty(config.TyphoonApiKey)),
        new("azure", "Azure Document Intelligence",
            "แม่นกว่ามากสำหรับฟอร์ม/ตาราง — ต้องตั้งค่า AZURE_DI_ENDPOINT/AZURE_DI_KEY ใน .env (มีค่าใช้จ่ายต่อหน้า)",
            !string.IsNullOrEmpty(config.AzureDiEndpoint) && !string.IsNullOrEmpty(config.AzureDiKey)),
        new("claude_text", "OCR + Claude จัดโครงสร้าง (ประหยัด)",
            "อ่านข้อความด้วย Tesseract/pdfplumber ก่อน (ฟรี) แล้วส่งข้อความให้ Claude จัดเป็น JSON — " +
            "ถูกกว่า Claude Vision มาก เหมาะกับเอกสารซ้ำ ๆ (PO/Invoice) ที่ OCR อ่านตัวอักษรออกมาได้ระดับหนึ่ง " +
            "ต้องตั้งค่า ANTHROPIC_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง แต่ถูกกว่า Claude Vision)",
            !string.IsNullOrEmpty(config.AnthropicApiKey)),
        new("claude", "Claude Vision (AI)",
            "แม่นที่สุดสำหรับเอกสารยุ่งเหยิง/ตารางซับซ้อน เข้าใจบริบทได้ — ต้องตั้งค่า ANTHROPIC_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง)",
            !string.IsNullOrEmpty(config.AnthropicApiKey)),
        new("gemini", "Gemini Vision (AI)",
            "โมเดล Vision ของ Google อ่านภาพเอกสารโดยตรง เข้าใจบริบทได้ — ต้องตั้งค่า GEMINI_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง)",
            !string.IsNullOrEmpty(config.GeminiApiKey)),
        new("openai", "ChatGPT Vision (AI)",
            "โมเดล GPT-4o/GPT-5 ของ OpenAI อ่านภาพเอกสารโดยตรง เข้าใจบริบทได้ — ต้องตั้งค่า OPENAI_API_KEY ใน .env (มีค่าใช้จ่ายต่อครั้ง)",
            !string.IsNullOrEmpty(config.OpenAiApiKey)),
        new("demo", "ข้อมูลตัวอย่าง (ทดสอบ)", "ไม่อ่านไฟล์จริง ใช้สำหรับทดสอบขั้นตอน Mapping/ส่ง SAP เท่านั้น", true),
    ];

    private ParsedDocument DemoFallback(string path, string module, string note)
    {
        var d = DemoData.DemoDoc(module, Math.Abs(Path.GetFileName(path).GetHashCode()) % DemoData.Demo[module].Count);
        d.Provider = "demo";
        d.Note = note;
        return d;
    }

    private static readonly Dictionary<string, string> ProviderCaveat = new()
    {
        ["ocr"] = "อ่านด้วย Tesseract OCR จากไฟล์สแกน ซึ่งแม่นยำต่ำกว่าอ่านข้อความจากไฟล์ต้นฉบับโดยตรง",
        ["typhoon"] = "อ่านด้วย Typhoon OCR จากภาพเอกสาร อาจมีข้อผิดพลาดจากคุณภาพภาพ/ลายมือ",
        ["azure"] = "อ่านด้วย Azure Document Intelligence จากภาพเอกสาร",
        ["claude"] = "อ่านด้วย Claude Vision จากภาพเอกสาร อาจตีความคลาดเคลื่อนได้ในบางจุด",
        ["claude_text"] = "ใช้ OCR อ่านข้อความก่อนแล้วให้ Claude จัดโครงสร้าง ความแม่นยำขึ้นกับคุณภาพข้อความจาก OCR รอบแรก",
        ["gemini"] = "อ่านด้วย Gemini Vision จากภาพเอกสาร อาจตีความคลาดเคลื่อนได้ในบางจุด",
        ["openai"] = "อ่านด้วย ChatGPT Vision จากภาพเอกสาร อาจตีความคลาดเคลื่อนได้ในบางจุด",
    };

    private static readonly Dictionary<string, string> ApImportant = new()
    {
        ["invoiceNo"] = "เลขที่ใบกำกับภาษี/ใบแจ้งหนี้", ["invoiceDate"] = "วันที่เอกสาร",
        ["vendorName"] = "ชื่อผู้ขาย", ["vendorTaxId"] = "เลขทะเบียนผู้เสียภาษีของผู้ขาย", ["totalAmount"] = "ยอดรวมทั้งสิ้น",
    };
    private static readonly Dictionary<string, string> SoImportant = new()
    {
        ["poNo"] = "เลขที่ใบสั่งซื้อ", ["poDate"] = "วันที่เอกสาร", ["customerName"] = "ชื่อลูกค้า",
        ["customerTaxId"] = "เลขทะเบียนผู้เสียภาษีของลูกค้า", ["totalAmount"] = "ยอดรวมทั้งสิ้น",
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
        if (missing.Count > 0) reasons.Add("ไม่พบข้อมูล: " + string.Join(", ", missing));
        if (lines.Count == 0) reasons.Add("ไม่พบรายการสินค้า/บริการ (Item Detail)");
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
    public async Task<ParsedDocument> ExtractAsync(string path, string module, string? providerOverride = null)
    {
        var doc = await ExtractDispatchAsync(path, module, providerOverride);
        doc.Note = doc.Provider switch
        {
            "demo" => string.IsNullOrEmpty(doc.Note) ? "ใช้ข้อมูลตัวอย่าง (demo) ไม่ได้อ่านจากไฟล์จริง" : doc.Note,
            "failed" => string.IsNullOrEmpty(doc.Note) ? "อ่านเอกสารไม่สำเร็จ" : doc.Note,
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
            return DemoFallback(path, module, "เชื่อมต่อ Azure Document Intelligence ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า AZURE_DI_ENDPOINT/AZURE_DI_KEY ใน .env");
        }
        if (provider == "claude")
        {
            var outDoc = await ClaudeOcr.VisionExtractAsync(path, module, config);
            if (outDoc != null) return outDoc;
            return DemoFallback(path, module, "เชื่อมต่อ Claude Vision ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า ANTHROPIC_API_KEY ใน .env");
        }
        if (provider == "gemini")
        {
            var outDoc = await GeminiOcr.VisionExtractAsync(path, module, config);
            if (outDoc != null) return outDoc;
            return DemoFallback(path, module, "เชื่อมต่อ Google Gemini Vision ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า GEMINI_API_KEY ใน .env");
        }
        if (provider == "openai")
        {
            var outDoc = await OpenAiOcr.VisionExtractAsync(path, module, config);
            if (outDoc != null) return outDoc;
            return DemoFallback(path, module, "เชื่อมต่อ OpenAI Vision ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า OPENAI_API_KEY ใน .env");
        }
        if (provider == "claude_text")
        {
            var preText = ext == ".pdf" ? PdfExtraction.PdfText(path) : "";
            if (string.IsNullOrWhiteSpace(preText) && (ImageExt.Contains(ext) || ext == ".pdf"))
                preText = await TesseractOcr.ExtractTextAsync(path, config);
            if (string.IsNullOrWhiteSpace(preText))
                return DemoFallback(path, module, "OCR อ่านข้อความจากไฟล์ไม่ได้ จึงส่งให้ Claude จัดโครงสร้างไม่ได้ (ลองใช้ Claude Vision แทน)");
            var outDoc = await ClaudeOcr.TextExtractAsync(module, preText, config);
            if (outDoc != null) return outDoc;
            return DemoFallback(path, module, "เชื่อมต่อ Claude (จัดโครงสร้างจากข้อความ) ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า ANTHROPIC_API_KEY ใน .env");
        }
        if (provider == "typhoon")
        {
            var (text, err) = await TyphoonOcr.ExtractTextAsync(path, config);
            if (string.IsNullOrWhiteSpace(text))
                return DemoFallback(path, module, err != "" ? err : "Typhoon OCR ไม่คืนข้อความใด ๆ กลับมา");
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

        return DemoFallback(path, module, "อ่านข้อความจากไฟล์ไม่ได้ (ไฟล์สแกน/ยังไม่ได้ตั้งค่า OCR engine)");
    }

    private static bool HasValue(Dictionary<string, object?> header, string key) =>
        header.TryGetValue(key, out var v) && !string.IsNullOrEmpty(Convert.ToString(v));
}

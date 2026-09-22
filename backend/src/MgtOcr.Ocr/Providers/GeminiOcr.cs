using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// New in the .NET port (no Python equivalent) — Google Gemini Vision, same raw-HTTP/no-SDK
// pattern as the existing Claude/Azure/Typhoon clients. Uses the Generative Language API's
// generateContent endpoint with inline base64 image parts.
public static class GeminiOcr
{
    private static readonly HttpClient Http = new();

    public static async Task<(ParsedDocument? Doc, string? Error)> VisionExtractAsync(string path, string module, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey))
            return (null, "GeminiApiKey is empty in config \u2014 appsettings Ocr:GeminiApiKey was not loaded by the running app (check which appsettings.json/appsettings.{Env}.json the process reads, and that it was restarted)");
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Was the first 3 pages only — a shipping bundle (invoices + receipts + packing slip, with
            // the FORM SHIPPING EXPENSE summary as the LAST page) never had its summary read. Send
            // every page up to 20, as JPEG at 150 dpi to stay under the inline request size limit.
            var isPdf = ext == ".pdf";
            var imgs = isPdf
                ? PdfRasterizer.RenderPagesToJpeg(path, maxPages: 20, dpi: 150)
                : [await File.ReadAllBytesAsync(path)];
            var mime = isPdf ? "image/jpeg" : ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png",
            };
            if (imgs.Count == 0)
                return (null, $"Could not rasterize '{Path.GetFileName(path)}' to images (renderer produced 0 pages \u2014 check the PDF rasterizer on the server)");

            // A prompt rule alone ("take lines from the FORM SHIPPING EXPENSE page") was not enough:
            // with 17 pages Gemini still took the first invoice's lines. When the text layer shows
            // which page the form is, put that page FIRST and say so explicitly up front.
            var prompt = VisionPrompt.Build(module);
            if (isPdf && module == "II" && imgs.Count > 1)
            {
                var formIdx = PdfExtraction.FindPageIndex(path,
                    new System.Text.RegularExpressions.Regex(@"FORM\s*SHIPPING\s*EXPENSE", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (formIdx > 0 && formIdx < imgs.Count)
                {
                    var form = imgs[formIdx];
                    imgs.RemoveAt(formIdx);
                    imgs.Insert(0, form);
                }
                if (formIdx >= 0 && formIdx < imgs.Count)
                    prompt = $"สำคัญ: ภาพแรกคือหน้า FORM SHIPPING EXPENSE (หน้า {formIdx + 1} ของไฟล์) " +
                             "lines ต้องมาจากตารางในภาพแรกนี้เท่านั้น ห้ามใช้รายการจากใบแจ้งหนี้/ใบเสร็จในภาพอื่นเป็น lines " +
                             "ภาพที่เหลือเป็นเอกสารประกอบ ให้ใช้เพื่อหายอดหัก ณ ที่จ่ายมาต่อท้าย lines ตามกติกาด้านล่าง\n\n" + prompt;
            }
            var parts = new List<object> { new { text = prompt } };
            parts.AddRange(imgs.Select(b => (object)new { inline_data = new { mime_type = mime, data = Convert.ToBase64String(b) } }));

            var body = new
            {
                contents = new[] { new { role = "user", parts = (object)parts } },
                // maxOutputTokens was 3000: newer Gemini Flash models spend part of that budget on internal
                // "thinking", so a longer invoice could be cut off mid-JSON (finishReason MAX_TOKENS) and fail
                // to parse — intermittently, since thinking length varies run to run. responseMimeType makes
                // Gemini emit bare JSON with no prose/code fences around it.
                generationConfig = new { temperature = 0, maxOutputTokens = 16384, responseMimeType = "application/json" },
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                return (null, $"Gemini HTTP {(int)resp.StatusCode} (model={config.GeminiModel}): {Trunc(respText, 400)}");

            var raw = ExtractText(respText);
            var parsed = VisionPrompt.ParseResponse(raw, module, "gemini", 0.87, raw);
            if (parsed != null) return (parsed, null);
            // Gemini answered 200 but the answer was not usable JSON — say why instead of a bare null
            // (which surfaced as the misleading "Could not connect to Google Gemini Vision").
            return (null, $"Gemini replied but the result could not be read as JSON (finishReason={FinishReason(respText)}, " +
                          $"{raw.Length} chars, model={config.GeminiModel}) — try Re-read Document. Reply starts: {Trunc(raw, 200)}");
        }
        catch (Exception ex)
        {
            return (null, $"Gemini request failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "\u2026";

    private static string FinishReason(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("candidates", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0
                && c[0].TryGetProperty("finishReason", out var fr))
                return fr.GetString() ?? "?";
            if (doc.RootElement.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
                return "BLOCKED:" + br.GetString();
            return "no candidates";
        }
        catch (JsonException) { return "unparseable response"; }
    }

    private static string ExtractText(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            return "";
        var sb = new StringBuilder();
        if (cands[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("text", out var t))
                    sb.Append(t.GetString());
        }
        return sb.ToString();
    }
}

using System.Linq;
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
            // Was the first 3 pages only — a shipping bundle (invoices + receipts + packing slip,
            // with the FORM SHIPPING EXPENSE summary as the LAST page) never had its summary read.
            // Shipping bundles run long (the FORM summary is the last page), so read up to 50
            // pages. Gemini's inline request limit is ~20 MB including base64 overhead, so the
            // resolution steps down as the file gets longer — still readable, still one request.
            const int MaxPages = 50;
            var isPdf = ext == ".pdf";
            var pageCount = isPdf ? PdfRasterizer.PageCount(path) : 1;
            var (dpi, quality) = pageCount switch
            {
                <= 20 => (150, 80),
                <= 35 => (130, 75),
                _ => (110, 70),
            };
            var imgs = isPdf
                ? PdfRasterizer.RenderPagesToJpeg(path, maxPages: MaxPages, dpi: dpi, quality: quality)
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
            // "AP" too: Import Invoice uploads are read as module AP and only routed to II (no PO) AFTER
            // the read, so an II-only check here never fired for a fresh upload.
            if (isPdf && (module is "AP" or "II") && imgs.Count > 1)
            {
                // A bundle can hold one FORM per PO, so every form page is pulled to the front —
                // including any that sits beyond the page cap (rendered on its own, replacing the
                // last and least useful page).
                var formIdxs = PdfExtraction.FindPageIndexes(path,
                    new System.Text.RegularExpressions.Regex(@"FORM\s*SHIPPING\s*EXPENSE", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                var formPageNos = formIdxs.Select(i => i + 1).ToList();
                var formPages = new List<byte[]>();
                foreach (var idx in formIdxs)
                {
                    if (idx < imgs.Count) { formPages.Add(imgs[idx]); }
                    else if (PdfRasterizer.RenderPageToJpeg(path, idx, dpi, quality) is { } extra) { formPages.Add(extra); }
                }
                if (formPages.Count > 0)
                {
                    foreach (var idx in formIdxs.Where(i => i < imgs.Count).OrderByDescending(i => i)) imgs.RemoveAt(idx);
                    while (imgs.Count + formPages.Count > MaxPages) imgs.RemoveAt(imgs.Count - 1);
                    imgs.InsertRange(0, formPages);
                    prompt = $"สำคัญ: {formPages.Count} ภาพแรกคือหน้า FORM SHIPPING EXPENSE (หน้า {string.Join(", ", formPageNos)} ของไฟล์) " +
                             "lines ต้องมาจากตารางในภาพเหล่านี้เท่านั้น ห้ามใช้รายการจากใบแจ้งหนี้/ใบเสร็จในภาพอื่นเป็น lines " +
                             (formPages.Count > 1
                                ? "ฟอร์มมีหลายใบเพราะมีหลาย PO — ให้รวมรายการชื่อเดียวกันของ vendor เดียวกันจากทุกใบเป็นแถวเดียวโดยบวกยอดกัน " +
                                  "และใส่ poRef เป็นเลข PO ทุกใบคั่นด้วย \", \" "
                                : "") +
                             "ภาพที่เหลือเป็นเอกสารประกอบ ให้ใช้เพื่อหายอดหัก ณ ที่จ่ายมาต่อท้าย lines ตามกติกาด้านล่าง\n\n" + prompt;
                }
            }
            var parts = new List<object> { new { text = prompt } };
            parts.AddRange(imgs.Select(b => (object)new { inline_data = new { mime_type = mime, data = Convert.ToBase64String(b) } }));

            var body = new
            {
                contents = new[] { new { role = "user", parts = (object)parts } },
                // maxOutputTokens was 3000/8192: newer Gemini Flash models spend part of that budget on internal
                // "thinking", so a longer invoice could be cut off mid-JSON (finishReason MAX_TOKENS) and fail
                // to parse. responseMimeType makes Gemini emit bare JSON with no prose/code fences around it.
                // (Kanomwan, 0cd227c)
                generationConfig = new { temperature = 0, maxOutputTokens = 16384, responseMimeType = "application/json" },
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
            var payload = JsonSerializer.Serialize(body);

            // Gemini's public endpoint frequently returns TRANSIENT errors — HTTP 503 (model
            // overloaded) and 429 (rate limit / quota) especially — and a large multi-page PDF can hit
            // the request timeout. With a single attempt any of these looked to the user like "Gemini
            // won't connect" (and, worse, used to be swallowed into demo data). Retry a few times with a
            // short exponential backoff on those transient conditions before giving up. Non-transient
            // errors (400/401/403, bad model id, bad/blocked key) are returned immediately — retrying
            // can't fix them — so the real reason still surfaces fast.
            //
            // Every failed attempt is also written to the console/log via Log() below, so an
            // intermittent "sometimes it connects, sometimes it doesn't" failure is captured
            // automatically (with the real HTTP status / reason) — no need to catch it live in a
            // debugger. Look at the app's console/log output to see the pattern (503 vs 429 vs timeout).
            const int maxAttempts = 4;
            var lastErr = "Could not connect to Google Gemini Vision";
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                    using var resp = await Http.SendAsync(req, cts.Token);
                    var respText = await resp.Content.ReadAsStringAsync(cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var raw = ExtractText(respText);
                        var parsedDoc = VisionPrompt.ParseResponse(raw, module, "gemini", 0.87, raw);
                        if (parsedDoc != null)
                            return (parsedDoc, null);
                        // 200 OK but the body held no parseable JSON. This is NOT a connection problem —
                        // almost always the JSON got TRUNCATED because the model's output-token budget ran
                        // out (thinking models spend part of it on reasoning/thoughtSignature), or the
                        // response was empty/safety-blocked. Report THAT precisely instead of falling
                        // through to the misleading "Could not connect to Google Gemini Vision" default.
                        var finish = FinishReason(respText);
                        lastErr = "Gemini returned HTTP 200 but no usable JSON"
                            + (finish != null
                                ? $" (finishReason={finish}{(finish == "MAX_TOKENS" ? " — response was cut off; raise maxOutputTokens" : "")})"
                                : " (empty or safety-blocked response)")
                            + $". extractedTextLen={raw.Length}, respLen={respText.Length}";
                        Log(lastErr);
                        return (null, lastErr);
                    }
                    var status = (int)resp.StatusCode;
                    var transient = status is 429 or 500 or 502 or 503 or 504;
                    lastErr = $"Gemini HTTP {status} (model={config.GeminiModel}"
                        + (transient ? $", attempt {attempt}/{maxAttempts}" : "")
                        + $"): {Trunc(respText, 400)}";
                    Log(lastErr);
                    if (!transient || attempt == maxAttempts)
                        return (null, lastErr);
                }
                catch (OperationCanceledException)
                {
                    // HttpClient timeout (TaskCanceledException) — treat as transient.
                    lastErr = $"Gemini request timed out after 180s (model={config.GeminiModel}, attempt {attempt}/{maxAttempts}) "
                        + "— the document may be large, or Gemini is slow/overloaded";
                    Log(lastErr);
                    if (attempt == maxAttempts) return (null, lastErr);
                }
                catch (HttpRequestException ex)
                {
                    // Connection-level failure (DNS/TLS/network) — treat as transient and retry.
                    lastErr = $"Gemini connection error (attempt {attempt}/{maxAttempts}): {ex.Message}";
                    Log(lastErr);
                    if (attempt == maxAttempts) return (null, lastErr);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(800 * attempt * attempt));
            }
            return (null, lastErr);
        }
        catch (Exception ex)
        {
            return (null, $"Gemini request failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Writes each Gemini failure to the console/log with a timestamp so an intermittent, hard-to-catch
    // failure is recorded automatically \u2014 the user doesn't have to reproduce it in a debugger. Prefixed
    // so it's easy to filter the app log (e.g. findstr "[GeminiOcr]").
    private static void Log(string msg) =>
        Console.Error.WriteLine($"[GeminiOcr] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}");

    // Reads candidates[0].finishReason from a Gemini 200 response so a truncated/blocked result can be
    // reported precisely (e.g. "MAX_TOKENS"). Returns null if the field isn't present.
    private static string? FinishReason(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array
                && cands.GetArrayLength() > 0 && cands[0].TryGetProperty("finishReason", out var fr)
                && fr.ValueKind == JsonValueKind.String)
                return fr.GetString();
            // Prompt blocked before any candidate was produced (from 0cd227c).
            if (doc.RootElement.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
                return "BLOCKED:" + br.GetString();
        }
        catch { /* not JSON / unexpected shape \u2014 no finishReason to report */ }
        return null;
    }

    private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "\u2026";

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

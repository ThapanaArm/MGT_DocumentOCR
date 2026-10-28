using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// New in the .NET port (no Python equivalent) — Google Gemini Vision, same raw-HTTP/no-SDK
// pattern as the existing Claude/Azure/Typhoon clients. Uses the Generative Language API's
// generateContent endpoint with inline base64 image parts.
public static partial class GeminiOcr
{
    // Gemini is called DIRECTLY, never through the machine's configured web proxy.
    //
    // Every request hung for the full timeout and never came back — 135s even for a three-page
    // request, on every attempt — while curl reached the same host in 0.2s. curl does not read the
    // Windows proxy settings and HttpClient does, which is the one difference between them, so the
    // requests were going out through a proxy that accepts the connection and then never answers.
    // Nothing here needs one: generativelanguage.googleapis.com is a plain public HTTPS endpoint,
    // and if this machine ever truly required a proxy the call would fail fast with a connection
    // error instead of hanging, which is a far better failure than a silent 135-second stall.
    //
    // A server that genuinely reaches the internet only through a proxy can put it back with the
    // environment variable OCR_USE_SYSTEM_PROXY=1 — no rebuild, no code change. It is read here
    // rather than from AppConfig because this client is created before configuration exists.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = Environment.GetEnvironmentVariable("OCR_USE_SYSTEM_PROXY") == "1",
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    });

    /// <param name="pageHint">Page memory: reference pictures of anchor pages from posted documents
    /// (see PageMemory.cs). Null = none; the classifier still runs, it just has nothing to compare with.</param>
    public static async Task<(ParsedDocument? Doc, string? Error)> VisionExtractAsync(string path, string module, AppConfig config, string salesOrg = "", string apDocCategory = "", PageMemoryHint? pageHint = null)
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
            // Resolution steps down as the bundle gets longer: every page is rasterised and sent in
            // one request, so the payload — and the time Gemini needs to look at it — grows with the
            // page count. A 22-page bundle at 130 dpi came out around 10-15 MB and timed out four
            // times in a row, so the bands past 20 pages were pulled down.
            var (dpi, quality) = pageCount switch
            {
                <= 12 => (150, 80),
                <= 20 => (130, 75),
                <= 30 => (110, 70),
                _ => (100, 65),
            };
            var imgs = isPdf
                ? PdfRasterizer.RenderPagesToJpeg(path, maxPages: MaxPages, dpi: dpi, quality: quality)
                : [await File.ReadAllBytesAsync(path)];
            var mime = isPdf ? "image/jpeg" : ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => "image/png",
            };
            // The file page number (1-based) of each image in imgs, kept in step with every reorder
            // below so page memory can say which pages were read.
            var pageNos = Enumerable.Range(1, imgs.Count).ToList();
            if (imgs.Count == 0)
                return (null, $"Could not rasterize '{Path.GetFileName(path)}' to images (renderer produced 0 pages \u2014 check the PDF rasterizer on the server)");

            // A prompt rule alone ("take lines from the FORM SHIPPING EXPENSE page") was not enough:
            // with 17 pages Gemini still took the first invoice's lines. When the text layer shows
            // which page the form is, put that page FIRST and say so explicitly up front.
            var prompt = VisionPrompt.Build(module, "image", salesOrg, apDocCategory);
            // Keyed against the purchase order the goods table is on the PURCHASE ORDER sheet, not
            // on the shipping form, so the anchor page is a different sheet: pulling the form to the
            // front there is what made the read come back with 13,951.73 of shipping costs instead
            // of the PO's 22,798.00.
            var isPoRead = VisionPrompt.IsInventoryPo(apDocCategory);
            // How many of the images at the FRONT of the list are anchor pages — the FORM SHIPPING
            // EXPENSE sheet normally, the PURCHASE ORDER sheet for the PO category. Zero when the
            // file has neither. This is what the chunked reader splits on.
            var formCount = 0;
            // "AP" too: Import Invoice uploads are read as module AP and only routed to II (no PO) AFTER
            // the read, so an II-only check here never fired for a fresh upload.
            if (isPdf && (module is "AP" or "II") && imgs.Count > 1)
            {
                // A bundle can hold one FORM per PO, so every form page is pulled to the front —
                // including any that sits beyond the page cap (rendered on its own, replacing the
                // last and least useful page).
                var formIdxs = FindAnchorPageIndexes(path, isPoRead);
                var formPageNos = formIdxs.Select(i => i + 1).ToList();
                var formPages = new List<byte[]>();
                var formPagesNos = new List<int>();
                foreach (var idx in formIdxs)
                {
                    if (idx < imgs.Count) { formPages.Add(imgs[idx]); formPagesNos.Add(idx + 1); }
                    else if (PdfRasterizer.RenderPageToJpeg(path, idx, dpi, quality) is { } extra) { formPages.Add(extra); formPagesNos.Add(idx + 1); }
                }
                if (formPages.Count > 0)
                {
                    foreach (var idx in formIdxs.Where(i => i < imgs.Count).OrderByDescending(i => i)) { imgs.RemoveAt(idx); pageNos.RemoveAt(idx); }
                    while (imgs.Count + formPages.Count > MaxPages) { imgs.RemoveAt(imgs.Count - 1); pageNos.RemoveAt(pageNos.Count - 1); }
                    imgs.InsertRange(0, formPages);
                    pageNos.InsertRange(0, formPagesNos);
                    formCount = formPages.Count;
                    prompt = AnchorPromptPrefix(isPoRead, formPages.Count, formPageNos) + prompt;
                }
            }
            // ---- Page memory (PageMemory.cs, Ocr:PageMemory:Mode) ----
            // The regex above only finds the anchor page through the text layer, which a scan does not
            // have. The classifier looks at the top of every page instead. "shadow" runs it beside the
            // read and only records the answer; "on" uses it to find the anchor and leave out pages
            // that carry no money (packing list, B/L, certificates, blank).
            var memMode = (config.OcrPageMemoryMode ?? "off").Trim().ToLowerInvariant();
            var memEnabled = memMode is "shadow" or "on" && isPdf && module is "AP" or "II";
            var anchorRole = PageRoles.AnchorFor(isPoRead);
            var regexAnchorNos = pageNos.Take(formCount).ToList();
            var classifyCount = Math.Min(pageCount, MaxPages);
            Task<PageClassification?>? classifyTask = memEnabled && pageCount >= Math.Max(2, config.OcrPageMemoryMinPages)
                ? Task.Run(() => ClassifyPagesAsync(path, classifyCount, anchorRole, pageHint, config))
                : null;

            var origImgs = imgs; var origNos = pageNos; var origFormCount = formCount; var origPrompt = prompt;
            var planNote = "";
            PageClassification? classification = null;
            if (memMode == "on" && classifyTask != null)
            {
                classification = await classifyTask;
                if (classification != null)
                {
                    var plan = PlanPages(classification, imgs, pageNos, formCount, prompt, isPoRead, anchorRole);
                    if (plan.Changed)
                    {
                        (imgs, pageNos, formCount, prompt, planNote) = (plan.Imgs, plan.PageNos, plan.FormCount, plan.Prompt, plan.Note);
                        Log($"Page memory (on): {planNote}");
                    }
                }
            }

            // Position hints (Ocr:PositionHints:Enabled). The main request also reports where it found
            // each header value, and — when earlier documents of this type left such a record — is told
            // which box held which field, per vendor. Off by default; supporting pages are never affected.
            var positionHintUsed = false;
            if (config.OcrPositionHints && (module is "AP" or "II"))
            {
                prompt = prompt + PositionInstructions(pageHint?.Positions);
                positionHintUsed = !string.IsNullOrWhiteSpace(pageHint?.Positions);
            }

            var result = await ReadPlanAsync(imgs, formCount, prompt);
            if (result.Doc != null) result.Doc.PositionHintUsed = positionHintUsed;
            var readNos = pageNos;
            if (planNote.Length > 0 && result.Doc != null && result.Doc.Lines.Count == 0)
            {
                // The narrowed read found nothing at all — never let page memory cost a document.
                Log("Page memory (on): the read with the page plan found no lines — reading again the old way");
                var again = await ReadPlanAsync(origImgs, origFormCount, origPrompt);
                if (again.Doc != null)
                {
                    if (result.Doc.TokensIn != null || again.Doc.TokensIn != null)
                    {
                        again.Doc.TokensIn = (again.Doc.TokensIn ?? 0) + (result.Doc.TokensIn ?? 0);
                        again.Doc.TokensOut = (again.Doc.TokensOut ?? 0) + (result.Doc.TokensOut ?? 0);
                    }
                    result = again;
                    readNos = origNos;
                    planNote = "Page memory: the narrowed read found no lines, so the whole document was read again";
                }
            }

            if (classifyTask != null && classification == null && memMode == "shadow")
            {
                // Shadow only records — it must never make the user wait. The classifier normally
                // finishes long before the read (header strips only); if it is still running a few
                // seconds after the read, the page map is simply not recorded for this document.
                try { classification = await classifyTask.WaitAsync(ShadowWait); }
                catch (TimeoutException)
                {
                    classification = null;
                    Log($"Page memory (shadow): the page classifier was still running {ShadowWait.TotalSeconds:0} s after the read — not recorded");
                }
                catch { classification = null; }
            }
            if (result.Doc != null && memEnabled)
            {
                if (classification?.TokensIn != null)
                {
                    // The classification is a billed request too.
                    result.Doc.TokensIn = (result.Doc.TokensIn ?? 0) + classification.TokensIn;
                    result.Doc.TokensOut = (result.Doc.TokensOut ?? 0) + (classification.TokensOut ?? 0);
                    if (classification.ThinkingTokens != null)
                        result.Doc.ThinkingTokens = (result.Doc.ThinkingTokens ?? 0) + classification.ThinkingTokens;
                }
                result.Doc.PageMap = BuildPageMap(path, classification, regexAnchorNos, anchorRole, readNos);
                result.Doc.PageMemoryMode = memMode;
                result.Doc.PageMemoryNote = planNote;
            }
            return result;

            // The read itself, unchanged: one request for a short file, lead + supporting groups for a long one.
            async Task<(ParsedDocument? Doc, string? Error)> ReadPlanAsync(List<byte[]> planImgs, int planFormCount, string planPrompt)
            {
                // A long bundle cannot be read in one request: 22 pages timed out at 288s and again at
                // 610s, because what takes the time is Gemini LOOKING at that many images, not sending
                // them. So it is split — the FORM SHIPPING EXPENSE pages in one request (they carry the
                // header and the cost lines), the supporting invoices and receipts in groups after it
                // (they only contribute tax rows). Each request is small enough to answer in well under
                // a minute, and the first one is MORE accurate on its own than it was buried in twenty
                // other pages.
                if (isPdf && planImgs.Count > ChunkThreshold)
                    return await ChunkedAsync(planImgs, planFormCount, mime, planPrompt, module, config, dpi, salesOrg, apDocCategory);

                // A file short enough to read in one request still needs the customs rows pointed at the
                // customs vendor: the model fills vendorCode from the form when it can, but it leaves it
                // blank often enough that the tax would otherwise land on the shipping agent.
                var oneShot = await OneShotAsync(planImgs, mime, planPrompt, module, config, dpi, TimeoutFor(planImgs.Count));
                if (oneShot.Doc != null && module is "AP" or "II") AssignTaxRowVendors(oneShot.Doc);
                return oneShot;
            }
        }
        catch (Exception ex)
        {
            return (null, $"Gemini request failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The anchor pages found through the PDF's text layer (0-based): the FORM SHIPPING
    /// EXPENSE sheets, or the PURCHASE ORDER sheets for "Goods/service items". Empty on a scan with
    /// no text layer. Shared by the read and by the page-memory backfill.</summary>
    internal static List<int> FindAnchorPageIndexes(string path, bool isPoRead)
    {
        var formSheet = new System.Text.RegularExpressions.Regex(@"FORM\s*SHIPPING\s*EXPENSE",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // The title is a heading in a large font, and the layout-aware text reconstruction
        // can split a heading into single letters, so whitespace is allowed between every
        // letter — "PURCHASE ORDER" and "P U R C H A S E  O R D E R" both have to match.
        var poSheet = new System.Text.RegularExpressions.Regex(
            @"P\s*U\s*R\s*C\s*H\s*A\s*S\s*E\s*O\s*R\s*D\s*E\s*R",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // The form sheet is excluded from the PO's page list: it prints "PO. NO." and would
        // otherwise be a candidate for being pulled to the front all over again.
        var formIdxs = isPoRead
            ? PdfExtraction.FindPageIndexes(path, poSheet, formSheet)
            : PdfExtraction.FindPageIndexes(path, formSheet);
        if (isPoRead && formIdxs.Count == 0)
        {
            // No page announced itself as a purchase order — the heading may be an image, or
            // reconstructed in a shape no regex catches. The PO NUMBER is printed as plain
            // text on the same page and is the more reliable anchor: the shipping form
            // prints it too, which is exactly why the form is excluded here.
            var poNo = PoNumberFinder.FindInFileName(Path.GetFileName(path));
            if (poNo.Length == 0) poNo = PoNumberFinder.Find(PdfExtraction.PdfText(path));
            if (poNo.Length > 0)
                formIdxs = PdfExtraction.FindPageIndexes(path,
                    new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(poNo)),
                    formSheet);
            Log(formIdxs.Count > 0
                ? $"No PURCHASE ORDER heading found \u2014 using the {formIdxs.Count} page(s) that print PO {poNo}"
                : "No PURCHASE ORDER page found in the text layer \u2014 reading the first pages instead");
        }
        else if (isPoRead)
            Log($"PO read: purchase order page(s) {string.Join(", ", formIdxs.Select(i => i + 1))}");
        return formIdxs;
    }

    /// <summary>The instruction put in front of the prompt once the anchor pages have been moved to
    /// the front of the images (unchanged wording; shared by the text-layer regex and page memory).</summary>
    private static string AnchorPromptPrefix(bool isPoRead, int count, IEnumerable<int> filePageNos) => isPoRead
        ? $"สำคัญ: {count} ภาพแรกคือหน้า PURCHASE ORDER (หน้า {string.Join(", ", filePageNos)} ของไฟล์) " +
          "lines ต้องมาจากตารางรายการสินค้า (Item / Description / Quantity / Unit Price / Amount) ในภาพเหล่านี้เท่านั้น " +
          "ห้ามใช้รายการจากหน้า FORM SHIPPING EXPENSE หรือใบแจ้งหนี้ค่าขนส่ง/ใบเสร็จในภาพอื่นเป็น lines " +
          "totalAmount ต้องเป็นยอด Total Amount ของหน้า PURCHASE ORDER ไม่ใช่ยอดรวมค่าขนส่ง " +
          (count > 1
             ? "หน้า PURCHASE ORDER มีหลายหน้า (Page 1 of 2, 2 of 2) — ให้รวมรายการสินค้าจากทุกหน้าเรียงต่อกันตามเลข Item "
             : "") +
          "ภาพที่เหลือเป็นเอกสารประกอบ ใช้เพื่ออ้างอิงเท่านั้น ห้ามนำมาเป็น lines\n\n"
        : $"สำคัญ: {count} ภาพแรกคือหน้า FORM SHIPPING EXPENSE (หน้า {string.Join(", ", filePageNos)} ของไฟล์) " +
             "lines ต้องมาจากตารางในภาพเหล่านี้เท่านั้น ห้ามใช้รายการจากใบแจ้งหนี้/ใบเสร็จในภาพอื่นเป็น lines " +
             (count > 1
                ? "ฟอร์มมีหลายใบเพราะมีหลาย PO — ให้รวมรายการชื่อเดียวกันของ vendor เดียวกันจากทุกใบเป็นแถวเดียวโดยบวกยอดกัน " +
                  "และใส่ poRef เป็นเลข PO ทุกใบคั่นด้วย \", \" "
                : "") +
             "ภาพที่เหลือเป็นเอกสารประกอบ ให้ใช้เพื่อหายอดหัก ณ ที่จ่ายมาต่อท้าย lines ตามกติกาด้านล่าง\n\n";

    /// <summary>Text-only structuring: another OCR engine (PaddleOCR) has already read the characters,
    /// so only the text goes to Gemini — no images — with the same JSON schema/rules as the Vision read.
    /// Much smaller request than Vision, so it is cheaper and faster.</summary>
    public static async Task<(ParsedDocument? Doc, string? Error)> TextExtractAsync(
        string module, string text, AppConfig config, string salesOrg = "", string providerId = "paddle_gemini")
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey))
            return (null, "GeminiApiKey is empty in config \u2014 cannot structure the OCR text with Gemini");
        if (string.IsNullOrWhiteSpace(text)) return (null, "No OCR text to send to Gemini");
        var prompt = VisionPrompt.Build(module, "text", salesOrg) + "\n\n--- ข้อความจาก OCR ---\n" +
                     (text.Length > 40000 ? text[..40000] : text) + "\n";
        var res = await OneShotAsync([], "", prompt, module, config, 0, TimeSpan.FromSeconds(180), providerId, text);
        if (res.Doc != null && module is "AP" or "II") AssignTaxRowVendors(res.Doc);
        return res;
    }

    /// <summary>Pages per request once a file is split. Eight A4 pages answer comfortably inside
    /// the timeout below; the threshold is a little above that so a file only slightly longer than
    /// one chunk is still read in a single request.</summary>
    private const int ChunkThreshold = 14;

    /// <summary>Pages of the vendor's own invoice that ride along with the form pages in the first
    /// request, so the header fields printed there are read. Two, because an invoice's header can
    /// run onto a second page.</summary>
    private const int HeaderPages = 2;
    private const int ChunkSize = 8;

    /// <summary>Pages read on their own first when the file has no FORM SHIPPING EXPENSE sheet to
    /// anchor on — the header and any line items are almost always at the front.</summary>
    private const int LeadPages = 6;

    /// <summary>Roughly 25s a page: what runs long is the model looking at the images, so the wait
    /// tracks the page count rather than the payload size.</summary>
    private static TimeSpan TimeoutFor(int pages) =>
        TimeSpan.FromSeconds(Math.Clamp(60 + pages * 25, 120, 900));

    private static async Task<(ParsedDocument? Doc, string? Error)> OneShotAsync(
        List<byte[]> imgs, string mime, string prompt, string module, AppConfig config, int dpi, TimeSpan timeout,
        string providerId = "gemini", string? rawTextForRecord = null)
    {
        try
        {
            var pageCount = imgs.Count;
            var parts = new List<object> { new { text = prompt } };
            parts.AddRange(imgs.Select(b => (object)new { inline_data = new { mime_type = mime, data = Convert.ToBase64String(b) } }));

            // Thinking cap (ReadTuning): sent only when Ocr:ThinkingBudget / Ocr:ThinkingLevel or a read
            // test asks for it; otherwise the request is exactly what it was.
            var thinkingConfig = ReadTuning.ThinkingConfig(config);
            string BuildPayload(bool withThinking)
            {
                // maxOutputTokens was 3000/8192: newer Gemini Flash models spend part of that budget on internal
                // "thinking", so a longer invoice could be cut off mid-JSON (finishReason MAX_TOKENS) and fail
                // to parse. responseMimeType makes Gemini emit bare JSON with no prose/code fences around it.
                // (Kanomwan, 0cd227c)
                var gen = new Dictionary<string, object>
                {
                    ["temperature"] = 0, ["maxOutputTokens"] = 16384, ["responseMimeType"] = "application/json",
                };
                if (withThinking && thinkingConfig != null) gen["thinkingConfig"] = thinkingConfig;
                return JsonSerializer.Serialize(new
                {
                    contents = new[] { new { role = "user", parts = (object)parts } },
                    generationConfig = gen,
                });
            }
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
            var thinkingSent = thinkingConfig != null;
            var payload = BuildPayload(thinkingSent);

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
            // A timeout is not the same kind of transient as a 503. An overloaded model clears in
            // seconds, so retrying the same request makes sense; a request that is simply too big
            // will time out again in exactly the same way, and four attempts at the timeout below
            // burned twelve minutes before telling the user anything. Two is enough to ride out a
            // slow moment without making someone wait through a hopeless third and fourth.
            const int maxTimeoutAttempts = 2;
            var timedOut = 0;

            var lastErr = "Could not connect to Google Gemini Vision";
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    using var cts = new CancellationTokenSource(timeout);
                    var sentAt = System.Diagnostics.Stopwatch.StartNew();
                    using var resp = await Http.SendAsync(req, cts.Token);
                    var respText = await resp.Content.ReadAsStringAsync(cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var raw = ExtractText(respText);
                        var parsedDoc = VisionPrompt.ParseResponse(raw, module, providerId,
                            providerId == "gemini" ? 0.87 : 0.82, rawTextForRecord ?? raw);
                        if (parsedDoc != null)
                        {
                            // What this read cost. Gemini bills per token, not per second, so the
                            // timings alone cannot produce an invoice figure — and until now nothing
                            // read usageMetadata at all, which is why ocr.Document.OcrTokensIn /
                            // OcrTokensOut (and therefore OcrCost, and the dashboard's cost tiles)
                            // have been NULL for every document ever read.
                            var (tin, tout) = Usage(respText);
                            parsedDoc.TokensIn = tin;
                            parsedDoc.TokensOut = tout;
                            // Measuring where the time goes (Megachem, 28 Oct 2026): the model's own
                            // "thinking" is billed and timed apart from the answer, so it is kept
                            // separately; one line per request shows pages / time / tokens.
                            parsedDoc.ThinkingTokens = ThinkingTokens(respText);
                            parsedDoc.PositionsJson = ExtractPositions(raw);
                            Log($"Request done ({(thinkingSent ? "thinking capped" : "model default")}): {pageCount} image(s) at {dpi} dpi in {sentAt.ElapsedMilliseconds} ms - "
                                + $"tokens in {tin?.ToString() ?? "?"}, answer {tout?.ToString() ?? "?"}, thinking {parsedDoc.ThinkingTokens?.ToString() ?? "0"}");
                            return (parsedDoc, null);
                        }
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
                    // A model that does not accept the thinking setting (budget vs level differ between
                    // Gemini 2.5 and 3) answers 400 — read again without it rather than fail the document.
                    if (status == 400 && thinkingSent && respText.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"Thinking setting rejected by model {config.GeminiModel} — reading without it");
                        thinkingSent = false;
                        payload = BuildPayload(false);
                        continue;
                    }
                    if (!transient || attempt == maxAttempts)
                        return (null, lastErr);
                }
                catch (OperationCanceledException)
                {
                    // HttpClient timeout (TaskCanceledException) — treat as transient.
                    timedOut++;
                    // Deliberately no longer blames the file size. It said that for a long time, and
                    // it was wrong: the requests that hung carried three pages, and the whole point of
                    // the chunked reader is that no request is ever large. A request that never answers
                    // is a connection problem, and saying otherwise sends the next person to split
                    // files that were never too big.
                    lastErr = $"Gemini did not answer within {timeout.TotalSeconds:0}s "
                        + $"(model={config.GeminiModel}, {pageCount} page(s) at {dpi} dpi, attempt {attempt}/{maxAttempts}). "
                        + (pageCount <= 8
                            ? "This request was small, so the file size is not the problem — check the connection "
                              + "to generativelanguage.googleapis.com (a web proxy that accepts the connection and "
                              + "never answers looks exactly like this), or read the document with another engine."
                            : "Try splitting the bundle into smaller files, or read it with another engine.");
                    Log(lastErr);
                    if (attempt == maxAttempts || timedOut >= maxTimeoutAttempts) return (null, lastErr);
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

    /// <summary>Reads a long file as several small requests instead of one large one.
    ///
    /// The first request gets the pages that carry the document itself — the FORM SHIPPING EXPENSE
    /// sheets, or simply the first few pages when there is no form — and produces the header and
    /// the cost lines. Every request after it gets a group of supporting pages and contributes only
    /// tax rows (VAT / WHT / DUTY), which are appended.
    ///
    /// A failed supporting group is logged and skipped rather than failing the whole read: losing
    /// one invoice's VAT, visibly, beats losing the document. Only the first request is fatal,
    /// since without it there is no document at all.</summary>
    private static async Task<(ParsedDocument? Doc, string? Error)> ChunkedAsync(
        List<byte[]> imgs, int formCount, string mime, string mainPrompt,
        string module, AppConfig config, int dpi, string salesOrg = "", string apDocCategory = "")
    {
        // The form pages carry the cost table, but not the header: the vendor's tax id, invoice
        // date and payment terms are printed on the vendor's own invoice, which the reorder above
        // leaves sitting right after the form pages (it was the file's first page). Sending it
        // with them costs one image and is the only way the header request can see those fields.
        // Keyed against the purchase order the PO sheet carries the header too (PO number, supplier,
        // Total Amount), so the two pages that follow it are not sent: they are the front of the
        // shipping bundle, and the only thing they can do in that request is tempt the read back to
        // the shipping costs.
        var isPo = VisionPrompt.IsInventoryPo(apDocCategory);
        var leadCount = formCount > 0
            ? (isPo ? formCount : Math.Min(formCount + HeaderPages, imgs.Count))
            : Math.Min(LeadPages, imgs.Count);
        var lead = imgs.Take(leadCount).ToList();
        var rest = imgs.Skip(leadCount).ToList();

        var leadName = formCount == 0 ? "lead" : isPo ? "purchase order" : "form";
        var groupNote = isPo ? "no supporting groups (PO read)" : "groups of " + ChunkSize;
        Log($"Splitting a {imgs.Count}-page read into 1 + {(isPo ? 0 : (rest.Count + ChunkSize - 1) / ChunkSize)} request(s) "
            + $"({leadCount} {leadName} page(s) then {groupNote})");

        var (doc, err) = await OneShotAsync(lead, mime, mainPrompt, module, config, dpi, TimeoutFor(lead.Count));
        if (doc == null) return (null, err);

        // Keyed against the purchase order the goods page IS the document: the VAT and the shipping
        // costs belong to the delivery document, so the supporting pages have nothing to add and the
        // read stops at one request.
        if (isPo) return (doc, null);

        // The supporting pages are read without the form in view, so the main vendor's name — the
        // party we pay directly — has to travel with the prompt: withholding tax on any other
        // issuer's invoice in the bundle was already deducted and remitted by the shipping agent,
        // and is not ours to record.
        var mainVendorName = doc.Header.TryGetValue("vendorName", out var vn) ? vn?.ToString() ?? "" : "";
        var supporting = VisionPrompt.BuildSupporting(module, mainVendorName, salesOrg, apDocCategory);
        var skipped = 0;
        // The supporting groups do not depend on each other, so they are read side by side instead
        // of one after another (Ocr:ChunkParallelism, default 3; 1 = the old one-at-a-time read).
        // A 40-page bundle is ~4 groups, and waiting for each in turn was most of its 70+ seconds.
        // Results are still MERGED in page order below: MergeTaxRows keeps the first of two equal
        // tax rows and upgrades it, so the merge order must not depend on which request came back
        // first. Each request keeps its own retry on 429/503 (OneShotAsync).
        var groups = new List<(int Start, List<byte[]> Pages)>();
        for (var i = 0; i < rest.Count; i += ChunkSize)
            groups.Add((i, rest.Skip(i).Take(ChunkSize).ToList()));
        var results = new (ParsedDocument? Doc, string? Err)[groups.Count];
        using (var gate = new SemaphoreSlim(Math.Max(1, config.OcrChunkParallelism)))
        {
            await Task.WhenAll(groups.Select(async (g, gi) =>
            {
                await gate.WaitAsync();
                try { results[gi] = await OneShotAsync(g.Pages, mime, supporting, module, config, dpi, TimeoutFor(g.Pages.Count)); }
                finally { gate.Release(); }
            }));
        }
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var (i, chunk) = groups[gi];
            var (part, perr) = results[gi];
            if (part == null)
            {
                skipped++;
                Log($"Supporting pages {leadCount + i + 1}-{leadCount + i + chunk.Count} could not be read: {perr}");
                continue;
            }
            // Every chunk is a separate billed request, so the document's cost is the sum of them
            // all. Without this a 30-page bundle would report only the lead request's tokens and
            // under-state what it actually cost by most of the file.
            doc.TokensIn = (doc.TokensIn ?? 0) + (part.TokensIn ?? 0);
            doc.TokensOut = (doc.TokensOut ?? 0) + (part.TokensOut ?? 0);
            if (part.ThinkingTokens != null) doc.ThinkingTokens = (doc.ThinkingTokens ?? 0) + part.ThinkingTokens;
            MergeTaxRows(doc, part, mainVendorName);
        }

        AssignTaxRowVendors(doc);
        RecountTaxTotals(doc);
        if (skipped > 0)
        {
            var note = $"{skipped} group(s) of supporting pages could not be read — the VAT / withholding rows "
                + "from those pages are missing and need checking by hand";
            doc.ConfidenceNote = doc.ConfidenceNote.Length > 0 ? $"{doc.ConfidenceNote} / {note}" : note;
            doc.Confidence = Math.Min(doc.Confidence, 0.6);
        }
        return (doc, null);
    }

    private static readonly string[] TaxRowCodes = ["VAT", "WHT", "DUTY"];

    /// <summary>Appends the tax rows a supporting group found, skipping any that are already in
    /// the document.
    ///
    /// The same tax reaches the read twice more often than it looks: a supplier sends a billing
    /// note and then the tax invoice for it, and a customs shipment is documented by both the
    /// import entry and the receipt — all four pages are in the bundle, all four show the same
    /// VAT. Matching on the description alone (which carries the document number) let every one of
    /// those through, so bundle #707 came back with 121,444.66 of VAT against an actual 60,794.62
    /// for the whole bundle — of which 60,049.00 is the Customs Department's import VAT, not the
    /// shipping agent's.
    /// Two rows are therefore the same tax when the code and the amount match and they came from
    /// the same issuer, whatever document number each was read off.
    ///
    /// Of such a pair the tax invoice is the one to keep: its VAT is claimable this period
    /// (taxKind INPUT), while the billing note's is only deferred. So a kept row is upgraded
    /// rather than simply left alone.</summary>
    private static void MergeTaxRows(ParsedDocument doc, ParsedDocument part, string mainVendorName)
    {
        foreach (var line in part.Lines)
        {
            var code = (line.ExtCode ?? "").Trim().ToUpperInvariant();
            if (!TaxRowCodes.Contains(code)) continue;
            if (line.Amount == 0) continue;

            // Withholding tax is only ours when we are the one paying that invoice. The issuer is
            // named in issuerName when the read filled it and in the description either way.
            if (code == "WHT" && VisionPrompt.HasDistinctiveWords(mainVendorName)
                && ((line.IssuerName ?? "").Trim().Length > 0 || (line.Desc ?? "").Trim().Length > 0)
                && !VisionPrompt.IsSameVendor(line.IssuerName, mainVendorName)
                && !VisionPrompt.IsSameVendor(line.Desc, mainVendorName))
            {
                Log($"Dropped a withholding row of {line.Amount:N2} issued by {line.IssuerName} "
                    + $"— the bundle is paid to {mainVendorName}, so that tax was withheld by someone else");
                continue;
            }

            // A supporting group never sees the form, so it reads no vendor codes and a guessed one
            // would put the row under the wrong vendor's tab. Which rows belong to the customs
            // vendor is settled once, after every group has been merged (AssignTaxRowVendors) —
            // doing it here failed, because the duty rows that carry that vendor's code often
            // arrive in a LATER group than the customs VAT row that needs it.
            line.VendorCode = "";

            var twin = doc.Lines.FirstOrDefault(existing => SameTax(existing, line, code));
            if (twin != null)
            {
                // Logged rather than dropped in silence: two separate invoices from one supplier
                // can carry the same tax to the satang, and this is the one place that would
                // quietly lose the second one.
                Log($"Merged a repeated {code} row of {line.Amount:N2} from {(line.IssuerName ?? "").Trim()} "
                    + $"(doc {(line.TaxDocNo ?? "").Trim()}) into the row already read "
                    + $"(doc {(twin.TaxDocNo ?? "").Trim()}) — same issuer and amount");
                PreferTaxInvoice(twin, line);
                continue;
            }
            doc.Lines.Add(line);
        }
    }

    // The Customs Department's own tax id, and the English name the read normalises its Thai name
    // to (VisionPrompt.AgencyNames). A customs receipt is the one supporting page whose VAT is not
    // the supplier's.
    private const string CustomsTaxId = "0994000163011";

    /// <summary>Puts the duty rows, and the import VAT that comes with them, under the customs
    /// vendor instead of the shipping agent.
    ///
    /// Import duty and the VAT on the customs receipt are paid to the Customs Department, not to
    /// the agent who fronts the money — bundle #709 showed 60,049.00 of import VAT sitting in the
    /// agent's tax tab, which is the wrong supplier's document to post it on. The vendor code is
    /// the form's own: it is on the CUSTOMS FEE / EXCISE FEE cost row that the first request read.
    ///
    /// This runs once, after every supporting group has been merged, because the row that carries
    /// the code and the row that needs it routinely arrive in different requests.</summary>
    private static void AssignTaxRowVendors(ParsedDocument doc)
    {
        static string Vendor(LineItem l) => (l.VendorCode ?? "").Trim();
        static bool Is(LineItem l, string code) =>
            string.Equals((l.ExtCode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase);

        // Whichever duty row already carries the code, else the government fee row on the form.
        var govVendor = doc.Lines.Where(l => Is(l, "DUTY")).Select(Vendor).FirstOrDefault(v => v.Length > 0)
            ?? doc.Lines.Where(l => GovernmentFeeDesc().IsMatch(l.Desc ?? "")).Select(Vendor).FirstOrDefault(v => v.Length > 0)
            ?? "";
        if (govVendor.Length == 0) return; // nothing to point at: leave the rows with the main vendor

        foreach (var l in doc.Lines)
        {
            if (Vendor(l).Length > 0) continue;
            if (Is(l, "DUTY") || (Is(l, "VAT") && IsCustomsIssuer(l)))
                l.VendorCode = govVendor;
        }
    }

    // The form's own row for what is paid to customs — the one that carries that vendor's code.
    [GeneratedRegex(@"customs|ศุลกากร|excise|สรรพสามิต|import\s*duty|อากรขาเข้า", RegexOptions.IgnoreCase)]
    private static partial Regex GovernmentFeeDesc();

    private static bool IsCustomsIssuer(LineItem line) =>
        (line.IssuerTaxId ?? "").Trim() == CustomsTaxId
        || (line.IssuerName ?? "").Contains("Customs", StringComparison.OrdinalIgnoreCase)
        || (line.IssuerName ?? "").Contains("ศุลกากร", StringComparison.Ordinal);

    /// <summary>Two tax rows for the same money: same code, same amount, and either the same
    /// description or the same issuer. Rounding between an import entry and its receipt moves the
    /// base but not the tax, so the amount is compared at one satang.</summary>
    private static bool SameTax(LineItem a, LineItem b, string code) =>
        string.Equals((a.ExtCode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase)
        && Math.Abs(a.Amount - b.Amount) < 0.005
        && (string.Equals((a.Desc ?? "").Trim(), (b.Desc ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
            || SameIssuer(a, b));

    /// <summary>Two rows came from the same issuer when the tax ids match, or — when one of them
    /// was not read — when the names do: "BILLION LOGISTICS CO., LTD." and "Billion Logistics" are
    /// the same party, matched on distinctive words the way every other vendor test here does.</summary>
    private static bool SameIssuer(LineItem a, LineItem b)
    {
        var ta = (a.IssuerTaxId ?? "").Trim();
        var tb = (b.IssuerTaxId ?? "").Trim();
        if (ta.Length == 13 && tb.Length == 13) return ta == tb;
        return VisionPrompt.IsSameVendor(a.IssuerName, b.IssuerName)
            || VisionPrompt.IsSameVendor(b.IssuerName, a.IssuerName);
    }

    /// <summary>When the row already held is the billing note's and the new one is the tax
    /// invoice's, the tax invoice's details replace it: the VAT is the same money, but only the
    /// tax invoice makes it claimable this period, and the input-tax report needs that document's
    /// own number and date.</summary>
    private static void PreferTaxInvoice(LineItem kept, LineItem incoming)
    {
        if (!string.Equals(incoming.TaxKind, "INPUT", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(kept.TaxKind, "INPUT", StringComparison.OrdinalIgnoreCase)) return;

        kept.TaxKind = incoming.TaxKind;
        if ((incoming.TaxDocNo ?? "").Trim().Length > 0) kept.TaxDocNo = incoming.TaxDocNo;
        if ((incoming.TaxDocDate ?? "").Trim().Length > 0) kept.TaxDocDate = incoming.TaxDocDate;
        if ((incoming.IssuerName ?? "").Trim().Length > 0) kept.IssuerName = incoming.IssuerName;
        if ((incoming.IssuerTaxId ?? "").Trim().Length == 13) kept.IssuerTaxId = incoming.IssuerTaxId;
        if ((incoming.IssuerBranch ?? "").Trim().Length > 0) kept.IssuerBranch = incoming.IssuerBranch;
        if (incoming.BaseAmount > 0) kept.BaseAmount = incoming.BaseAmount;
        if ((incoming.Desc ?? "").Trim().Length > 0) kept.Desc = incoming.Desc;
    }

    /// <summary>The header's VAT and withholding totals have to be the sum of the rows now present,
    /// not what the first request worked out from the few pages it saw.
    ///
    /// Always assigned, including zero. The first request only sees the FORM SHIPPING EXPENSE page,
    /// which does not list the tax invoices at all, so any total it reports is a guess off some
    /// other figure on that sheet — one bundle came back with VAT 120,843.62 against an actual
    /// 745.62, with no VAT rows behind it. A total with no rows to support it is worse than no
    /// total: it looks reviewed, reconciles against nothing, and flows into the export.</summary>
    private static void RecountTaxTotals(ParsedDocument doc)
    {
        double Sum(string code) => doc.Lines
            .Where(l => string.Equals((l.ExtCode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase))
            .Sum(l => l.Amount);

        doc.Header["vatAmount"] = Math.Round(Sum("VAT"), 2);
        doc.Header["whtAmount"] = Math.Round(Sum("WHT"), 2);
    }

    // Writes each Gemini failure to the console/log with a timestamp so an intermittent, hard-to-catch
    // failure is recorded automatically \u2014 the user doesn't have to reproduce it in a debugger. Prefixed
    // so it's easy to filter the app log (e.g. findstr "[GeminiOcr]").
    private static void Log(string msg) =>
        Console.Error.WriteLine($"[GeminiOcr] {DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}");

    /// <summary>usageMetadata from a Gemini 200 response — what the request is billed on.
    ///
    /// promptTokenCount covers the prompt AND the page images (an image is charged as a block of
    /// tokens), which is why input dwarfs output on a scanned invoice. cachedContentTokenCount is
    /// already included in promptTokenCount by Google, so it is NOT added again here; it is only
    /// worth splitting out if we ever start caching context, which bills at a lower rate.
    ///
    /// Returns nulls when the field is absent rather than zeros: a zero would be stored as "this
    /// read cost nothing", and a missing figure is not the same as a free one.</summary>
    private static (int? In, int? Out) Usage(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("usageMetadata", out var u)) return (null, null);
            int? Read(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
            return (Read("promptTokenCount"), Read("candidatesTokenCount"));
        }
        catch
        {
            // Never fail a successful read over its own accounting.
            return (null, null);
        }
    }

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

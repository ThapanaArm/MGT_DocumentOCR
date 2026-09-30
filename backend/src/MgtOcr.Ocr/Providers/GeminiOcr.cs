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
            if (imgs.Count == 0)
                return (null, $"Could not rasterize '{Path.GetFileName(path)}' to images (renderer produced 0 pages \u2014 check the PDF rasterizer on the server)");

            // A prompt rule alone ("take lines from the FORM SHIPPING EXPENSE page") was not enough:
            // with 17 pages Gemini still took the first invoice's lines. When the text layer shows
            // which page the form is, put that page FIRST and say so explicitly up front.
            var prompt = VisionPrompt.Build(module);
            // How many of the images at the FRONT of the list are FORM SHIPPING EXPENSE pages.
            // Zero when the file has no form sheet. This is what the chunked reader splits on.
            var formCount = 0;
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
                    formCount = formPages.Count;
                    prompt = $"สำคัญ: {formPages.Count} ภาพแรกคือหน้า FORM SHIPPING EXPENSE (หน้า {string.Join(", ", formPageNos)} ของไฟล์) " +
                             "lines ต้องมาจากตารางในภาพเหล่านี้เท่านั้น ห้ามใช้รายการจากใบแจ้งหนี้/ใบเสร็จในภาพอื่นเป็น lines " +
                             (formPages.Count > 1
                                ? "ฟอร์มมีหลายใบเพราะมีหลาย PO — ให้รวมรายการชื่อเดียวกันของ vendor เดียวกันจากทุกใบเป็นแถวเดียวโดยบวกยอดกัน " +
                                  "และใส่ poRef เป็นเลข PO ทุกใบคั่นด้วย \", \" "
                                : "") +
                             "ภาพที่เหลือเป็นเอกสารประกอบ ให้ใช้เพื่อหายอดหัก ณ ที่จ่ายมาต่อท้าย lines ตามกติกาด้านล่าง\n\n" + prompt;
                }
            }
            // A long bundle cannot be read in one request: 22 pages timed out at 288s and again at
            // 610s, because what takes the time is Gemini LOOKING at that many images, not sending
            // them. So it is split — the FORM SHIPPING EXPENSE pages in one request (they carry the
            // header and the cost lines), the supporting invoices and receipts in groups after it
            // (they only contribute tax rows). Each request is small enough to answer in well under
            // a minute, and the first one is MORE accurate on its own than it was buried in twenty
            // other pages.
            if (isPdf && imgs.Count > ChunkThreshold)
                return await ChunkedAsync(imgs, formCount, mime, prompt, module, config, dpi);

            // A file short enough to read in one request still needs the customs rows pointed at the
            // customs vendor: the model fills vendorCode from the form when it can, but it leaves it
            // blank often enough that the tax would otherwise land on the shipping agent.
            var oneShot = await OneShotAsync(imgs, mime, prompt, module, config, dpi, TimeoutFor(imgs.Count));
            if (oneShot.Doc != null && module is "AP" or "II") AssignTaxRowVendors(oneShot.Doc);
            return oneShot;
        }
        catch (Exception ex)
        {
            return (null, $"Gemini request failed: {ex.GetType().Name}: {ex.Message}");
        }
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
        List<byte[]> imgs, string mime, string prompt, string module, AppConfig config, int dpi, TimeSpan timeout)
    {
        try
        {
            var pageCount = imgs.Count;
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
        string module, AppConfig config, int dpi)
    {
        // The form pages carry the cost table, but not the header: the vendor's tax id, invoice
        // date and payment terms are printed on the vendor's own invoice, which the reorder above
        // leaves sitting right after the form pages (it was the file's first page). Sending it
        // with them costs one image and is the only way the header request can see those fields.
        var leadCount = formCount > 0
            ? Math.Min(formCount + HeaderPages, imgs.Count)
            : Math.Min(LeadPages, imgs.Count);
        var lead = imgs.Take(leadCount).ToList();
        var rest = imgs.Skip(leadCount).ToList();

        Log($"Splitting a {imgs.Count}-page read into 1 + {(rest.Count + ChunkSize - 1) / ChunkSize} request(s) "
            + $"({leadCount} {(formCount > 0 ? "form" : "lead")} page(s) then groups of {ChunkSize})");

        var (doc, err) = await OneShotAsync(lead, mime, mainPrompt, module, config, dpi, TimeoutFor(lead.Count));
        if (doc == null) return (null, err);

        // The supporting pages are read without the form in view, so the main vendor's name — the
        // party we pay directly — has to travel with the prompt: withholding tax on any other
        // issuer's invoice in the bundle was already deducted and remitted by the shipping agent,
        // and is not ours to record.
        var mainVendorName = doc.Header.TryGetValue("vendorName", out var vn) ? vn?.ToString() ?? "" : "";
        var supporting = VisionPrompt.BuildSupporting(module, mainVendorName);
        var skipped = 0;
        for (var i = 0; i < rest.Count; i += ChunkSize)
        {
            var chunk = rest.Skip(i).Take(ChunkSize).ToList();
            var (part, perr) = await OneShotAsync(chunk, mime, supporting, module, config, dpi, TimeoutFor(chunk.Count));
            if (part == null)
            {
                skipped++;
                Log($"Supporting pages {leadCount + i + 1}-{leadCount + i + chunk.Count} could not be read: {perr}");
                continue;
            }
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

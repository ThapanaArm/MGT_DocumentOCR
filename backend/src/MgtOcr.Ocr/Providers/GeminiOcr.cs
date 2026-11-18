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
            if (imgs.Count == 0)
                return (null, $"Could not rasterize '{Path.GetFileName(path)}' to images (renderer produced 0 pages \u2014 check the PDF rasterizer on the server)");
            // The file page behind each image, kept in step with every reorder below, so the
            // positions the read reports ("image 1") land on the right page of the file.
            var pageNos = Enumerable.Range(1, imgs.Count).ToList();

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
                // A SCANNED bundle has no text layer, so neither regex above can see the form, and
                // a long one is then read as "the first six pages" — on PO 2130000427 that was the
                // JV, the GR sheets and the supplier's invoice, with the form on page 8 never in
                // the request that makes the lines: the goods were read, dropped as goods, and the
                // document came out with no cost lines at all. One cheap look at small thumbnails
                // finds the form page so it can be pulled to the front like a text-layer hit.
                if (!isPoRead && formIdxs.Count == 0 && imgs.Count > ChunkThreshold
                    && !string.Equals((config.OcrPageMemoryMode ?? "").Trim(), "on", StringComparison.OrdinalIgnoreCase))
                {
                    formIdxs = await FindFormPagesByVisionAsync(path, Math.Min(pageCount, MaxPages), config);
                    Log(formIdxs.Count > 0
                        ? $"No text layer for the form \u2014 found FORM SHIPPING EXPENSE on page(s) {string.Join(", ", formIdxs.Select(i => i + 1))} by looking"
                        : "No FORM SHIPPING EXPENSE page found, by text or by looking \u2014 reading the first pages instead");
                }
                var formPageNos = formIdxs.Select(i => i + 1).ToList();
                var formPages = new List<byte[]>();
                var formPagesNo = new List<int>();
                foreach (var idx in formIdxs)
                {
                    if (idx < imgs.Count) { formPages.Add(imgs[idx]); formPagesNo.Add(pageNos[idx]); }
                    else if (PdfRasterizer.RenderPageToJpeg(path, idx, dpi, quality) is { } extra) { formPages.Add(extra); formPagesNo.Add(idx + 1); }
                }
                if (formPages.Count > 0)
                {
                    foreach (var idx in formIdxs.Where(i => i < imgs.Count).OrderByDescending(i => i)) { imgs.RemoveAt(idx); pageNos.RemoveAt(idx); }
                    while (imgs.Count + formPages.Count > MaxPages) { imgs.RemoveAt(imgs.Count - 1); pageNos.RemoveAt(pageNos.Count - 1); }
                    imgs.InsertRange(0, formPages);
                    pageNos.InsertRange(0, formPagesNo);
                    formCount = formPages.Count;
                    prompt = AnchorPromptPrefix(isPoRead, formPages.Count, formPageNos, salesOrg) + prompt;
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
                    var plan = PlanPages(classification, imgs, pageNos, formCount, prompt, isPoRead, anchorRole, salesOrg);
                    if (plan.Changed)
                    {
                        (imgs, pageNos, formCount, prompt, planNote) = (plan.Imgs, plan.PageNos, plan.FormCount, plan.Prompt, plan.Note);
                        Log($"Page memory (on): {planNote}");
                    }
                }
            }

            // Focus (AP, 2026-11-18 "OCR Document Scope"): each image goes out with what it is and where
            // to look on it, from the page classification. Without a classification nothing changes.
            var roleByPage = memMode == "on" && classification != null
                ? classification.Pages.Where(p => p.Confidence >= FocusMinConfidence).ToDictionary(p => p.PageNo, p => p.Role)
                : new Dictionary<int, string>();
            foreach (var n in pageNos.Take(formCount)) roleByPage[n] = anchorRole;

            // Positions: the read reports where it found each value (frames on "View document"), and is
            // told where AP confirmed those fields on posted documents of the same type (Memory Focus).
            var positionHintUsed = false;
            if (config.OcrRecordPositions && module is "AP" or "II")
            {
                prompt += PositionInstructions();
                if (config.OcrPositionHints && !string.IsNullOrWhiteSpace(pageHint?.Positions))
                {
                    prompt += PositionHintText(pageHint.Positions);
                    positionHintUsed = true;
                }
            }

            var result = await ReadPlanAsync(imgs, pageNos, formCount, prompt);
            if (result.Doc != null) result.Doc.PositionHintUsed = positionHintUsed;
            var readNos = pageNos;
            if (planNote.Length > 0 && result.Doc != null && result.Doc.Lines.Count == 0)
            {
                // The narrowed read found nothing at all — never let page memory cost a document.
                Log("Page memory (on): the read with the page plan found no lines — reading again the old way");
                var again = await ReadPlanAsync(origImgs, origNos, origFormCount, origPrompt);
                if (again.Doc != null)
                {
                    again.Doc.TokensIn = (again.Doc.TokensIn ?? 0) + (result.Doc.TokensIn ?? 0);
                    again.Doc.TokensOut = (again.Doc.TokensOut ?? 0) + (result.Doc.TokensOut ?? 0);
                    result = again;
                    readNos = origNos;
                    planNote = "Page memory: the narrowed read found no lines, so the whole document was read again";
                }
            }

            if (classifyTask != null && classification == null && memMode == "shadow")
            {
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

            string? RoleOfImage(List<int> nos, int i) => i < nos.Count && roleByPage.TryGetValue(nos[i], out var r) ? r : null;

            // The read itself: one request for a short file, lead + supporting groups for a long one.
            async Task<(ParsedDocument? Doc, string? Error)> ReadPlanAsync(List<byte[]> planImgs, List<int> planNos, int planFormCount, string planPrompt)
            {
                // A long bundle cannot be read in one request: 22 pages timed out at 288s and again at
                // 610s, because what takes the time is Gemini LOOKING at that many images, not sending
                // them. So it is split — the FORM SHIPPING EXPENSE pages in one request (they carry the
                // header and the cost lines), the supporting invoices and receipts in groups after it
                // (they only contribute tax rows).
                if (isPdf && planImgs.Count > ChunkThreshold)
                    return MgtDeliveryCostsOnly(ShippingFormText.ApplyMultiForm(path, module, salesOrg,
                        ApplyPositions(await ChunkedAsync(planImgs, planFormCount, mime, planPrompt, module, config, dpi, salesOrg, apDocCategory,
                            i => RoleOfImage(planNos, i)), planNos)),
                        module, salesOrg, apDocCategory);

                // A file short enough to read in one request still needs the customs rows pointed at the
                // customs vendor: the model fills vendorCode from the form when it can, but it leaves it
                // blank often enough that the tax would otherwise land on the shipping agent.
                var focus = FocusMapText(Enumerable.Range(0, planImgs.Count).Select(i => RoleOfImage(planNos, i)).ToList(), 1, isPoRead);
                var oneShot = await OneShotAsync(planImgs, mime, focus + planPrompt, module, config, dpi, TimeoutFor(planImgs.Count));
                if (oneShot.Doc != null && module is "AP" or "II")
                {
                    AssignTaxRowVendors(oneShot.Doc);
                    VisionPrompt.FillLineVatFromSourceDocs(oneShot.Doc);
                    VisionPrompt.FillLineVatFromTaxRows(oneShot.Doc);
                }
                return MgtDeliveryCostsOnly(ShippingFormText.ApplyMultiForm(path, module, salesOrg, ApplyPositions(oneShot, planNos)),
                    module, salesOrg, apDocCategory);
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
    /// the front of the images (shared by the text-layer regex and page memory).</summary>
    private static string AnchorPromptPrefix(bool isPoRead, int count, IEnumerable<int> filePageNos, string salesOrg = "") => isPoRead
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
             (count > 1 && CompanyRules.IsMgt(salesOrg)
                // MGT reads as before (its own form; the GLC multi-PO split is not MGT's).
                ? "ฟอร์มมีหลายใบเพราะมีหลาย PO — ให้รวมรายการชื่อเดียวกันของ vendor เดียวกันจากทุกใบเป็นแถวเดียวโดยบวกยอดกัน " +
                  "และใส่ poRef เป็นเลข PO ทุกใบคั่นด้วย \", \" "
                : count > 1
                ? "ฟอร์มมีหลายใบเพราะมีหลาย PO (1 ใบต่อ 1 PO) — ห้ามรวมแถวข้ามใบ: อ่านทุกใบแยกกัน 1 แถวต่อ 1 รายการต่อ 1 ใบ " +
                  "และใส่ lines[].poRef = เลข PO. NO. ที่หัวของใบที่แถวนั้นอยู่ (แถวที่ AMOUNT เป็น - หรือ 0 ยังข้ามเหมือนเดิม); " +
                  "header.poRef = เลข PO ทุกใบคั่นด้วย \", \" เรียงตามที่พิมพ์ใน MEMO (CONSOLIDATED … Pos #…) " +
                  "และ header.poList = [{\"po\": เลข PO. NO., \"goodsInvoiceNo\": เลขในช่อง INVOICE NO. ของใบนั้น, \"grDoc\": \"\"}] หนึ่งรายการต่อใบ เรียงตาม MEMO "
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

    /// <summary>MGT, "Planned delivery costs" (AP, 2026-11-16): the read may only return the rows of
    /// the form's VENDOR CODE block (Freight / Local Handling / Insurance / Pallet / Intertek, extCode
    /// COST) and the tax rows. A scanned bundle has no text layer to find the form by, so the model
    /// sometimes took the goods supplier's invoice instead — 16 x IBC CANOLA OIL 40,000 AUD in the
    /// DETAIL of a delivery-cost document, and again from each group of supporting pages. Those goods
    /// rows are dropped here, and the note says so; the goods are posted by their own document.</summary>
    private static (ParsedDocument? Doc, string? Error) MgtDeliveryCostsOnly(
        (ParsedDocument? Doc, string? Error) read, string module, string salesOrg, string apDocCategory)
    {
        var doc = read.Doc;
        if (doc == null || module is not ("AP" or "II") || !CompanyRules.IsMgt(salesOrg)
            || VisionPrompt.IsInventoryPo(apDocCategory)) return read;
        static bool Keep(LineItem l)
        {
            var ext = (l.ExtCode ?? "").Trim().ToUpperInvariant();
            if (ext is "COST" or "VAT" or "WHT" or "DUTY") return true;
            return System.Text.RegularExpressions.Regex.IsMatch(l.Desc ?? "",
                @"FREIGHT|HANDLING|INSURANCE|PALLET|INTERTEK|IMPORT\s*TAX|DUTY|^\s*VAT|WITHHOLDING|ภาษี|อากร|ค่าระวาง|ประกัน",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        FillInvoiceNos(doc);
        FillVendorDateAndTaxId(doc);
        DropCustomsFeesBilledByAgent(doc);
        var dropped = doc.Lines.Where(l => !Keep(l)).ToList();
        if (dropped.Count == 0) return read;
        doc.Lines = doc.Lines.Where(Keep).ToList();
        // The header total followed the goods invoice too (40,000 AUD); the costs are the document.
        var costs = doc.Lines.Where(l => (l.ExtCode ?? "").Trim().Equals("COST", StringComparison.OrdinalIgnoreCase)).ToList();
        if (costs.Count > 0) doc.Header["totalAmount"] = Math.Round(costs.Sum(l => l.Amount), 2);
        var note = $"ตัดรายการสินค้าออก {dropped.Count} แถว (เอกสาร Planned delivery costs ใช้เฉพาะตาราง VENDOR CODE ท้ายฟอร์ม): "
            + string.Join(", ", dropped.Take(3).Select(l => (l.Desc ?? "").Trim()).Where(d => d.Length > 0))
            + (doc.Lines.Any(l => (l.ExtCode ?? "").Trim().Equals("COST", StringComparison.OrdinalIgnoreCase)) ? ""
                : " — ไม่พบรายการค่าใช้จ่ายจากฟอร์ม ตรวจว่าไฟล์มีหน้า FORM SHIPPING EXPENSE แล้วกด Re-read");
        doc.ConfidenceNote = doc.ConfidenceNote.Length > 0 ? $"{doc.ConfidenceNote} / {note}" : note;
        Log($"[MGT] dropped {dropped.Count} goods line(s) from a delivery-cost read");
        return read;
    }

    /// <summary>MGT (AP, 2026-11-18, PO 2130000720): a Customs Department receipt for a FEE only (the
    /// 200.00 customs fee 2801-092746, overtime) is already on the FORM — CUSTOMS FEE / OT CHARGE under
    /// the agent's LOCAL HANDLING (S.N.R. 4000496; PRD nets it off as Prepayment "Customs Fee" 200.00).
    /// The Customs Department's own document carries the import duty and the import VAT only
    /// (PRD 5100000747), so the fee read off the receipt as a DUTY row must not become a document
    /// of its own. The receipt stays listed, filed under Local and as included in the agent's invoice.</summary>
    private static void DropCustomsFeesBilledByAgent(ParsedDocument doc)
    {
        static bool Fee(string d) => System.Text.RegularExpressions.Regex.IsMatch(d ?? "",
            @"FEE|ค่าธรรมเนียม|OVER\s*TIME|ล่วงเวลา", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(d ?? "", @"IMPORT\s*(DUTY|TAX)|EXCISE|INTERIOR|อากรขาเข้า|สรรพสามิต|มหาดไทย",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var fees = doc.Lines.Where(l => (l.ExtCode ?? "").Trim().Equals("DUTY", StringComparison.OrdinalIgnoreCase) && Fee(l.Desc ?? "")).ToList();
        if (fees.Count == 0) return;
        doc.Lines = doc.Lines.Except(fees).ToList();
        var agentInv = Convert.ToString(doc.Header.GetValueOrDefault("invoiceNo"))?.Trim() ?? "";
        if (doc.Header.TryGetValue("sourceDocs", out var v) && v is List<object?> docs)
            foreach (var d in docs.OfType<Dictionary<string, object?>>())
            {
                var issuer = Convert.ToString(d.GetValueOrDefault("issuerName")) ?? "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(issuer, "CUSTOMS|ศุลกากร", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                var vat = double.TryParse(Convert.ToString(d.GetValueOrDefault("vatAmount"), System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var x) ? x : 0;
                var total = double.TryParse(Convert.ToString(d.GetValueOrDefault("totalAmount"), System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0;
                if (vat != 0 || !fees.Any(f => Math.Abs(f.Amount - total) < 0.005)) continue;
                d["costType"] = "Local";
                if (agentInv.Length > 0 && string.IsNullOrWhiteSpace(Convert.ToString(d.GetValueOrDefault("includedIn")))) d["includedIn"] = agentInv;
            }
        var note = $"ค่าธรรมเนียมกรมศุลฯ {string.Join(", ", fees.Select(f => f.Amount.ToString("N2")))} อยู่ใน Local Handling ของตัวแทนแล้ว (FORM) — ไม่ตั้งเป็นเอกสารแยก";
        doc.ConfidenceNote = doc.ConfidenceNote.Length > 0 ? $"{doc.ConfidenceNote} / {note}" : note;
        Log($"[MGT] {note}");
    }

    /// <summary>MGT (AP, 2026-11-17, #855 / #861): the FORM SHIPPING EXPENSE carries no invoice date
    /// and no tax id, and on a scanned bundle the header request does not see the vendor's own
    /// invoice, so both came back empty and SAP refused the document ("Fill in mandatory field
    /// 'DocumentDate'"). The supporting pages did read that invoice — as a sourceDoc and as its VAT
    /// row — so the header takes them from there: the document of the invoicing party (its name
    /// shares a distinctive word with the header's vendor). Only empty fields are filled.</summary>
    private static void FillVendorDateAndTaxId(ParsedDocument doc)
    {
        static string Get(Dictionary<string, object?> d, string k) => Convert.ToString(d.GetValueOrDefault(k))?.Trim() ?? "";
        var vendor = Get(doc.Header, "vendorName");
        static IEnumerable<string> Words(string s) => System.Text.RegularExpressions.Regex
            .Split(s.ToUpperInvariant().Replace(".", ""), @"[^A-Z0-9ก-๙]+")
            .Where(w => w.Length >= 3 && w is not ("THE" or "CO" or "LTD" or "COMPANY" or "LIMITED" or "THAILAND" or "PUBLIC"
                or "GROUP" or "INTERNATIONAL" or "LOGISTICS" or "SERVICE" or "SERVICES" or "TRANSPORT" or "SHIPPING"
                or "FREIGHT" or "EXPRESS" or "TRADING" or "CORPORATION"));
        var mine = Words(vendor).ToHashSet();
        if (mine.Count == 0) return;
        bool Ours(string issuer) => Words(issuer).Any(mine.Contains);

        if (Get(doc.Header, "invoiceDate").Length == 0
            && doc.Header.TryGetValue("sourceDocs", out var v) && v is List<object?> docs)
        {
            var date = docs.OfType<Dictionary<string, object?>>()
                .Where(d => Ours(Get(d, "issuerName")))
                .Select(d => Get(d, "docDate"))
                .FirstOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^\d{4}-\d{2}-\d{2}$"));
            date ??= doc.Lines.Where(l => Ours(l.IssuerName ?? "")).Select(l => (l.TaxDocDate ?? "").Trim())
                .FirstOrDefault(x => System.Text.RegularExpressions.Regex.IsMatch(x, @"^\d{4}-\d{2}-\d{2}$"));
            if (date != null)
            {
                doc.Header["invoiceDate"] = date;
                Log($"[MGT] invoice date {date} from the vendor's own document");
            }
        }
        if (Get(doc.Header, "vendorTaxId").Length == 0)
        {
            var tax = doc.Lines.Where(l => Ours(l.IssuerName ?? ""))
                .Select(l => System.Text.RegularExpressions.Regex.Replace(l.IssuerTaxId ?? "", @"\D", ""))
                .FirstOrDefault(x => x.Length == 13);
            if (tax != null)
            {
                doc.Header["vendorTaxId"] = tax;
                Log($"[MGT] vendor tax id {tax} from the vendor's own tax invoice");
            }
        }
    }

    /// <summary>MGT (AP, 2026-11-16): each cost row's supplier invoice, from the invoice table of the
    /// forwarder's receipt (sourceDocs[].invoiceRefs — RE2512206 lists LOG101185LCH OCEAN FREIGHT and
    /// LOG101202LCH TRANSPORTATION). The receipt usually sits in a group of supporting pages, which
    /// never sees the cost rows, so the pairing is done here: Freight takes the freight invoice,
    /// Local Handling the other one. Rows that already name an invoice are left alone.</summary>
    private static void FillInvoiceNos(ParsedDocument doc)
    {
        var refs = new List<(string No, string Desc)>();
        // Only the main supplier's own receipts say which of ITS invoices a cost row is. A receipt of
        // somebody it paid on our behalf (DHL's tax invoice W1736878 on S.N.R.'s bundle) lists that
        // party's numbers, which became S.N.R.'s Reference (TEST 5100000075, AP 2026-11-18).
        static HashSet<string> Words(string s) => System.Text.RegularExpressions.Regex
            .Split((s ?? "").ToUpperInvariant().Replace(".", ""), @"[^A-Z0-9ก-๙]+")
            .Where(w => w.Length >= 3 && w is not ("THE" or "CO" or "LTD" or "COMPANY" or "LIMITED" or "THAILAND" or "PUBLIC"
                or "GROUP" or "INTERNATIONAL" or "LOGISTICS" or "SERVICE" or "SERVICES" or "TRANSPORT" or "SHIPPING"
                or "FREIGHT" or "EXPRESS" or "TRADING" or "CORPORATION" or "IMPORT" or "EXPORT" or "FORWARDING"))
            .ToHashSet();
        var mainWords = Words(Convert.ToString(doc.Header.GetValueOrDefault("vendorName")) ?? "");
        if (doc.Header.TryGetValue("sourceDocs", out var v) && v is List<object?> docs)
            foreach (var d in docs.OfType<Dictionary<string, object?>>())
                if ((mainWords.Count == 0 || Words(Convert.ToString(d.GetValueOrDefault("issuerName")) ?? "").Overlaps(mainWords))
                    && d.TryGetValue("invoiceRefs", out var r) && r is List<object?> rows)
                    foreach (var row in rows.OfType<Dictionary<string, object?>>())
                    {
                        var no = Convert.ToString(row.GetValueOrDefault("invoiceNo"))?.Trim() ?? "";
                        if (no.Length > 0 && !refs.Any(x => x.No.Equals(no, StringComparison.OrdinalIgnoreCase)))
                            refs.Add((no, Convert.ToString(row.GetValueOrDefault("description")) ?? ""));
                    }
        if (refs.Count == 0) return;
        static bool IsFreight(string t) => System.Text.RegularExpressions.Regex.IsMatch(t ?? "", @"FREIGHT|OCEAN|AIR\s*FREIGHT|ค่าระวาง",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        static bool IsInsurance(string t) => System.Text.RegularExpressions.Regex.IsMatch(t ?? "", @"INSURANCE|ประกัน",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in doc.Lines)
        {
            if (!(l.ExtCode ?? "").Trim().Equals("COST", StringComparison.OrdinalIgnoreCase) || (l.InvoiceNo ?? "").Trim().Length > 0) continue;
            if (IsInsurance(l.Desc)) continue;   // the insurer's document uses the PO
            var want = IsFreight(l.Desc);
            var hit = refs.FirstOrDefault(x => !used.Contains(x.No) && IsFreight(x.Desc) == want && !IsInsurance(x.Desc));
            if (hit.No == null) continue;
            l.InvoiceNo = hit.No;
            used.Add(hit.No);
        }
        if (used.Count > 0) Log($"[MGT] supplier invoice numbers from the receipt: {string.Join(", ", used)}");
    }

    /// <summary>Reads ONE value from a cut-out of the page (a frame the person moved on "View
    /// document"). Small request: one picture of a few centimetres. Returns the value or an error.</summary>
    public static async Task<(string? Value, string? Error, int? TokensIn, int? TokensOut)> ReadRegionAsync(
        byte[] jpeg, string field, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey)) return (null, "GeminiApiKey is empty in config", null, null);
        var kind = field switch
        {
            "invoiceDate" => "วันที่ ตอบรูปแบบ YYYY-MM-DD (ปี พ.ศ. ให้แปลงเป็น ค.ศ.)",
            "subTotal" or "vatAmount" or "totalAmount" => "จำนวนเงิน ตอบเป็นตัวเลขล้วน ไม่มีลูกน้ำ ไม่มีสกุลเงิน",
            "vendorTaxId" => "เลขประจำตัวผู้เสียภาษี 13 หลัก ตัวเลขล้วน",
            _ => "ข้อความตามที่พิมพ์ในภาพ",
        };
        var prompt = $"ภาพนี้คือส่วนหนึ่งของเอกสาร อ่านค่าของช่อง \"{field}\" ที่อยู่ในภาพ ({kind}) " +
                     "ตอบเป็น JSON เท่านั้น: {\"value\": \"...\"} ถ้าในภาพไม่มีค่าที่อ่านได้ให้ตอบ {\"value\": \"\"}";
        var body = new
        {
            contents = new[] { new { role = "user", parts = (object)new List<object>
            {
                new { text = prompt },
                new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(jpeg) } },
            } } },
            generationConfig = new { temperature = 0, maxOutputTokens = 2048, responseMimeType = "application/json" },
        };
        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) return (null, $"Gemini HTTP {(int)resp.StatusCode}: {Trunc(respText, 300)}", null, null);
            var (tin, tout) = Usage(respText);
            var raw = ExtractText(respText).Trim();
            var st = raw.IndexOf('{'); var en = raw.LastIndexOf('}');
            if (st < 0 || en <= st) return (null, "Gemini returned no value", tin, tout);
            using var j = JsonDocument.Parse(raw[st..(en + 1)]);
            var v = j.RootElement.TryGetProperty("value", out var vv) ? vv.ToString().Trim() : "";
            return (v, null, tin, tout);
        }
        catch (Exception ex) { return (null, $"{ex.GetType().Name}: {ex.Message}", null, null); }
    }

    // The header values whose position the read reports (AP, 2026-11-18).
    private static readonly string[] PositionFields =
    [
        "invoiceNo", "invoiceDate", "vendorName", "vendorTaxId", "poRef", "currency",
        "subTotal", "vatAmount", "totalAmount", "goodsSupplier", "goodsInvoiceNo", "linesTable",
    ];

    /// <summary>Asks the main read to say where it found each value: one extra key in the same JSON.
    /// The answer is a few hundred tokens; the values themselves are still read from the images.</summary>
    private static string PositionInstructions() =>
        "\n\nเพิ่ม key \"_positions\" ใน JSON คำตอบเดียวกัน (นอกเหนือจาก header และ lines) บอกตำแหน่งที่อ่านแต่ละค่าได้ " +
        "รูปแบบ {\"invoiceNo\":{\"image\":1,\"box\":[ymin,xmin,ymax,xmax]}, ..., \"lines\":[{\"image\":1,\"box\":[ymin,xmin,ymax,xmax]}, ...]} " +
        "พิกัด 0-1000 ของภาพนั้น, image = ลำดับภาพในคำขอนี้ (เริ่มที่ 1) " +
        "สำหรับ " + string.Join(", ", PositionFields) + " (linesTable = กรอบของตารางที่ใช้ทำ lines) " +
        "และ \"lines\" = กรอบของแถวที่อ่านแต่ละแถวของ lines เรียงตามลำดับ lines เดียวกัน (แถวที่ไม่ได้อ่านจากภาพใดภาพหนึ่งใส่ null) " +
        "ค่าที่ไม่พบไม่ต้องใส่ ห้ามเดาตำแหน่ง";

    private static string? ExtractPositions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains("_positions")) return null;
        var st = raw.IndexOf('{'); var en = raw.LastIndexOf('}');
        if (st < 0 || en <= st) return null;
        try
        {
            using var d = JsonDocument.Parse(raw[st..(en + 1)]);
            return d.RootElement.TryGetProperty("_positions", out var pos) && pos.ValueKind == JsonValueKind.Object
                ? pos.GetRawText() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Turns the read's "_positions" (images of its own request) into FILE pages:
    /// header._positions = {field: "page:ymin,xmin,ymax,xmax"} and each line's SourceBox. Only the
    /// main request reports positions, and its images are the first ones of pageNos.</summary>
    private static (ParsedDocument? Doc, string? Error) ApplyPositions((ParsedDocument? Doc, string? Error) read, List<int> pageNos)
    {
        var doc = read.Doc;
        if (doc == null || string.IsNullOrWhiteSpace(doc.PositionsJson)) return read;
        string? Box(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("box", out var b) || b.ValueKind != JsonValueKind.Array) return null;
            var n = b.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)Math.Round(x.GetDouble())).ToList();
            if (n.Count != 4 || n[2] <= n[0] || n[3] <= n[1]) return null;
            var img = e.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.Number ? im.GetInt32() : 1;
            if (img < 1 || img > pageNos.Count) return null;
            return $"{pageNos[img - 1]}:{string.Join(",", n.Select(v => Math.Clamp(v, 0, 1000)))}";
        }
        try
        {
            using var d = JsonDocument.Parse(doc.PositionsJson);
            var fields = new Dictionary<string, object?>();
            foreach (var p in d.RootElement.EnumerateObject())
            {
                if (p.Name == "lines")
                {
                    if (p.Value.ValueKind != JsonValueKind.Array) continue;
                    var i = 0;
                    foreach (var e in p.Value.EnumerateArray())
                    {
                        if (i < doc.Lines.Count && Box(e) is { } lb) doc.Lines[i].SourceBox = lb;
                        i++;
                    }
                }
                else if (Box(p.Value) is { } hb) fields[p.Name] = hb;
            }
            if (fields.Count > 0) doc.Header["_positions"] = fields;
            Log($"Positions: {fields.Count} header field(s), {doc.Lines.Count(l => l.SourceBox.Length > 0)} line(s)");
        }
        catch (JsonException) { /* positions are a display aid; a bad answer never fails the read */ }
        return read;
    }

    /// <summary>Which pages are a FORM SHIPPING EXPENSE sheet, by looking: every page at thumbnail
    /// size in one small request. Used only when the text layer cannot say (a scanned bundle).
    /// Returns zero-based page indexes; empty when none is found or the request fails — the read
    /// then goes on exactly as before, so this can only help.</summary>
    private static async Task<List<int>> FindFormPagesByVisionAsync(string path, int pages, AppConfig config)
    {
        try
        {
            var thumbs = PdfRasterizer.RenderPagesToJpeg(path, maxPages: pages, dpi: 60, quality: 60);
            if (thumbs.Count < 2) return [];
            var parts = new List<object>
            {
                new
                {
                    text = $"มี {thumbs.Count} ภาพ เรียงเป็นหน้า 1 ถึง {thumbs.Count} ของไฟล์เดียวกัน " +
                           "ให้หาหน้าที่หัวกระดาษพิมพ์ว่า FORM SHIPPING EXPENSE (ฟอร์มสรุปค่าขนส่ง มีตาราง DESCRIPTION / SHIPPING BROKER HANDLING / " +
                           "MGT'S GR EST. HANDLING และกล่อง VENDOR CODE ท้ายฟอร์ม) — ไม่ใช่ใบแจ้งหนี้ ใบเสร็จ ใบสั่งซื้อ หรือใบรับสินค้า " +
                           "ตอบเป็น JSON เท่านั้น: {\"pages\": [เลขหน้าเริ่มที่ 1]} ถ้าไม่มีให้ตอบ {\"pages\": []}",
                },
            };
            parts.AddRange(thumbs.Select(b => (object)new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(b) } }));
            var body = new
            {
                contents = new[] { new { role = "user", parts = (object)parts } },
                generationConfig = new { temperature = 0, maxOutputTokens = 4096, responseMimeType = "application/json" },
            };
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.GeminiModel}:generateContent?key={config.GeminiApiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            using var resp = await Http.SendAsync(req, cts.Token);
            var respText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"Form page finder: HTTP {(int)resp.StatusCode}: {Trunc(respText, 300)}");
                return [];
            }
            var raw = ExtractText(respText).Trim();
            if (raw.StartsWith("```")) raw = raw.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
            using var j = JsonDocument.Parse(raw);
            if (!j.RootElement.TryGetProperty("pages", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            return arr.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n - 1 : -1)
                .Where(i => i >= 0 && i < thumbs.Count)
                .Distinct().OrderBy(i => i).ToList();
        }
        catch (Exception ex)
        {
            Log($"Form page finder failed: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

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
            const string engineName = "Gemini";
            var model = config.GeminiModel;
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
                            // Pages actually sent in THIS request. Needed to report cost and
                            // time per page; nothing else in the pipeline knows the figure.
                            parsedDoc.Pages = pageCount;
                            parsedDoc.PositionsJson = ExtractPositions(raw);
                            // Measuring where the time goes (Megachem, 28 Oct 2026): the model's own
                            // "thinking" is billed and timed apart from the answer, so it is kept
                            // separately; one line per request shows pages / time / tokens.
                            parsedDoc.ThinkingTokens = ThinkingTokens(respText);
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
                        lastErr = $"{engineName} returned HTTP 200 but no usable JSON"
                            + (finish != null
                                ? $" (finishReason={finish}{(finish == "MAX_TOKENS" ? " — response was cut off; raise maxOutputTokens" : "")})"
                                : " (empty or safety-blocked response)")
                            + $". extractedTextLen={raw.Length}, respLen={respText.Length}";
                        Log(lastErr);
                        return (null, lastErr);
                    }
                    var status = (int)resp.StatusCode;
                    var transient = status is 429 or 500 or 502 or 503 or 504;
                    lastErr = $"{engineName} HTTP {status} (model={model}"
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
                    lastErr = $"{engineName} did not answer within {timeout.TotalSeconds:0}s "
                        + $"(model={model}, {pageCount} page(s) at {dpi} dpi, attempt {attempt}/{maxAttempts}). "
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
                    lastErr = $"{engineName} connection error (attempt {attempt}/{maxAttempts}): {ex.Message}";
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
        string module, AppConfig config, int dpi, string salesOrg = "", string apDocCategory = "",
        Func<int, string?>? roleOfImage = null)
    {
        string Focus(int start, int count) => roleOfImage == null ? ""
            : FocusMapText(Enumerable.Range(start, count).Select(roleOfImage).ToList(), 1, VisionPrompt.IsInventoryPo(apDocCategory));
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

        var (doc, err) = await OneShotAsync(lead, mime, Focus(0, lead.Count) + mainPrompt, module, config, dpi, TimeoutFor(lead.Count));
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
                try { results[gi] = await OneShotAsync(g.Pages, mime, Focus(leadCount + g.Start, g.Pages.Count) + supporting, module, config, dpi, TimeoutFor(g.Pages.Count)); }
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
            doc.Pages = (doc.Pages ?? 0) + (part.Pages ?? 0);
            if (part.ThinkingTokens != null) doc.ThinkingTokens = (doc.ThinkingTokens ?? 0) + part.ThinkingTokens;
            MergeTaxRows(doc, part, mainVendorName);
            MergeSourceDocs(doc, part);
        }

        AssignTaxRowVendors(doc);
        VisionPrompt.FillLineVatFromSourceDocs(doc);
        VisionPrompt.FillLineVatFromTaxRows(doc);
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

    /// <summary>Appends the supporting documents a later group of pages found (header.sourceDocs)
    /// to the document's list, once each: the same issuer, document number and COSTS heading read
    /// twice — a billing note and its tax invoice on two pages — is one document.</summary>
    private static void MergeSourceDocs(ParsedDocument doc, ParsedDocument part)
    {
        if (!part.Header.TryGetValue("sourceDocs", out var add) || add is not List<object?> incoming || incoming.Count == 0) return;
        if (!doc.Header.TryGetValue("sourceDocs", out var cur) || cur is not List<object?> list)
            doc.Header["sourceDocs"] = list = [];
        static string Key(object? o) => o is Dictionary<string, object?> d
            ? $"{d.GetValueOrDefault("issuerName")}|{d.GetValueOrDefault("docNo")}|{d.GetValueOrDefault("costType")}".ToUpperInvariant()
            : "";
        var seen = list.Select(Key).ToHashSet();
        foreach (var o in incoming)
            if (o is Dictionary<string, object?> && seen.Add(Key(o))) list.Add(o);
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
    internal static void AssignTaxRowVendors(ParsedDocument doc)
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

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MgtOcr.Core.Config;

namespace MgtOcr.Ocr.Providers;

// Page memory (see PageMemory.cs): one small Gemini call that says what every page of an AP bundle
// is, from the top part of each page only. Kept in its own file so the extraction code in
// GeminiOcr.cs only gains the few lines that use the answer.
public static partial class GeminiOcr
{
    /// <summary>Classifies the first <paramref name="pageCount"/> pages. Never throws: any failure
    /// (no key, HTTP error, unreadable answer) returns null and the read carries on as before.</summary>
    internal static async Task<PageClassification?> ClassifyPagesAsync(
        string path, int pageCount, string anchorRole, PageMemoryHint? hint, AppConfig config)
    {
        if (string.IsNullOrEmpty(config.GeminiApiKey) || pageCount < 1) return null;
        var sw = Stopwatch.StartNew();
        try
        {
            var strips = PdfRasterizer.RenderHeaderStripsToJpeg(path, pageCount);
            if (strips.Count == 0) return null;

            var parts = new List<object> { new { text = ClassifyPrompt(anchorRole) } };
            // Up to two pictures of the anchor page from documents people already checked and
            // posted. The FORM SHIPPING EXPENSE is our own form, so it looks the same whoever the
            // shipping agent is — one good picture of it is worth more than any description.
            var refs = (hint?.References ?? []).Where(r => r.Role == anchorRole && r.HeaderJpeg.Length > 0).Take(2).ToList();
            foreach (var r in refs)
            {
                parts.Add(new { text = $"ตัวอย่างอ้างอิง (ไม่ใช่หน้าของเอกสารนี้): ส่วนบนของหน้า {r.Role} จากเอกสารที่ตรวจและบันทึกแล้ว" });
                parts.Add(new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(r.HeaderJpeg) } });
            }
            parts.Add(new { text = $"--- ส่วนบนของทุกหน้าในเอกสาร ({strips.Count} หน้า) ---" });
            for (var i = 0; i < strips.Count; i++)
            {
                parts.Add(new { text = $"Page {i + 1}" });
                parts.Add(new { inline_data = new { mime_type = "image/jpeg", data = Convert.ToBase64String(strips[i]) } });
            }

            var body = new
            {
                contents = new[] { new { role = "user", parts = (object)parts } },
                generationConfig = new { temperature = 0, maxOutputTokens = 8192, responseMimeType = "application/json" },
            };
            var model = string.IsNullOrWhiteSpace(config.OcrPageMemoryModel) ? config.GeminiModel : config.OcrPageMemoryModel;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={config.GeminiApiKey}";
            var payload = JsonSerializer.Serialize(body);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(45 + strips.Count * 2, 60, 180)));
                    using var resp = await Http.SendAsync(req, cts.Token);
                    var respText = await resp.Content.ReadAsStringAsync(cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var pages = ParseClassification(ExtractText(respText), strips.Count);
                        if (pages == null)
                        {
                            Log($"Page memory: classification answer could not be read ({Trunc(ExtractText(respText), 200)})");
                            return null;
                        }
                        // Keep the header strip of anchor pages: if this document is posted, its
                        // strip becomes the reference picture for the next bundle.
                        foreach (var p in pages.Where(p => p.Role == anchorRole && p.Confidence >= AnchorMinConfidence))
                            p.HeaderJpeg = strips[p.PageNo - 1];
                        var (tin, tout) = Usage(respText);
                        var thinking = ThinkingTokens(respText);
                        Log($"Page memory: classified {strips.Count} page(s) in {sw.ElapsedMilliseconds} ms with {refs.Count} reference(s): "
                            + string.Join(", ", pages.Select(p => $"{p.PageNo}:{p.Role}")));
                        return new PageClassification { Pages = pages, TokensIn = tin, TokensOut = tout, ThinkingTokens = thinking, DurationMs = (int)sw.ElapsedMilliseconds };
                    }
                    var status = (int)resp.StatusCode;
                    Log($"Page memory: classification HTTP {status} (attempt {attempt}/2): {Trunc(respText, 300)}");
                    if (status is not (429 or 500 or 502 or 503 or 504)) return null;
                }
                catch (OperationCanceledException) { Log($"Page memory: classification timed out (attempt {attempt}/2)"); }
                catch (HttpRequestException ex) { Log($"Page memory: connection error (attempt {attempt}/2): {ex.Message}"); }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
            return null;
        }
        catch (Exception ex)
        {
            Log($"Page memory: classification failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>usageMetadata.thoughtsTokenCount — what the model spent "thinking" before it answered.
    /// Billed as output and part of the wait, but not in candidatesTokenCount. Null when absent.</summary>
    internal static int? ThinkingTokens(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("usageMetadata", out var u)
                   && u.TryGetProperty("thoughtsTokenCount", out var t) && t.TryGetInt32(out var n) ? n : null;
        }
        catch { return null; }
    }

    // ---- Position hints (Ocr:PositionHints:Enabled) ----
    private static readonly string[] PositionFields =
        ["vendorName", "vendorTaxId", "invoiceNo", "invoiceDate", "poRef", "subTotal", "vatAmount", "totalAmount", "linesTable"];

    /// <summary>Added to the main request's prompt: report where each value was found, and — when a
    /// record from an earlier document exists — where to look first.</summary>
    internal static string PositionInstructions(string? knownPositions)
    {
        var sb = new StringBuilder("\n\n");
        sb.Append("เพิ่ม key \"_positions\" ใน JSON คำตอบเดียวกัน (นอกเหนือจาก header และ lines): ตำแหน่งที่อ่านแต่ละค่าได้ ")
          .Append("รูปแบบ {\"invoiceNo\":{\"image\":1,\"box\":[ymin,xmin,ymax,xmax]}} ")
          .Append("พิกัด 0-1000 ของภาพนั้น, image = ลำดับภาพในคำขอนี้ (เริ่มที่ 1) ")
          .Append("สำหรับ ").Append(string.Join(", ", PositionFields))
          .Append(" (linesTable = กรอบของตารางรายการ) ค่าที่ไม่พบไม่ต้องใส่");
        if (!string.IsNullOrWhiteSpace(knownPositions))
            sb.Append("\n\nช่องข้อมูลกับตำแหน่งที่พบในเอกสารประเภทเดียวกันครั้งก่อน แยกตามเลขประจำตัวผู้เสียภาษีของผู้ขาย ")
              .Append("(ชื่อ key = ช่องที่ต้องใส่ค่า, image = ลำดับภาพ, box = [ymin,xmin,ymax,xmax] พิกัด 0-1000): ")
              .Append("ถ้าเลขผู้เสียภาษีของผู้ขายในเอกสารนี้ตรงกับรายการใด ให้มองหาค่าของแต่ละช่องที่กรอบของรายการนั้นก่อน ")
              .Append("ถ้าไม่ตรงกับรายการใดให้ข้ามข้อมูลส่วนนี้ ตำแหน่งจริงอาจเลื่อนไปได้ และห้ามเดาค่าจากตำแหน่ง ให้อ่านค่าจากภาพเสมอ\n")
              .Append(knownPositions);
        return sb.ToString();
    }

    /// <summary>The "_positions" object of an answer, as compact JSON; null when there is none.</summary>
    internal static string? ExtractPositions(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains("_positions")) return null;
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[s..(e + 1)]);
            return doc.RootElement.TryGetProperty("_positions", out var p) && p.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(p) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A page is taken as the anchor only at or above this confidence.</summary>
    internal const double AnchorMinConfidence = 0.8;
    /// <summary>How long a "shadow" read waits for the page classifier after the read itself is done.</summary>
    private static readonly TimeSpan ShadowWait = TimeSpan.FromSeconds(3);
    /// <summary>A page is left out of the read only at or above this confidence.</summary>
    internal const double SkipMinConfidence = 0.85;

    private static string ClassifyPrompt(string anchorRole) =>
        "คุณจะเห็น \"ส่วนบน\" (ประมาณ 1 ใน 3 จากด้านบน) ของทุกหน้าในไฟล์เอกสารตั้งหนี้ของบริษัท Megachem " +
        "ไฟล์หนึ่งรวมเอกสารหลายใบ เช่น ฟอร์มสรุปค่าใช้จ่าย ใบกำกับภาษี ใบแจ้งหนี้ ใบเสร็จ ใบขนสินค้า packing list B/L\n" +
        "งาน: บอกว่าแต่ละหน้าเป็นเอกสารประเภทไหน จากหัวกระดาษ ชื่อเอกสาร และผู้ออกเอกสาร — ไม่ต้องอ่านตัวเลขหรือรายการ\n\n" +
        "ประเภท (role) ที่ใช้ได้เท่านั้น:\n" +
        "- FORM_SHIPPING_EXPENSE: ฟอร์มภายในของ Megachem หัวข้อ \"FORM SHIPPING EXPENSE\" (ตารางสรุปค่าใช้จ่ายขนส่ง/นำเข้า)\n" +
        "- PURCHASE_ORDER: ใบสั่งซื้อ \"PURCHASE ORDER\" ที่ Megachem ออก\n" +
        "- TAX_INVOICE: ใบกำกับภาษี / Tax Invoice (รวมถึง \"ใบแจ้งหนี้/ใบกำกับภาษี\" และ \"ใบเสร็จรับเงิน/ใบกำกับภาษี\")\n" +
        "- INVOICE: ใบแจ้งหนี้ / Invoice / Debit Note / ใบวางบิล ที่ไม่ได้เขียนว่าใบกำกับภาษี\n" +
        "- RECEIPT: ใบเสร็จรับเงิน / Receipt ที่ไม่ได้เขียนว่าใบกำกับภาษี\n" +
        "- CUSTOMS: เอกสารกรมศุลกากร ใบขนสินค้าขาเข้า ใบเสร็จอากร\n" +
        "- WHT_CERTIFICATE: หนังสือรับรองการหักภาษี ณ ที่จ่าย (50 ทวิ)\n" +
        "- DELIVERY_ORDER: ใบปล่อยสินค้า D/O\n" +
        "- PACKING_LIST: Packing List / ใบบรรจุหีบห่อ\n" +
        "- BILL_OF_LADING: Bill of Lading / Sea Waybill / Air Waybill\n" +
        "- CERTIFICATE: Certificate of Analysis / Certificate of Origin / Insurance Certificate / เอกสารรับรองอื่นที่ไม่มีการเรียกเก็บเงิน\n" +
        "- CONTINUATION: หน้าต่อจากหน้าก่อน (ไม่มีหัวเอกสารของตัวเอง เช่น ตารางต่อ หน้า 2 of 3)\n" +
        "- BLANK: หน้าว่าง\n" +
        "- OTHER: อื่น ๆ หรือไม่แน่ใจ\n\n" +
        $"สำคัญที่สุด: หาหน้า {anchorRole} ให้เจอ ถ้ามีภาพตัวอย่างอ้างอิง ให้เทียบหน้าตาของหัวกระดาษกับตัวอย่าง\n" +
        "confidence = ความมั่นใจ 0 ถึง 1; ถ้าเห็นไม่ชัดหรือไม่แน่ใจ ให้ตอบ OTHER หรือให้ confidence ต่ำ\n" +
        "ต้องตอบครบทุกหน้า เรียงตามเลขหน้า เป็น JSON เท่านั้น รูปแบบ:\n" +
        "{\"pages\":[{\"page\":1,\"role\":\"TAX_INVOICE\",\"confidence\":0.95}]}";

    /// <summary>Page memory for a document that was read BEFORE page memory existed (backfill):
    /// the anchor pages from the text layer (certain, free) and — only when <paramref name="classify"/>
    /// — the classifier's view of every page (one billed Gemini call). Nothing is read for extraction,
    /// so every page is recorded as not used. Null when the file has nothing to record.</summary>
    public static async Task<(List<PageInfo>? Map, int? TokensIn, int? TokensOut)> BackfillPageMapAsync(
        string path, string apDocCategory, bool classify, PageMemoryHint? hint, AppConfig config)
    {
        var pageCount = PdfRasterizer.PageCount(path);
        if (pageCount < 1) return (null, null, null);
        var isPo = VisionPrompt.IsInventoryPo(apDocCategory);
        var anchorRole = PageRoles.AnchorFor(isPo);
        var anchors = pageCount > 1 ? FindAnchorPageIndexes(path, isPo).Select(i => i + 1).ToList() : [];
        PageClassification? cls = null;
        if (classify && pageCount >= Math.Max(2, config.OcrPageMemoryMinPages))
            cls = await ClassifyPagesAsync(path, Math.Min(pageCount, 50), anchorRole, hint, config);
        var map = BuildPageMap(path, cls, anchors, anchorRole, []);
        return (map, cls?.TokensIn, cls?.TokensOut);
    }

    internal record PagePlan(bool Changed, List<byte[]> Imgs, List<int> PageNos, int FormCount, string Prompt, string Note);

    /// <summary>Mode "on": turns the classification into the images to read. Works on copies — the
    /// caller keeps the originals for the fall-back read.
    /// 1. No anchor found by the text layer → the classifier's anchor pages go to the front, with the
    ///    same instruction the regex path adds.
    /// 2. Pages the classifier is sure carry no money (PageRoles.NoMoney) are left out — never an
    ///    anchor page, and never file pages 1–2, which ChunkedAsync sends with the anchor as the
    ///    vendor's own invoice (the header fields are printed there).</summary>
    internal static PagePlan PlanPages(PageClassification cls, List<byte[]> imgs, List<int> pageNos, int formCount,
        string prompt, bool isPoRead, string anchorRole)
    {
        var newImgs = new List<byte[]>(imgs);
        var newNos = new List<int>(pageNos);
        var newForm = formCount;
        var newPrompt = prompt;
        var notes = new List<string>();

        if (formCount == 0)
        {
            var anchors = cls.Pages
                .Where(p => p.Role == anchorRole && p.Confidence >= AnchorMinConfidence && newNos.Contains(p.PageNo))
                .Select(p => p.PageNo).ToList();
            if (anchors.Count > 0)
            {
                var front = anchors.Select(n => newImgs[newNos.IndexOf(n)]).ToList();
                foreach (var n in anchors)
                {
                    var idx = newNos.IndexOf(n);
                    newImgs.RemoveAt(idx); newNos.RemoveAt(idx);
                }
                newImgs.InsertRange(0, front);
                newNos.InsertRange(0, anchors);
                newForm = anchors.Count;
                newPrompt = AnchorPromptPrefix(isPoRead, anchors.Count, anchors) + prompt;
                notes.Add($"{anchorRole} found on page {string.Join(", ", anchors)} by page memory");
            }
        }

        var protectedNos = new HashSet<int>(newNos.Take(newForm)) { 1, 2 };
        var skip = cls.Pages.Where(p => !p.Keep && !protectedNos.Contains(p.PageNo)).Select(p => p.PageNo).ToHashSet();
        var skipped = new List<int>();
        for (var i = newNos.Count - 1; i >= newForm; i--)
        {
            if (!skip.Contains(newNos[i])) continue;
            skipped.Add(newNos[i]);
            newImgs.RemoveAt(i); newNos.RemoveAt(i);
        }
        if (skipped.Count > 0)
        {
            skipped.Sort();
            var roles = cls.Pages.Where(p => skipped.Contains(p.PageNo)).Select(p => p.Role).Distinct();
            notes.Add($"left out {skipped.Count} page(s) with no amounts ({string.Join(", ", roles)}: page {string.Join(", ", skipped)})");
        }

        var changed = notes.Count > 0;
        var note = changed ? $"Page memory: read {newImgs.Count} of {imgs.Count} page(s); " + string.Join("; ", notes) : "";
        return new PagePlan(changed, newImgs, newNos, newForm, newPrompt, note);
    }

    /// <summary>What gets stored in ocr.DocPageMap for this read. Pages found by the text-layer regex
    /// are recorded as certain (Method TEXT) whatever the classifier said, and their header strip is
    /// kept: a text-layer PDF that is posted teaches page memory what the anchor looks like on scans.
    /// Null when there is nothing to record.</summary>
    internal static List<PageInfo>? BuildPageMap(string path, PageClassification? cls,
        List<int> regexAnchorNos, string anchorRole, List<int> readNos)
    {
        if (cls == null && regexAnchorNos.Count == 0) return null;
        var map = cls?.Pages.Select(p => new PageInfo
        {
            PageNo = p.PageNo, Role = p.Role, Confidence = p.Confidence, Method = p.Method, Keep = p.Keep,
            HeaderJpeg = p.HeaderJpeg, AiRole = p.Role, AiConfidence = p.Confidence,
        }).ToList() ?? [];
        foreach (var n in regexAnchorNos)
        {
            var p = map.FirstOrDefault(x => x.PageNo == n);
            if (p == null) { p = new PageInfo { PageNo = n }; map.Add(p); }
            p.Role = anchorRole; p.Confidence = 1; p.Method = "TEXT"; p.Keep = true;
            if (p.HeaderJpeg == null)
            {
                try { p.HeaderJpeg = PdfRasterizer.RenderHeaderStripToJpeg(path, n - 1); }
                catch (Exception ex) { Log($"Page memory: header strip of page {n} could not be rendered: {ex.Message}"); }
            }
        }
        var read = readNos.ToHashSet();
        foreach (var p in map) p.Used = read.Contains(p.PageNo);
        return map.OrderBy(p => p.PageNo).ToList();
    }

    /// <summary>Parses the classifier's JSON. Unknown roles become OTHER; pages the answer left out
    /// stay UNCLASSIFIED (and are therefore kept). Null when the answer is not usable at all.</summary>
    internal static List<PageInfo>? ParseClassification(string raw, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        if (s < 0 || e <= s) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw[s..(e + 1)]);
            if (!doc.RootElement.TryGetProperty("pages", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var result = Enumerable.Range(1, pageCount)
                .Select(n => new PageInfo { PageNo = n, Role = PageRoles.Unclassified, Confidence = 0, Method = "AI" })
                .ToList();
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("page", out var pg) || !pg.TryGetInt32(out var n) || n < 1 || n > pageCount) continue;
                var role = el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                    ? (r.GetString() ?? "").Trim().ToUpperInvariant() : "";
                if (!PageRoles.All.Contains(role)) role = PageRoles.Other;
                var conf = el.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1) : 0.5;
                var p = result[n - 1];
                p.Role = role;
                p.Confidence = Math.Round(conf, 3);
                p.Keep = !(PageRoles.NoMoney.Contains(role) && conf >= SkipMinConfidence);
            }
            return result;
        }
        catch (JsonException) { return null; }
    }
}

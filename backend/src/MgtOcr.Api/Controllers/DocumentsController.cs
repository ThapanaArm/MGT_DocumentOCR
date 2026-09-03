using System.Text.Json;
using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Config;
using MgtOcr.Core.Json;
using MgtOcr.Core.Mapping;
using MgtOcr.Sap;
using MgtOcr.Data;
using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Api.Controllers;

// Ported from the "documents" section of app/main.py (lines 402-877) — upload/list/get/put/delete/
// reocr/chat/chat-fix/rawtext/file/map/learn/split/payload/post. One controller, matching the
// original single-file layout, since these all share the same document-lifecycle state machine.
[ApiController]
public class DocumentsController(DocumentRepository repo, MasterRepository masters, OcrEngine ocr,
    SapClient sap, AppConfig config) : ControllerBase
{
    private static readonly (string Id, string Label)[] ApDocCategories =
    [
        ("INVENTORY", "การบันทึกรายการตั้งหนี้เจ้า - Inventory"),
        ("EXPENSE", "การบันทึกรายการตั้งหนี้เจ้า - Expense"),
        ("FIXED_ASSET_BUDGET", "การบันทึกรายการตั้งหนี้เจ้า - Fixed Asset กรณีคุมงบประมาณ"),
        ("FIXED_ASSET_NO_BUDGET", "การบันทึกรายการตั้งหนี้เจ้า - Fixed Asset กรณีไม่คุมงบประมาณ"),
        ("SUB_CONTRACT", "การบันทึกรายการตั้งหนี้เจ้า - Sub Contract"),
    ];

    private static readonly HashSet<string> Modules = ["AP", "SO", "II", "PODP"];
    private static readonly Dictionary<string, (string Label, string EnvVar)> ChatFixProviderLabel = new()
    {
        ["claude"] = ("Claude", "ANTHROPIC_API_KEY"), ["gemini"] = ("Gemini", "GEMINI_API_KEY"), ["openai"] = ("ChatGPT", "OPENAI_API_KEY"),
    };

    private static string ValidateModule(string? module)
    {
        var m = (module ?? "").ToUpperInvariant();
        if (!Modules.Contains(m)) throw new HttpApiException(400, "module ต้องเป็น AP, SO, II หรือ PODP");
        return m;
    }

    private static string ValidateApDocCategory(string module, string? apDocCategory)
    {
        var cat = module == "AP" ? (apDocCategory ?? "").Trim().ToUpperInvariant() : "";
        if (cat.Length > 0 && !ApDocCategories.Any(c => c.Id == cat)) throw new HttpApiException(400, "ประเภทเอกสารไม่ถูกต้อง");
        return cat;
    }

    [HttpGet("api/ap-doc-categories")]
    public IActionResult GetApDocCategories() => Ok(ApDocCategories.Select(c => new { id = c.Id, label = c.Label }));

    [HttpGet("api/samples/{module}")]
    public IActionResult Samples(string module)
    {
        var list = DemoData.Demo.GetValueOrDefault(module.ToUpperInvariant(), []);
        return Ok(list.Select((s, i) => new { index = i, name = s.Name, label = s.Label, confidence = s.Confidence }));
    }

    [HttpPost("api/documents/sample")]
    public async Task<IActionResult> CreateFromSample([FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var module = ValidateModule(body.GetStr("module"));
        var apCat = ValidateApDocCategory(module, body.GetStr("apDocCategory"));
        var idx = (int)Num(body.Get("index"));
        var pd = DemoData.DemoDoc(module, idx);
        var ext = ExtConversion.ToExtDict(pd);
        var user = body.GetStr("user") is { Length: > 0 } u ? u : "system";
        var fileName = pd.SampleName ?? "sample.pdf";
        var docId = await repo.CreateDocumentAsync(module, ext, fileName, "", 0, user, apCat);
        return Ok(await repo.GetDocumentAsync(docId));
    }

    [HttpPost("api/documents/upload")]
    public async Task<IActionResult> Upload([FromForm] string module, [FromForm] string user, [FromForm] string ocr_,
        [FromForm(Name = "apDocCategory")] string? apDocCategory, [FromForm] IFormFile file)
    {
        var mod = ValidateModule(module);
        var apCat = ValidateApDocCategory(mod, apDocCategory);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fname = ExtConversion.SafeName(file.FileName);
        var stored = Path.Combine(config.UploadDir, $"{stamp}_{fname}");
        await using (var fs = System.IO.File.Create(stored))
            await file.CopyToAsync(fs);
        var size = (int)new FileInfo(stored).Length;

        var t0 = DateTime.UtcNow;
        var pd = await ocr.ExtractAsync(stored, mod, string.IsNullOrEmpty(ocr_) ? "auto" : ocr_);
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        var ext = ExtConversion.ToExtDict(pd);
        var docId = await repo.CreateDocumentAsync(mod, ext, fname, stored, size, string.IsNullOrEmpty(user) ? "system" : user, apCat, durationMs);
        var outDoc = await repo.GetDocumentAsync(docId);
        outDoc["ocrNote"] = pd.Note ?? "";
        return Ok(outDoc);
    }

    [HttpGet("api/documents")]
    public async Task<IActionResult> ListDocuments([FromQuery] string module = "", [FromQuery] string status = "",
        [FromQuery] string apDocCategory = "", [FromQuery] int limit = 100) =>
        Ok(await repo.ListDocumentsAsync(module, status, apDocCategory, limit));

    [HttpGet("api/documents/{docId:int}")]
    public async Task<IActionResult> ReadDocument(int docId) => Ok(await repo.GetDocumentAsync(docId));

    [HttpPut("api/documents/{docId:int}")]
    public async Task<IActionResult> SaveDocument(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, "เอกสารถูกส่งเข้า SAP แล้ว แก้ไขไม่ได้");
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        var header = body.Get("header") as Dictionary<string, object?> ?? (Dictionary<string, object?>)doc["header"]!;
        await repo.UpdateHeaderAsync(docId, module, header);
        if (body.Get("lines") is List<object?> linesRaw)
            await repo.SaveLinesAsync(module, docId, linesRaw.OfType<Dictionary<string, object?>>().ToList());
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END, MapStatus=NULL, MapMessage=NULL WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, module, "UPDATE", body.GetStr("user") is { Length: > 0 } u ? u : "system",
            detail: "แก้ไขข้อมูลเอกสาร", fileName: doc.GetStr("fileName"));
        return Ok(await repo.GetDocumentAsync(docId));
    }

    [HttpPost("api/documents/{docId:int}/category")]
    public async Task<IActionResult> SetDocCategory(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var cat = (body.GetStr("apDocCategory")).Trim().ToUpperInvariant();
        if (cat.Length > 0 && !ApDocCategories.Any(c => c.Id == cat)) throw new HttpApiException(400, "ประเภทเอกสารไม่ถูกต้อง");
        var docT = DocumentTables.ForId(docId).Doc;
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET ApDocCategory=@cat WHERE DocId=@docId", new { cat = cat.Length > 0 ? cat : null, docId });
        var doc = await repo.GetDocumentAsync(docId);
        await repo.LogAuditAsync(docId, doc.GetStr("module"), "UPDATE", body.GetStr("user") is { Length: > 0 } u ? u : "system",
            detail: "เปลี่ยนประเภทเอกสารเป็น: " + (cat.Length > 0 ? cat : "-"), fileName: doc.GetStr("fileName"));
        return Ok(doc);
    }

    [HttpDelete("api/documents/{docId:int}")]
    public async Task<IActionResult> DeleteDocument(int docId, [FromQuery] string user = "system")
    {
        var doc = await repo.GetDocumentAsync(docId);
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"DELETE FROM {docT} WHERE DocId=@docId", new { docId });
        var header = (Dictionary<string, object?>)doc["header"]!;
        var docNo = header.GetStr("invoiceNo") is { Length: > 0 } inv ? inv : header.GetStr("poNo");
        await repo.LogAuditAsync(docId, module, "DELETE", user, detail: "ลบเอกสาร", docNo: docNo, fileName: doc.GetStr("fileName"));
        return Ok(new { ok = true });
    }

    [HttpPost("api/documents/{docId:int}/reocr")]
    public async Task<IActionResult> ReocrDocument(int docId, [FromBody] Dictionary<string, object?>? rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody ?? new());
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? row = await GetDbInstance().QueryOneAsync($"SELECT Module, StoredPath, FileName FROM {docT} WHERE DocId=@docId", new { docId });
        if (row == null) throw new HttpApiException(404, "ไม่พบเอกสาร");
        string storedPath = row.StoredPath ?? ""; string module = row.Module; string fileNameOnDisk = row.FileName ?? "";

        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, "เอกสารถูกส่งเข้า SAP แล้ว อ่านใหม่ไม่ได้");
        if (doc.GetStr("status") == "SPLIT") throw new HttpApiException(400, "เอกสารนี้ถูก Split ไปแล้ว อ่านใหม่ไม่ได้ (เก็บไว้เป็นเอกสารอ้างอิงของเอกสารที่แยกไป)");
        if (doc.Get("sourceDocId") != null) throw new HttpApiException(400, "เอกสารนี้เป็นส่วนที่ Split มาจากเอกสารอื่น อ่านใหม่ไม่ได้ (จะทับรายการที่แยกไว้ด้วยข้อมูลเอกสารเต็ม)");
        if (storedPath.Length == 0 || !System.IO.File.Exists(storedPath)) throw new HttpApiException(400, "ไม่พบไฟล์ต้นฉบับ (เอกสารนี้อาจสร้างจากชุดตัวอย่าง)");

        var t0 = DateTime.UtcNow;
        var pd = await ocr.ExtractAsync(storedPath, module, body.GetStr("ocr") is { Length: > 0 } o ? o : "auto");
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        var filled = await repo.ApplyVendorMemoryAsync(module, pd.Header);
        if (filled.Count > 0)
        {
            var note = "เติมข้อมูลจากคู่ค้าเดิมที่เคยยืนยันไว้ (ไม่ได้อ่านจากเอกสารนี้โดยตรง): " + string.Join(", ", filled);
            pd.ConfidenceNote = pd.ConfidenceNote.Length > 0 ? $"{pd.ConfidenceNote} / {note}" : note;
        }
        var dn = DocumentRepository.Denorm(module, pd.Header);
        var rawText = pd.RawText.Length > 20000 ? pd.RawText[..20000] : pd.RawText;
        await using (var conn = await GetDbAsync())
        {
            await conn.ExecuteAsync($"""
                UPDATE {docT} SET OcrProvider=@provider, OcrConfidence=@confidence, OcrConfidenceNote=@confidenceNote,
                    OcrTokensIn=@tokensIn, OcrTokensOut=@tokensOut, OcrCost=@cost, OcrInputCost=@costIn, OcrOutputCost=@costOut,
                    OcrCostCurrency=@costCurrency, OcrDurationMs=@durationMs, HeaderJson=@headerJson, RawText=@rawText,
                    DocNo=@docNo, DocDate=@docDate, PostingDate=@postingDate, PartnerName=@partnerName, PartnerTaxId=@partnerTaxId,
                    Currency=@currency, SubTotal=@subTotal, VatRate=@vatRate, VatAmount=@vatAmount, WhtAmount=@whtAmount,
                    TotalAmount=@totalAmount, Status='NEW', MapStatus=NULL, MapMessage=NULL, PartnerCode=NULL, ShipToCode=NULL,
                    SapPartnerCode=NULL, SapShipToCode=NULL, UpdatedAt=SYSDATETIME()
                  WHERE DocId=@docId
                """, new
            {
                provider = pd.Provider, confidence = pd.Confidence, confidenceNote = pd.ConfidenceNote,
                tokensIn = pd.TokensIn, tokensOut = pd.TokensOut, cost = pd.Cost, costIn = pd.CostIn, costOut = pd.CostOut,
                costCurrency = pd.CostCurrency, durationMs,
                headerJson = JsonSerializer.Serialize(pd.Header, PyJson.Options), rawText,
                docNo = dn.Get("DocNo"), docDate = dn.Get("DocDate"), postingDate = dn.Get("PostingDate"),
                partnerName = dn.Get("PartnerName"), partnerTaxId = dn.Get("PartnerTaxId"), currency = dn.Get("Currency"),
                subTotal = dn.Get("SubTotal"), vatRate = dn.Get("VatRate"), vatAmount = dn.Get("VatAmount"),
                whtAmount = dn.Get("WhtAmount"), totalAmount = dn.Get("TotalAmount"), docId,
            });
        }
        await repo.SaveLinesAsync(module, docId, pd.Lines.Select(ExtConversion.ToLineDict).ToList());
        await repo.LogAuditAsync(docId, module, "REOCR", body.GetStr("user") is { Length: > 0 } u ? u : "system",
            detail: "อ่านเอกสารใหม่", fileName: fileNameOnDisk, ocrProvider: pd.Provider);
        var outDoc = await repo.GetDocumentAsync(docId);
        outDoc["ocrNote"] = pd.Note is { Length: > 0 } n ? n : $"อ่านเอกสารใหม่เรียบร้อย ({pd.Provider})";
        return Ok(outDoc);
    }

    [HttpGet("api/documents/{docId:int}/chat")]
    public async Task<IActionResult> ReadChatHistory(int docId) => Ok(await repo.GetChatHistoryAsync(docId));

    [HttpGet("api/documents/{docId:int}/chat/{chatId:int}/image")]
    public async Task<IActionResult> ChatImage(int docId, int chatId)
    {
        var chatT = DocumentTables.ForId(docId).Chat;
        dynamic? r = await GetDbInstance().QueryOneAsync($"SELECT ImagePath FROM {chatT} WHERE DocId=@docId AND ChatId=@chatId", new { docId, chatId });
        string? path = r?.ImagePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) throw new HttpApiException(404, "ไม่พบภาพ");
        return PhysicalFile(Path.GetFullPath(path), "application/octet-stream");
    }

    [HttpPost("api/documents/{docId:int}/chat-fix")]
    public async Task<IActionResult> ChatFixDocument(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var message = body.GetStr("message").Trim();
        var imageDataUrl = body.GetStr("image").Trim();
        var user = body.GetStr("user") is { Length: > 0 } u ? u : "system";
        if (message.Length == 0 && imageDataUrl.Length == 0) throw new HttpApiException(400, "กรุณาพิมพ์ข้อความหรือแนบภาพ");
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, "เอกสารถูกส่งเข้า SAP แล้ว แก้ไขไม่ได้");

        string? imageB64 = null; var imageMediaType = "image/png"; byte[]? imageBytes = null; var imageExt = ".png";
        if (imageDataUrl.Length > 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(imageDataUrl, @"^data:(image/([a-zA-Z0-9.+-]+));base64,(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) throw new HttpApiException(400, "รูปแบบภาพไม่ถูกต้อง");
            imageMediaType = m.Groups[1].Value; var subtype = m.Groups[2].Value; imageB64 = m.Groups[3].Value;
            imageExt = "." + (System.Text.RegularExpressions.Regex.IsMatch(subtype, "^[a-zA-Z0-9]+$") ? subtype : "png");
            try { imageBytes = Convert.FromBase64String(imageB64); }
            catch { throw new HttpApiException(400, "ถอดรหัสภาพไม่สำเร็จ"); }
        }

        var provider = body.GetStr("provider") is { Length: > 0 } pr && ChatFixProviderLabel.ContainsKey(pr) ? pr : "claude";
        var history = await repo.GetChatHistoryAsync(docId);
        await repo.SaveChatMessageAsync(docId, "user", message, imageBytes, imageExt, user);

        var promptMessage = message.Length > 0 ? message : "ดูภาพที่แนบมา แล้วแก้ไขข้อมูลในเอกสารให้ถูกต้องตามสิ่งที่เห็นในภาพ";
        var module = doc.GetStr("module");
        var header = (Dictionary<string, object?>)doc["header"]!;
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var result = await ChatFix.ChatFixDocumentAsync(module, header, lines, history, promptMessage, imageB64, imageMediaType, provider, config);
        if (result == null)
        {
            var (label, envVar) = ChatFixProviderLabel[provider];
            throw new HttpApiException(400, $"เชื่อมต่อ {label} ไม่สำเร็จ หรือยังไม่ได้ตั้งค่า {envVar} ใน .env");
        }

        await repo.SaveChatMessageAsync(docId, "assistant", result.Reply, null, ".png", "AI");
        await repo.UpdateHeaderAsync(docId, module, result.Header);
        await repo.SaveLinesAsync(module, docId, result.Lines);
        var docT = DocumentTables.For(module).Doc;
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END, MapStatus=NULL, MapMessage=NULL WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, module, "UPDATE", user, detail: "แก้ไขผ่านแชท AI: " + message[..Math.Min(200, message.Length)], fileName: doc.GetStr("fileName"));
        var outDoc = await repo.GetDocumentAsync(docId);
        return Ok(new { reply = result.Reply, document = outDoc });
    }

    [HttpGet("api/documents/{docId:int}/rawtext")]
    public async Task<IActionResult> RawText(int docId)
    {
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? r = await GetDbInstance().QueryOneAsync($"SELECT RawText FROM {docT} WHERE DocId=@docId", new { docId });
        if (r == null) throw new HttpApiException(404, "ไม่พบเอกสาร");
        string text = r.RawText ?? "";
        return Ok(new { text });
    }

    [HttpGet("api/documents/{docId:int}/file")]
    public async Task<IActionResult> DocumentFile(int docId)
    {
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? d = await GetDbInstance().QueryOneAsync($"SELECT StoredPath, FileName FROM {docT} WHERE DocId=@docId", new { docId });
        string? path = d?.StoredPath;
        if (d == null || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) throw new HttpApiException(404, "ไม่พบไฟล์ต้นฉบับ");
        string fileName = d.FileName ?? "";
        return PhysicalFile(Path.GetFullPath(path), "application/octet-stream", fileName);
    }

    [HttpPost("api/documents/{docId:int}/map")]
    public async Task<IActionResult> MapDocument(int docId, [FromBody] Dictionary<string, object?>? rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody ?? new());
        var doc = await repo.GetDocumentAsync(docId);
        var manual = body.Get("manual") as Dictionary<string, object?> ?? new();
        var newHeader = body.Get("header") as Dictionary<string, object?>;
        var newLines = body.Get("lines") as List<object?>;
        var oldHeader = (Dictionary<string, object?>)doc["header"]!;
        var oldLines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var edited = manual.Count > 0 ||
            (newHeader != null && !DictEquals(newHeader, oldHeader)) ||
            (newLines != null && !ListEquals(newLines, oldLines));

        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc; var lineT = DocumentTables.For(module).Line;
        var header = oldHeader; var lines = oldLines;
        if (newHeader != null) { await repo.UpdateHeaderAsync(docId, module, newHeader); header = newHeader; }
        if (newLines != null)
        {
            await repo.SaveLinesAsync(module, docId, newLines.OfType<Dictionary<string, object?>>().ToList());
            lines = (List<Dictionary<string, object?>>)(await repo.GetDocumentAsync(docId))["lines"]!;
        }

        var masterData = await masters.LoadForMappingAsync();
        var res = MappingEngine.RunMapping(module, header, lines, masterData, manual);
        var resLines = (List<Dictionary<string, object?>>)res["lines"]!;
        var resHeader = (Dictionary<string, object?>)res["header"]!;

        await using (var conn = await GetDbAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var r = resLines[i];
                var u = r.Get("uom") as Dictionary<string, object?> ?? new();
                var uStatus = u.GetStr("status");
                await conn.ExecuteAsync($"""
                    UPDATE {lineT} SET MaterialCode=@materialCode, MapStatus=@status, MapMethod=@method,
                        SapQty=@sapQty, SapUom=@sapUom, UomFactor=@uomFactor, SapMaterialCode=@sapMaterialCode, SapUomIso=@sapUomIso
                    WHERE DocId=@docId AND ItemNo=@itemNo
                    """, new
                {
                    materialCode = r.GetStr("code") is { Length: > 0 } c ? c : null, status = r.Get("status"), method = r.Get("method"),
                    sapQty = uStatus is "ok" or "convert" ? u.Get("sapQty") : null, sapUom = u.GetStr("sapUom") is { Length: > 0 } su ? su : null,
                    uomFactor = uStatus is "ok" or "convert" ? u.Get("factor") : null,
                    sapMaterialCode = r.GetStr("sapCode") is { Length: > 0 } sc ? sc : null, sapUomIso = u.GetStr("iso") is { Length: > 0 } iso ? iso : null,
                    docId, itemNo = lines[i].Get("itemNo"),
                }, tx);
            }
            var partnerRow = (resHeader.Get("customer") ?? resHeader.Get("vendor")) as Dictionary<string, object?>;
            var shipToRow = resHeader.Get("shipTo") as Dictionary<string, object?>;
            var partner = partnerRow.GetStr("code") is { Length: > 0 } pc ? pc : null;
            var shipTo = shipToRow.GetStr("code") is { Length: > 0 } stc ? stc : null;
            var sapPartner = partnerRow.GetStr("sapCode") is { Length: > 0 } spc ? spc : null;
            var sapShipTo = shipToRow.GetStr("sapCode") is { Length: > 0 } sstc ? sstc : null;
            var pass = res.Get("pass") is true;
            await conn.ExecuteAsync($"""
                UPDATE {docT} SET SapPartnerCode=@sapPartner, SapShipToCode=@sapShipTo,
                    PartnerCode=@partner, ShipToCode=@shipTo, MapStatus=@mapStatus, MapMessage=@mapMessage,
                    Status=CASE WHEN Status='POSTED' THEN 'POSTED' WHEN @pass=1 THEN 'MAPPED' ELSE 'INCOMPLETE' END,
                    UpdatedAt=SYSDATETIME() WHERE DocId=@docId
                """, new
            {
                sapPartner, sapShipTo, partner, shipTo, mapStatus = pass ? "PASS" : "FAIL",
                mapMessage = JsonSerializer.Serialize(new { errors = res["errors"], warns = res["warns"] }, PyJson.Options),
                pass = pass ? 1 : 0, docId,
            }, tx);
            await tx.CommitAsync();
        }

        if (res.Get("pass") is true) await repo.SaveVendorMemoryAsync(module, header);
        if (edited) await repo.LogAuditAsync(docId, module, "UPDATE", body.GetStr("user") is { Length: > 0 } u ? u : "system",
            detail: "แก้ไขข้อมูลเอกสาร / Mapping", fileName: doc.GetStr("fileName"));
        res["document"] = await repo.GetDocumentAsync(docId);
        return Ok(res);
    }

    [HttpPost("api/documents/{docId:int}/learn")]
    public async Task<IActionResult> LearnMapping(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var doc = await repo.GetDocumentAsync(docId);
        var partner = body.GetStr("partnerCode") is { Length: > 0 } p ? p : doc.GetStr("partnerCode");
        var extCode = body.Get("extCode"); var extDesc = body.Get("extDesc"); var mat = body.GetStr("materialCode");
        if (string.IsNullOrEmpty(partner) || mat.Length == 0) throw new HttpApiException(400, "ต้องระบุคู่ค้าและ Material");
        await using var conn = await GetDbAsync();
        if (doc.GetStr("module") == "SO")
            await conn.ExecuteAsync("""
                IF NOT EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE CustomerCode=@partner AND ExtCode=@extCode)
                  INSERT ocr.CustomerMaterial(CustomerCode,ExtCode,ExtDesc,MaterialCode) VALUES(@partner,@extCode,@extDesc,@mat)
                """, new { partner, extCode, extDesc, mat });
        else
            await conn.ExecuteAsync("""
                IF NOT EXISTS(SELECT 1 FROM ocr.VendorMaterial WHERE VendorCode=@partner AND ExtCode=@extCode)
                  INSERT ocr.VendorMaterial(VendorCode,ExtCode,ExtDesc,MaterialCode) VALUES(@partner,@extCode,@extDesc,@mat)
                """, new { partner, extCode, extDesc, mat });
        return Ok(new { ok = true });
    }

    [HttpPost("api/documents/{docId:int}/split")]
    public async Task<IActionResult> SplitDocument(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("module") != "SO") throw new HttpApiException(400, "Split ใช้ได้เฉพาะเอกสาร Sales Order");
        if (doc.GetStr("status") is "POSTED" or "SPLIT") throw new HttpApiException(400, "เอกสารนี้ส่งเข้า SAP แล้ว หรือถูก Split ไปแล้ว");
        if (doc.Get("sourceDocId") != null) throw new HttpApiException(400, "เอกสารที่แยกมาจากเอกสารอื่นแล้ว ไม่สามารถแยกซ้ำได้");

        var assign = body.Get("assign") as Dictionary<string, object?> ?? new();
        var user = body.GetStr("user") is { Length: > 0 } u ? u : "system";
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var groups = new SortedDictionary<int, List<Dictionary<string, object?>>>();
        foreach (var l in lines)
        {
            var itemNo = l.Get("itemNo")?.ToString() ?? "";
            var g = (int)Num(assign.Get(itemNo));
            if (g <= 0) continue;
            if (!groups.TryGetValue(g, out var list)) groups[g] = list = [];
            list.Add(l);
        }
        if (groups.Count < 2) throw new HttpApiException(400, "ต้องแบ่งอย่างน้อย 2 กลุ่มจึงจะ Split ได้");

        var docT = DocumentTables.For("SO").Doc;
        dynamic? src = await GetDbInstance().QueryOneAsync($"SELECT StoredPath, FileSize, RawText FROM {docT} WHERE DocId=@docId", new { docId });
        var header = (Dictionary<string, object?>)doc["header"]!;
        var created = new List<Dictionary<string, object?>>();
        foreach (var (gNo, gLines) in groups)
        {
            var gHeader = new Dictionary<string, object?>(header);
            var gTotal = gLines.Sum(l => Num(l.Get("amount")));
            gHeader["totalAmount"] = gTotal; gHeader["subTotal"] = gTotal;
            if (gHeader.GetStr("poNo").Length > 0) gHeader["poNo"] = $"{gHeader.GetStr("poNo")}-{gNo}";
            var d = DocumentRepository.Denorm("SO", gHeader);
            var newId = await GetDbInstance().InsertReturningIdAsync($"""
                INSERT {docT}(Module,FileName,StoredPath,FileSize,OcrProvider,OcrConfidence,OcrConfidenceNote,Status,
                      DocNo,DocDate,PostingDate,PartnerName,PartnerTaxId,Currency,SubTotal,VatRate,VatAmount,
                      WhtAmount,TotalAmount,HeaderJson,RawText,CreatedBy,SourceDocId)
                VALUES('SO',@fileName,@storedPath,@fileSize,@provider,@confidence,@confidenceNote,'NEW',
                      @docNo,@docDate,@postingDate,@partnerName,@partnerTaxId,@currency,@subTotal,@vatRate,@vatAmount,
                      @whtAmount,@totalAmount,@headerJson,@rawText,@user,@sourceDocId);
                SELECT SCOPE_IDENTITY();
                """, new
            {
                fileName = doc.GetStr("fileName"), storedPath = (string?)src?.StoredPath, fileSize = (int?)src?.FileSize,
                provider = doc.Get("provider"), confidence = doc.Get("confidence"), confidenceNote = doc.GetStr("confidenceNote"),
                docNo = d.Get("DocNo"), docDate = d.Get("DocDate"), postingDate = d.Get("PostingDate"),
                partnerName = d.Get("PartnerName"), partnerTaxId = d.Get("PartnerTaxId"), currency = d.Get("Currency"),
                subTotal = d.Get("SubTotal"), vatRate = d.Get("VatRate"), vatAmount = d.Get("VatAmount"),
                whtAmount = d.Get("WhtAmount"), totalAmount = d.Get("TotalAmount"),
                headerJson = JsonSerializer.Serialize(gHeader, PyJson.Options), rawText = (string?)src?.RawText, user, sourceDocId = docId,
            });
            await repo.SaveLinesAsync("SO", newId, gLines.Select(l => new Dictionary<string, object?>
            {
                ["extCode"] = l.Get("extCode"), ["desc"] = l.Get("desc"), ["qty"] = l.Get("qty"), ["uom"] = l.Get("uom"),
                ["price"] = l.Get("price"), ["amount"] = l.Get("amount"), ["materialCode"] = l.Get("materialCode"), ["extra"] = l.Get("extra"),
            }).ToList());
            await repo.LogAuditAsync(newId, "SO", "CREATE", user, detail: $"Split จากเอกสาร #{docId}", docNo: d.GetStr("DocNo"), fileName: doc.GetStr("fileName"));
            created.Add(await repo.GetDocumentAsync(newId));
        }

        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status='SPLIT', UpdatedAt=SYSDATETIME() WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, "SO", "UPDATE", user,
            detail: $"แยกเอกสารออกเป็น {groups.Count} Sales Order: {string.Join(", ", created.Select(c => c["docId"]))}", fileName: doc.GetStr("fileName"));
        return Ok(new { source = await repo.GetDocumentAsync(docId), created });
    }

    private async Task<(Dictionary<string, object?> Payload, Dictionary<string, object?> Res)> PayloadForAsync(Dictionary<string, object?> doc, Dictionary<string, object?>? manual = null)
    {
        var masterData = await masters.LoadForMappingAsync();
        var module = doc.GetStr("module");
        var header = (Dictionary<string, object?>)doc["header"]!;
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var res = MappingEngine.RunMapping(module, header, lines, masterData, manual ?? StoredManual(doc));
        var resHeader = (Dictionary<string, object?>)res["header"]!;
        Dictionary<string, object?>? pm;
        if (module == "SO")
        {
            var custCode = (resHeader.Get("customer") as Dictionary<string, object?>).GetStr("code");
            pm = masterData.Customers.FirstOrDefault(c => c.GetStr("CustomerCode") == custCode);
        }
        else
        {
            var venCode = (resHeader.Get("vendor") as Dictionary<string, object?>).GetStr("code");
            pm = masterData.Vendors.FirstOrDefault(v => v.GetStr("VendorCode") == venCode);
        }
        var source = new Dictionary<string, object?>
        {
            ["docId"] = doc.Get("docId"), ["file"] = doc.Get("fileName"), ["ocrProvider"] = doc.Get("provider"), ["confidence"] = doc.Get("confidence"),
        };
        var payload = SapPayloadBuilder.BuildPayload(config, module, header, lines, res, pm, source);
        return (payload, res);
    }

    // stored_manual(): reconstructs the manual-override shape from what's already confirmed in the
    // DB (partnerCode/shipToCode/materialCode per line), so payload/post reflect what the user last saw.
    private static Dictionary<string, object?> StoredManual(Dictionary<string, object?> doc)
    {
        var m = new Dictionary<string, object?> { ["header"] = new Dictionary<string, object?>(), ["lines"] = new Dictionary<string, object?>() };
        var mHeader = (Dictionary<string, object?>)m["header"]!;
        var mLines = (Dictionary<string, object?>)m["lines"]!;
        if (doc.GetStr("module") == "SO")
        {
            if (doc.GetStr("partnerCode").Length > 0) mHeader["customer"] = doc.Get("partnerCode");
            if (doc.GetStr("shipToCode").Length > 0) mHeader["shipTo"] = doc.Get("shipToCode");
        }
        else if (doc.GetStr("partnerCode").Length > 0) mHeader["vendor"] = doc.Get("partnerCode");
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].GetStr("materialCode").Length > 0) mLines[i.ToString()] = lines[i].Get("materialCode");
        return m;
    }

    [HttpGet("api/documents/{docId:int}/payload")]
    public async Task<IActionResult> PreviewPayload(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var (payload, res) = await PayloadForAsync(doc);
        return Ok(new { payload, pass = res.Get("pass"), errors = res["errors"] });
    }

    [HttpPost("api/documents/{docId:int}/post")]
    public async Task<IActionResult> PostDocument(int docId, [FromBody] Dictionary<string, object?>? rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody ?? new());
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, $"เอกสารนี้ถูกส่งเข้า SAP แล้ว ({doc.GetStr("sapDocNo")})");
        if (doc.GetStr("status") != "MAPPED" || doc.GetStr("mapStatus") != "PASS") throw new HttpApiException(400, "ต้องกด Mapping ให้ผ่านก่อนส่งเข้า SAP");
        var (payload, res) = await PayloadForAsync(doc);
        if (res.Get("pass") is not true)
            throw new HttpApiException(400, $"Mapping ยังไม่ผ่าน — ไม่พบข้อมูล {((List<Dictionary<string, object?>>)res["errors"]!).Count} จุด");

        var user = body.GetStr("user") is { Length: > 0 } u ? u : "system";
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        var r = await sap.PostAsync(module, payload);
        await using (var conn = await GetDbAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync("""
                INSERT ocr.PostLog(DocId,Module,SapDocNo,Endpoint,PayloadJson,Success,Message,PostedBy)
                VALUES(@docId,@module,@sapDocNo,@endpoint,@payloadJson,@success,@message,@user)
                """, new
            {
                docId, module, sapDocNo = r.SapDocNo, endpoint = r.Endpoint,
                payloadJson = JsonSerializer.Serialize(payload, PyJson.Options), success = r.Success ? 1 : 0, message = r.Message, user,
            }, tx);
            if (r.Success)
                await conn.ExecuteAsync($"UPDATE {docT} SET Status='POSTED', SapDocNo=@sapDocNo, PostedAt=SYSDATETIME(), PostedBy=@user, UpdatedAt=SYSDATETIME() WHERE DocId=@docId",
                    new { sapDocNo = r.SapDocNo, user, docId }, tx);
            await tx.CommitAsync();
        }
        return Ok(new { success = r.Success, simulated = r.Simulated, sapDocNo = r.SapDocNo, endpoint = r.Endpoint, message = r.Message, document = await repo.GetDocumentAsync(docId) });
    }

    // ---- small local helpers (avoid threading Db through every method signature) ----
    private Db GetDbInstance() => HttpContext.RequestServices.GetRequiredService<Db>();
    private Task<Microsoft.Data.SqlClient.SqlConnection> GetDbAsync() => GetDbInstance().OpenAsync();

    private static bool DictEquals(Dictionary<string, object?> a, Dictionary<string, object?> b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private static bool ListEquals(List<object?> a, List<Dictionary<string, object?>> b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}

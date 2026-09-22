using System.Text.Json;
using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
using MgtOcr.Core.Json;
using MgtOcr.Core.Mapping;
using MgtOcr.Sap;
using MgtOcr.Data;
using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;
using MgtOcr.Api.Auth;
using static MgtOcr.Core.Mapping.MappingHelpers;

namespace MgtOcr.Api.Controllers;

// Ported from the "documents" section of app/main.py (lines 402-877) — upload/list/get/put/delete/
// reocr/chat/chat-fix/rawtext/file/map/learn/split/payload/post. One controller, matching the
// original single-file layout, since these all share the same document-lifecycle state machine.
[ApiController]
[ServiceFilter(typeof(DepartmentAccessFilter))]
public class DocumentsController(DocumentRepository repo, MasterRepository masters, OcrEngine ocr,
    SapClient sap, SapBusinessPartnerClient sapBp, AppConfig config, ICurrentUserAccessor currentUser) : ControllerBase
{
    // Who to stamp on CreatedBy / PerformedBy / PostedBy.
    //
    // This used to be read from the request body ("user", defaulting to "system"), which meant any
    // caller could put any name on an audit row — including a name that was not theirs. It now comes
    // from the validated Entra ID token via Ms_User, so the audit trail means something. Throws 403
    // if the signed-in address has no active row in the user master.
    private async Task<string> ActorAsync(CancellationToken ct = default) =>
        (await currentUser.RequireAsync(ct)).AuditName;

    private async Task<string> SalesOrgAsync(Dictionary<string, object?> header)
    {
        var org = header.GetStr("salesOrg");
        if (org is "1000" or "2000") return org;
        var user = await currentUser.RequireAsync();
        org = user.SalesOrganization;
        if (org is "1000" or "2000") return org;
        return user.PrimaryCompany?.CompanyCode == "MGT" ? "1000" : "2000";
    }

    private string CompanyCodeForSalesOrg(string salesOrg) =>
        config.CompanyForSalesOrg(salesOrg)?.CompanyCode ?? salesOrg;

    // GLC treats the ship-to as optional (see MappingEngine.RunMapping's shipToOptional): many GLC
    // orders have no separate ship-to and don't need one sent to SAP. Resolved from the document's
    // SalesOrg, which for a Sales Order follows the active company (incl. the admin "View as" dev
    // toggle — see DocumentPage), so simulating GLC correctly makes the ship-to optional.
    private bool IsGlcSalesOrg(string salesOrg) =>
        string.Equals(config.CompanyForSalesOrg(salesOrg)?.Name, "GLC", StringComparison.OrdinalIgnoreCase);

    private string AuthorizationGroupForSalesOrg(string salesOrg) =>
        config.CompanyForSalesOrg(salesOrg)?.AuthorizationGroup ?? "";

    // SharePoint archiving phase-1 foundation (2026-09-22): the canonical "MGT"/"GLC" label to
    // stamp on a document at the moment it posts successfully -- see
    // sql/22_document_company_code.sql for why this must be resolved from the DOCUMENT's own
    // header, never from whoever is signed in. This endpoint only ever posts to SAP, which for
    // AP/II always means GLC (see this controller's own doc comment on Upload -- "Both are
    // separate SAP apps") and for a Sales Order follows the SAME SalesOrg resolution already used
    // to build its payload/master data above, just read back as the "MGT"/"GLC" name instead of
    // the raw SalesOrg. A Sales Order that resolves to MGT never reaches this endpoint in practice
    // (that company posts through ZohoSalesOrderController instead), but this still asks rather
    // than assumes, so it stays correct if that ever changes.
    private async Task<string> CompanyNameForPostAsync(string module, Dictionary<string, object?> header) =>
        module == "SO"
            ? config.CompanyForSalesOrg(await SalesOrgAsync(header))?.Name ?? "GLC"
            : "GLC";

    // Department gate for endpoints whose module is not a plain action argument (e.g. it
    // arrives inside the JSON body). DepartmentAccessFilter covers the rest of the controller.
    private async Task RequireModuleAccessAsync(string module, CancellationToken ct = default)
    {
        var u = await currentUser.RequireAsync(ct);
        if (!DepartmentAccess.CanAccess(u, module))
            throw new HttpApiException(403, "You do not have access to this document type (your department can only see its own documents)");
    }

    private static readonly (string Id, string Label)[] ApDocCategories =
    [
        ("INVENTORY", "Liability Recording - Inventory"),
        ("EXPENSE", "Liability Recording - Expense"),
        ("FIXED_ASSET_BUDGET", "Liability Recording - Fixed Asset (Budget-controlled)"),
        ("FIXED_ASSET_NO_BUDGET", "Liability Recording - Fixed Asset (Non-budget-controlled)"),
        ("SUB_CONTRACT", "Liability Recording - Sub Contract"),
    ];

    private static readonly HashSet<string> Modules = ["AP", "SO", "II", "PODP"];
    private static readonly Dictionary<string, (string Label, string EnvVar)> ChatFixProviderLabel = new()
    {
        ["claude"] = ("Claude", "ANTHROPIC_API_KEY"), ["gemini"] = ("Gemini", "GEMINI_API_KEY"), ["openai"] = ("ChatGPT", "OPENAI_API_KEY"),
    };

    private static string ValidateModule(string? module)
    {
        var m = (module ?? "").ToUpperInvariant();
        if (!Modules.Contains(m)) throw new HttpApiException(400, "module must be one of AP, SO, II, PODP");
        return m;
    }

    private static string ValidateApDocCategory(string module, string? apDocCategory)
    {
        // Liability-recording category applies to both invoice modules (AP with PO, II without PO).
        var cat = module is "AP" or "II" ? (apDocCategory ?? "").Trim().ToUpperInvariant() : "";
        if (cat.Length > 0 && !ApDocCategories.Any(c => c.Id == cat)) throw new HttpApiException(400, "Invalid document type");
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
        await RequireModuleAccessAsync(module);
        var apCat = ValidateApDocCategory(module, body.GetStr("apDocCategory"));
        var idx = (int)Num(body.Get("index"));
        var pd = DemoData.DemoDoc(module, idx);
        var ext = ExtConversion.ToExtDict(pd);
        var user = await ActorAsync();
        var fileName = pd.SampleName ?? "sample.pdf";
        var docId = await repo.CreateDocumentAsync(module, ext, fileName, "", 0, user, apCat);
        return Ok(await repo.GetDocumentAsync(docId));
    }

    [HttpPost("api/documents/upload")]
    public async Task<IActionResult> Upload([FromForm] string module, [FromForm] string ocr_,
        [FromForm(Name = "apDocCategory")] string? apDocCategory, [FromForm] IFormFile file, [FromForm] string? password)
    {
        var user = await ActorAsync();
        // AP = the single liability-recording (การตั้งหนี้) reading page: read the document first,
        // then route by PO number (poRef present -> Supplier Invoice / MIRO, still module "AP";
        // absent -> Incoming Invoice / FB60, module "II"). Both are separate SAP apps.
        var mod = ValidateModule(module);
        var detect = mod == "AP";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fname = ExtConversion.SafeName(file.FileName);
        var stored = Path.Combine(config.UploadDir, $"{stamp}_{fname}");
        await using (var fs = System.IO.File.Create(stored))
            await file.CopyToAsync(fs);
        var size = (int)new FileInfo(stored).Length;

        // Encrypted PDF? Ask the user for the open password (or say it was wrong) before OCR, so an
        // encrypted file does not silently read as empty.
        if (Path.GetExtension(fname).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pdfStatus = PdfExtraction.CheckPassword(stored, password);
            if (pdfStatus != PdfExtraction.PdfOpenStatus.Ok)
            {
                try { System.IO.File.Delete(stored); } catch { /* best effort */ }
                throw new HttpApiException(400, pdfStatus == PdfExtraction.PdfOpenStatus.PasswordRequired ? "PDF_PASSWORD_REQUIRED" : "PDF_PASSWORD_WRONG");
            }
        }

        var t0 = DateTime.UtcNow;
        // Locked to Gemini (per Megachem): the empty / "auto" engine path always uses Gemini. An
        // explicit non-auto engine is still honored, but the gemini-only UI never sends one.
        var uploadEngine = string.IsNullOrEmpty(ocr_) || ocr_ == "auto" ? "gemini" : ocr_;
        var pd = await ocr.ExtractAsync(stored, mod, uploadEngine, password);
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        if (detect)
            mod = (pd.Header.GetStr("poRef")).Trim().Length > 0 ? "AP" : "II";
        var apCat = ValidateApDocCategory(mod, apDocCategory);
        var ext = ExtConversion.ToExtDict(pd);
        var docId = await repo.CreateDocumentAsync(mod, ext, fname, stored, size, user, apCat, durationMs);
        var outDoc = await repo.GetDocumentAsync(docId);
        outDoc["ocrNote"] = pd.Note ?? "";
        return Ok(outDoc);
    }

    [HttpGet("api/documents")]
    public async Task<IActionResult> ListDocuments([FromQuery] string module = "", [FromQuery] string status = "",
        [FromQuery] string apDocCategory = "", [FromQuery] string search = "", [FromQuery] string dateFrom = "",
        [FromQuery] string dateTo = "", [FromQuery] string invModule = "", [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        // A specific tab is already authorized by DepartmentAccessFilter; for the all-tabs
        // listing, restrict to the modules this user's department may see.
        var allowed = DepartmentAccess.AllowedModules(await currentUser.RequireAsync());
        page = page < 1 ? 1 : page;
        pageSize = Math.Clamp(pageSize, 1, 200);
        var r = await repo.ListDocumentsPagedAsync(module, status, apDocCategory, search, dateFrom, dateTo,
            invModule, page, pageSize, allowed);
        return Ok(new
        {
            results = r.Rows,
            total = r.Total,
            counts = r.CountAll == null ? null : new { all = r.CountAll, AP = r.CountAP, II = r.CountII },
        });
    }

    [HttpGet("api/documents/{docId:int}")]
    public async Task<IActionResult> ReadDocument(int docId) => Ok(await repo.GetDocumentAsync(docId));

    [HttpPut("api/documents/{docId:int}")]
    public async Task<IActionResult> SaveDocument(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, "This document has been posted to SAP and cannot be edited");
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        var header = body.Get("header") as Dictionary<string, object?> ?? (Dictionary<string, object?>)doc["header"]!;
        await repo.UpdateHeaderAsync(docId, module, header);
        if (body.Get("lines") is List<object?> linesRaw)
            await repo.SaveLinesAsync(module, docId, linesRaw.OfType<Dictionary<string, object?>>().ToList());
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END, MapStatus=NULL, MapMessage=NULL WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, module, "UPDATE", await ActorAsync(),
            detail: "Edited document data", fileName: doc.GetStr("fileName"));
        return Ok(await repo.GetDocumentAsync(docId));
    }

    [HttpPost("api/documents/{docId:int}/category")]
    public async Task<IActionResult> SetDocCategory(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var cat = (body.GetStr("apDocCategory")).Trim().ToUpperInvariant();
        if (cat.Length > 0 && !ApDocCategories.Any(c => c.Id == cat)) throw new HttpApiException(400, "Invalid document type");
        var docT = DocumentTables.ForId(docId).Doc;
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET ApDocCategory=@cat WHERE DocId=@docId", new { cat = cat.Length > 0 ? cat : null, docId });
        var doc = await repo.GetDocumentAsync(docId);
        await repo.LogAuditAsync(docId, doc.GetStr("module"), "UPDATE", await ActorAsync(),
            detail: "Changed document type to: " + (cat.Length > 0 ? cat : "-"), fileName: doc.GetStr("fileName"));
        return Ok(doc);
    }

    [HttpDelete("api/documents/{docId:int}")]
    public async Task<IActionResult> DeleteDocument(int docId)
    {
        var user = await ActorAsync();
        var doc = await repo.GetDocumentAsync(docId);
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"DELETE FROM {docT} WHERE DocId=@docId", new { docId });
        var header = (Dictionary<string, object?>)doc["header"]!;
        var docNo = header.GetStr("invoiceNo") is { Length: > 0 } inv ? inv : header.GetStr("poNo");
        await repo.LogAuditAsync(docId, module, "DELETE", user, detail: "Deleted document", docNo: docNo, fileName: doc.GetStr("fileName"));
        return Ok(new { ok = true });
    }

    [HttpPost("api/documents/{docId:int}/reocr")]
    public async Task<IActionResult> ReocrDocument(int docId, [FromBody] Dictionary<string, object?>? rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody ?? new());
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? row = await GetDbInstance().QueryOneAsync($"SELECT Module, StoredPath, FileName FROM {docT} WHERE DocId=@docId", new { docId });
        if (row == null) throw new HttpApiException(404, "Document not found");
        string storedPath = row.StoredPath ?? ""; string module = row.Module; string fileNameOnDisk = row.FileName ?? "";

        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, "This document has been posted to SAP and cannot be re-read");
        if (doc.GetStr("status") == "SPLIT") throw new HttpApiException(400, "This document has already been split and cannot be re-read (kept as a reference for the split documents)");
        if (doc.Get("sourceDocId") != null) throw new HttpApiException(400, "This document is a split part of another document and cannot be re-read (it would overwrite the split lines with the full document data)");
        if (storedPath.Length == 0 || !System.IO.File.Exists(storedPath)) throw new HttpApiException(400, "Original file not found (this document may have been created from a sample set)");

        var t0 = DateTime.UtcNow;
        var reocrPw = body.GetStr("password") is { Length: > 0 } rp ? rp : null;
        if (Path.GetExtension(storedPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var pdfStatus = PdfExtraction.CheckPassword(storedPath, reocrPw);
            if (pdfStatus != PdfExtraction.PdfOpenStatus.Ok)
                throw new HttpApiException(400, pdfStatus == PdfExtraction.PdfOpenStatus.PasswordRequired ? "PDF_PASSWORD_REQUIRED" : "PDF_PASSWORD_WRONG");
        }
        // Locked to Gemini (per Megachem): empty / "auto" re-OCR uses Gemini (the UI sends "gemini").
        var reocrEngine = body.GetStr("ocr") is { Length: > 0 } o && o != "auto" ? o : "gemini";
        var pd = await ocr.ExtractAsync(storedPath, module, reocrEngine, reocrPw);
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        var filled = await repo.ApplyVendorMemoryAsync(module, pd.Header);
        if (filled.Count > 0)
        {
            var note = "Filled from a previously confirmed partner (not read directly from this document): " + string.Join(", ", filled);
            pd.ConfidenceNote = pd.ConfidenceNote.Length > 0 ? $"{pd.ConfidenceNote} / {note}" : note;
        }
        // Keep the posting date this document was stamped with at upload — a re-read must not move it
        // to today, and the fresh OCR header would otherwise fall back to the invoice date.
        var keptPosting = ((Dictionary<string, object?>)doc["header"]!).GetStr("postingDate");
        DocumentRepository.StampPostingDate(module, pd.Header,
            DateTime.TryParseExact(keptPosting, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var kp) ? kp : DateTime.Today);
        DocumentRepository.DefaultTaxDates(module, pd.Header);
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
        await repo.LogAuditAsync(docId, module, "REOCR", await ActorAsync(),
            detail: "Re-read document", fileName: fileNameOnDisk, ocrProvider: pd.Provider);
        var outDoc = await repo.GetDocumentAsync(docId);
        outDoc["ocrNote"] = pd.Note is { Length: > 0 } n ? n : $"Document re-read successfully ({pd.Provider})";
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
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) throw new HttpApiException(404, "Image not found");
        return PhysicalFile(Path.GetFullPath(path), "application/octet-stream");
    }

    [HttpPost("api/documents/{docId:int}/chat-fix")]
    public async Task<IActionResult> ChatFixDocument(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var message = body.GetStr("message").Trim();
        var imageDataUrl = body.GetStr("image").Trim();
        var user = await ActorAsync();
        if (message.Length == 0 && imageDataUrl.Length == 0) throw new HttpApiException(400, "Please type a message or attach an image");
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, "This document has been posted to SAP and cannot be edited");

        string? imageB64 = null; var imageMediaType = "image/png"; byte[]? imageBytes = null; var imageExt = ".png";
        if (imageDataUrl.Length > 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(imageDataUrl, @"^data:(image/([a-zA-Z0-9.+-]+));base64,(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) throw new HttpApiException(400, "Invalid image format");
            imageMediaType = m.Groups[1].Value; var subtype = m.Groups[2].Value; imageB64 = m.Groups[3].Value;
            imageExt = "." + (System.Text.RegularExpressions.Regex.IsMatch(subtype, "^[a-zA-Z0-9]+$") ? subtype : "png");
            try { imageBytes = Convert.FromBase64String(imageB64); }
            catch { throw new HttpApiException(400, "Failed to decode image"); }
        }

        var provider = body.GetStr("provider") is { Length: > 0 } pr && ChatFixProviderLabel.ContainsKey(pr) ? pr : "claude";
        var history = await repo.GetChatHistoryAsync(docId);
        await repo.SaveChatMessageAsync(docId, "user", message, imageBytes, imageExt, user);

        var promptMessage = message.Length > 0 ? message : "Look at the attached image and correct the document data to match what is shown in the image";
        var module = doc.GetStr("module");
        var header = (Dictionary<string, object?>)doc["header"]!;
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;

        // SO documents can also have a line's Material changed, and the document's Ship-to selected,
        // straight from the chat -- give the AI real options to pick from instead of letting it
        // invent a code. AP/II have no per-line Material or Ship-to concept in mapping today, so both
        // stay empty there and the AI is never offered either option.
        var materialOptions = new List<(string Code, string Description, bool Mine)>();
        var shipToOptions = new List<(string Code, string Address)>();
        var accountOptions = new List<(string Code, string Description)>();
        if (module == "SO")
        {
            var salesOrg = await SalesOrgAsync(header);
            var companyCode = CompanyCodeForSalesOrg(salesOrg);
            var authorizationGroup = AuthorizationGroupForSalesOrg(salesOrg);
            var partnerCode = doc.GetStr("partnerCode");
            var masterData = MasterSchema.ForSalesOrg(await masters.LoadForMappingAsync(module, companyCode), companyCode);
            var currentUserInfo = await currentUser.RequireAsync();
            var isGlc = currentUserInfo.PrimaryCompany?.CompanyCode != "MGT";

            // Own CustomerMaterial rows first, then other customers' (same cross-customer set the
            // Material dropdown searches) -- covers the SAP send path (map.lines[i].code) directly.
            materialOptions = masterData.CustomerMaterials
                .Where(cm => cm.GetStr("SalesOrg") == companyCode && cm.GetStr("MaterialCodeSAP").Length > 0)
                .OrderBy(cm => cm.GetStr("CustomerCode") == partnerCode ? 0 : 1)
                .ThenBy(cm => cm.GetStr("MaterialCodeName"))
                .Take(400)
                .Select(cm => (cm.GetStr("MaterialCodeSAP"), cm.GetStr("MaterialCodeName"), cm.GetStr("CustomerCode") == partnerCode))
                .ToList();

            // A Zoho document additionally offers the currently-selected Deal's own Ordered Items --
            // that Deal is pure frontend state (which one is picked on the Deal card), so the frontend
            // sends it along as "dealItems" when one is selected. These are what the Zoho Sales Order
            // editor's line items actually match against, so a code picked from here is what makes the
            // chat-driven pick actually reach the Zoho POST, not just the Step 2 display.
            if (body.Get("dealItems") is List<object?> rawDealItems)
            {
                var seen = new HashSet<string>(materialOptions.Select(o => o.Code), StringComparer.OrdinalIgnoreCase);
                foreach (var raw in rawDealItems)
                {
                    if (raw is not Dictionary<string, object?> di) continue;
                    var code = di.GetStr("materialCode").Trim();
                    if (code.Length == 0 || !seen.Add(code)) continue;
                    materialOptions.Add((code, di.GetStr("materialName"), true));
                }
            }

            // Ship-to is always scoped to this document's own customer -- no cross-customer borrowing
            // concept the way Material has. Both SAP and Zoho resolve Ship-to from the same
            // ocr.ShipTo master (see buildZohoEdits' mappedShipTo fallback in DocumentPage.tsx), so
            // one option list covers both, before the SAP/GLC-only live search below adds to it.
            if (partnerCode.Length > 0)
                shipToOptions = masterData.ShipTos
                    .Where(s => s.GetStr("CustomerCode") == partnerCode)
                    .Select(s => (
                        Code: s.GetStr("SapShipToCode") is { Length: > 0 } sc ? sc : s.GetStr("ShipToCode"),
                        Address: s.GetStr("ShipToAddress") is { Length: > 0 } a ? a : s.GetStr("Address")))
                    .Where(x => x.Code.Length > 0)
                    .Take(200)
                    .ToList();

            // SAP/GLC only: also offer whatever Ship-to SAP itself already has on file for this
            // Sold-to (A_CustSalesPartnerFunc, PartnerFunction "SH") -- the exact same live lookup
            // the Ship-to card's own "Data from SAP" search panel runs
            // (SapShipToPanel/findSapPartnerFunctions in the frontend), so the chat can offer a
            // SAP-known Ship-to that just hasn't been saved to the local shiptos master yet, not only
            // what's already sitting in the local database. Needs the matched customer's real SAP
            // Business Partner code (Customers.SapCustomerCode -- a separate field from the internal
            // CustomerCode master key), so this is skipped quietly if the customer isn't matched yet
            // or has no SAP code on file. Never fatal to the rest of the chat fix.
            if (isGlc && partnerCode.Length > 0)
            {
                var soldToSapCode = masterData.Customers.FirstOrDefault(c => c.GetStr("CustomerCode") == partnerCode)?.GetStr("SapCustomerCode");
                if (!string.IsNullOrWhiteSpace(soldToSapCode))
                {
                    try
                    {
                        var links = await sapBp.FindPartnerFunctionsAsync(soldToSapCode, "SH", salesOrg, 50);
                        var seenShip = new HashSet<string>(shipToOptions.Select(o => o.Code), StringComparer.OrdinalIgnoreCase);
                        foreach (var link in links)
                        {
                            if (link.Partner == null || !seenShip.Add(link.PartnerCustomer)) continue;
                            var bp = link.Partner;
                            var addr = $"{bp.BusinessPartnerFullName ?? bp.BusinessPartnerName}" +
                                (string.IsNullOrWhiteSpace(bp.AddressStreet) && string.IsNullOrWhiteSpace(bp.AddressCity)
                                    ? "" : $" ({bp.AddressStreet} {bp.AddressCity})".Trim());
                            shipToOptions.Add((link.PartnerCustomer, addr));
                        }
                    }
                    catch
                    {
                        // SAP unreachable/misconfigured this turn -- the AI just goes with whatever
                        // local Ship-to rows it already has, same as an empty accountOptions; not fatal.
                    }
                }
            }

            // SAP/GLC SO documents can also have their Customer/Account re-searched straight from SAP --
            // for when the local match "feels wrong" and needs a live look-up, not just a locally-known
            // candidate. Zoho's own Account search is a separate, already-existing chat flow
            // (pendingMatch/sendMatchChat -> /api/compare), so this stays SAP-only. Runs a real SAP call
            // every chat turn on an SO document (bounded to `top`, same cost as opening the Customer
            // card's own live search panel), so failures here must never break the rest of the chat fix.
            if (isGlc)
            {
                var custName = header.GetStr("customerName").Trim();
                var custTaxId = header.GetStr("customerTaxId").Trim();
                if (custName.Length > 0 || custTaxId.Length > 0)
                {
                    try
                    {
                        var bpResults = custTaxId.Length > 0 ? await sapBp.FindByTaxIdAsync(custTaxId, authorizationGroup, 10) : [];
                        if (bpResults.Count == 0 && custName.Length > 0)
                            bpResults = await sapBp.FindByNameAsync(custName, authorizationGroup, 10);
                        accountOptions = bpResults
                            .Select(bp => (
                                bp.BusinessPartnerId,
                                $"{bp.BusinessPartnerFullName ?? bp.BusinessPartnerName}" +
                                (string.IsNullOrWhiteSpace(bp.AddressStreet) && string.IsNullOrWhiteSpace(bp.AddressCity)
                                    ? "" : $" ({bp.AddressStreet} {bp.AddressCity})".Trim())))
                            .ToList();
                    }
                    catch
                    {
                        // SAP unreachable/misconfigured this turn -- the AI just goes without live
                        // Account options, same as an empty materialOptions/shipToOptions; not fatal.
                    }
                }
            }
        }

        var (result, chatFixErr) = await ChatFix.ChatFixDocumentAsync(module, header, lines, history, promptMessage, imageB64, imageMediaType, provider, config, materialOptions, shipToOptions, accountOptions);
        if (result == null)
        {
            var (label, envVar) = ChatFixProviderLabel[provider];
            throw new HttpApiException(400, chatFixErr ?? $"Could not connect to {label}, or {envVar} is not set in .env");
        }

        await repo.SaveChatMessageAsync(docId, "assistant", result.Reply, null, ".png", "AI");
        await repo.UpdateHeaderAsync(docId, module, result.Header);
        await repo.SaveLinesAsync(module, docId, result.Lines);
        var docT = DocumentTables.For(module).Doc;
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END, MapStatus=NULL, MapMessage=NULL WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, module, "UPDATE", user, detail: "Edited via AI chat: " + message[..Math.Min(200, message.Length)], fileName: doc.GetStr("fileName"));
        var outDoc = await repo.GetDocumentAsync(docId);
        // materialCodes: line index -> Material code the AI picked for that line, if any.
        // shipToCode: the Ship-to code the AI picked for the whole document, if any.
        // customerCode: the Account/Business Partner code the AI picked from the live SAP search, if any.
        // None of these are persisted here -- the frontend applies each as a plain selection (see
        // sendChat()), same as picking manually; only an explicit save action creates/updates master
        // data.
        return Ok(new { reply = result.Reply, document = outDoc, materialCodes = result.MaterialCodes, shipToCode = result.ShipToCode, customerCode = result.CustomerCode });
    }

    [HttpGet("api/documents/{docId:int}/rawtext")]
    public async Task<IActionResult> RawText(int docId)
    {
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? r = await GetDbInstance().QueryOneAsync($"SELECT RawText FROM {docT} WHERE DocId=@docId", new { docId });
        if (r == null) throw new HttpApiException(404, "Document not found");
        string text = r.RawText ?? "";
        return Ok(new { text });
    }

    [HttpGet("api/documents/{docId:int}/file")]
    public async Task<IActionResult> DocumentFile(int docId)
    {
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? d = await GetDbInstance().QueryOneAsync($"SELECT StoredPath, FileName FROM {docT} WHERE DocId=@docId", new { docId });
        string? path = d?.StoredPath;
        if (d == null || string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) throw new HttpApiException(404, "Original file not found");
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

        if (module == "SO")
        {
            header["salesOrg"] = await SalesOrgAsync(header);
            await repo.UpdateHeaderAsync(docId, module, header);
        }
        var companyCode = module == "SO" ? CompanyCodeForSalesOrg(header.GetStr("salesOrg")) : null;
        var masterData = await masters.LoadForMappingAsync(module, companyCode);
        var res = MappingEngine.RunMapping(module, header, lines, masterData, manual, companyCode,
            module == "SO" && IsGlcSalesOrg(header.GetStr("salesOrg")));
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
        if (edited) await repo.LogAuditAsync(docId, module, "UPDATE", await ActorAsync(),
            detail: "Edited document data / Mapping", fileName: doc.GetStr("fileName"));
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
        if (string.IsNullOrEmpty(partner) || mat.Length == 0) throw new HttpApiException(400, "Partner and Material are required");
        await using var conn = await GetDbAsync();
        if (doc.GetStr("module") == "SO")
        {
            var salesOrg = CompanyCodeForSalesOrg(await SalesOrgAsync((Dictionary<string, object?>)doc["header"]!));
            await conn.ExecuteAsync("""
                IF EXISTS(SELECT 1 FROM ocr.CustomerMaterial WHERE SalesOrg=@salesOrg AND CustomerCode=@partner AND MaterialCodeCode=@extCode)
                  UPDATE ocr.CustomerMaterial SET MaterialCodeName=@extDesc, MaterialCodeSAP=@mat, Isactive=1, UpdatedAt=SYSDATETIME()
                  WHERE SalesOrg=@salesOrg AND CustomerCode=@partner AND MaterialCodeCode=@extCode
                ELSE
                  INSERT ocr.CustomerMaterial(SalesOrg,CustomerCode,MaterialCodeCode,MaterialCodeName,MaterialCodeSAP,Isactive)
                  VALUES(@salesOrg,@partner,@extCode,@extDesc,@mat,1)
                """, new { salesOrg, partner, extCode, extDesc, mat });
        }
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
        if (doc.GetStr("module") != "SO") throw new HttpApiException(400, "Split is only available for Sales Order documents");
        if (doc.GetStr("status") is "POSTED" or "SPLIT") throw new HttpApiException(400, "This document has been posted to SAP or already split");
        if (doc.Get("sourceDocId") != null) throw new HttpApiException(400, "A document split from another cannot be split again");

        var assign = body.Get("assign") as Dictionary<string, object?> ?? new();
        var user = await ActorAsync();
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
        if (groups.Count < 2) throw new HttpApiException(400, "At least 2 groups are required to split");

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
            await repo.LogAuditAsync(newId, "SO", "CREATE", user, detail: $"Split from document #{docId}", docNo: d.GetStr("DocNo"), fileName: doc.GetStr("fileName"));
            created.Add(await repo.GetDocumentAsync(newId));
        }

        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status='SPLIT', UpdatedAt=SYSDATETIME() WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, "SO", "UPDATE", user,
            detail: $"Split into {groups.Count} Sales Order: {string.Join(", ", created.Select(c => c["docId"]))}", fileName: doc.GetStr("fileName"));
        return Ok(new { source = await repo.GetDocumentAsync(docId), created });
    }

    private async Task<(Dictionary<string, object?> Payload, Dictionary<string, object?> Res)> PayloadForAsync(Dictionary<string, object?> doc, Dictionary<string, object?>? manual = null)
    {
        var module = doc.GetStr("module");
        var header = (Dictionary<string, object?>)doc["header"]!;
        var companyCode = module == "SO"
            ? CompanyCodeForSalesOrg(await SalesOrgAsync(header))
            : null;
        var masterData = await masters.LoadForMappingAsync(module, companyCode);
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        if (module == "SO")
        {
            header["salesOrg"] = await SalesOrgAsync(header);
            masterData = MasterSchema.ForSalesOrg(masterData, CompanyCodeForSalesOrg(header.GetStr("salesOrg")));
        }
        var res = MappingEngine.RunMapping(module, header, lines, masterData, manual ?? StoredManual(doc), companyCode,
            module == "SO" && IsGlcSalesOrg(header.GetStr("salesOrg")));
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
        if (doc.GetStr("status") == "POSTED") throw new HttpApiException(400, $"This document has already been posted to SAP ({doc.GetStr("sapDocNo")})");
        if (doc.GetStr("status") != "MAPPED" || doc.GetStr("mapStatus") != "PASS") throw new HttpApiException(400, "Mapping must pass before posting to SAP");
        var (payload, res) = await PayloadForAsync(doc);
        if (res.Get("pass") is not true)
            throw new HttpApiException(400, $"Mapping has not passed — {((List<Dictionary<string, object?>>)res["errors"]!).Count} missing item(s)");

        var user = await ActorAsync();
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        var companyCode = await CompanyNameForPostAsync(module, (Dictionary<string, object?>)doc["header"]!);
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
                await conn.ExecuteAsync($"UPDATE {docT} SET Status='POSTED', SapDocNo=@sapDocNo, CompanyCode=@companyCode, PostedAt=SYSDATETIME(), PostedBy=@user, UpdatedAt=SYSDATETIME() WHERE DocId=@docId",
                    new { sapDocNo = r.SapDocNo, companyCode, user, docId }, tx);
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

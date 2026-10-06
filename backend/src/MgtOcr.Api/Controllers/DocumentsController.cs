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
    SapClient sap, SapBusinessPartnerClient sapBp, AppConfig config, ICurrentUserAccessor currentUser,
    OcrJobRepository jobs, MgtOcr.Api.Services.DocumentIngestService ingest,
    SapProductClient sapProduct, FileArchiveRepository archive, MgtOcr.Api.Services.GraphArchiveClient graph,
    SalesOrderPostRepository soPosts, ILogger<DocumentsController> logger) : ControllerBase
{
    // Who to stamp on CreatedBy / PerformedBy / PostedBy.
    //
    // This used to be read from the request body ("user", defaulting to "system"), which meant any
    // caller could put any name on an audit row — including a name that was not theirs. It now comes
    // from the validated Entra ID token via Ms_User, so the audit trail means something. Throws 403
    // if the signed-in address has no active row in the user master.
    private async Task<string> ActorAsync(CancellationToken ct = default) =>
        (await currentUser.RequireAsync(ct)).AuditName;

    /// <summary>The company whose documents this person may see — "1000" MGT, "2000" GLC, or ""
    /// for Admin and for a user record that names no company (see CompanyScope). Read from the user
    /// master, never from the request.</summary>
    private async Task<string> CompanyScopeAsync(CancellationToken ct = default) =>
        CompanyScope.For(await currentUser.RequireAsync(ct), config);

    /// <summary>The company a document imported right now belongs to — the importer's own, Admin
    /// included. Admin sees both companies but still files under theirs, so an MGT invoice an Admin
    /// imports does not end up on GLC's list as well.</summary>
    private async Task<string> OwnCompanyAsync(CancellationToken ct = default) =>
        CompanyScope.Own(await currentUser.RequireAsync(ct), config);

    /// <summary>The company an import screen asked to file under. Only an Admin may choose; for
    /// everyone else the value is ignored and their own company wins (see CompanyScope.ImportFor),
    /// so the switch on the Import page can never be used to file into another company's list.</summary>
    private async Task<string> ImportCompanyAsync(string? requested, CancellationToken ct = default) =>
        CompanyScope.ImportFor(await currentUser.RequireAsync(ct), config, requested);

    /// <summary>The company profile an exported file should carry — resolved from the document's
    /// own SalesOrg, falling back to GLC for a document that predates the company column.</summary>
    private async Task<MgtOcr.Core.Config.CompanyProfile?> ExportCompanyAsync(int docId)
    {
        var salesOrg = (await repo.GetCompanyAsync(docId) ?? "").Trim();
        return (salesOrg.Length > 0 ? config.CompanyForSalesOrg(salesOrg) : null)
            ?? config.Companies.FirstOrDefault(c => string.Equals(c.Name, "GLC", StringComparison.OrdinalIgnoreCase));
    }

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

    // Same ceiling as the batch endpoint below. Without these the single-file upload fell back to
    // the ~28.6 MB Kestrel/IIS default while ten files at once were allowed 200 MB, so one detailed
    // scan of a long bundle failed with a bare 413 that the screen could not explain.
    [RequestSizeLimit(209_715_200)]
    [RequestFormLimits(MultipartBodyLengthLimit = 209_715_200)]
    [HttpPost("api/documents/upload")]
    public async Task<IActionResult> Upload([FromForm] string module, [FromForm] string ocr_,
        [FromForm(Name = "apDocCategory")] string? apDocCategory, [FromForm] IFormFile file, [FromForm] string? password,
        [FromForm(Name = "company")] string? company = null)
    {
        var user = await ActorAsync();
        // AP = the single liability-recording (การตั้งหนี้) reading page: read the document first,
        // then route by PO number (poRef present -> Supplier Invoice / MIRO, still module "AP";
        // absent -> Incoming Invoice / FB60, module "II"). Both are separate SAP apps.
        var mod = ValidateModule(module);
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

        // Category applies to both post-routing invoice modules (AP with PO, II without), so it can
        // be validated up front against the pre-routing module before OCR decides AP vs II.
        var apCat = ValidateApDocCategory(mod == "AP" ? "AP" : mod, apDocCategory);
        var (docId, _, note) = await ingest.IngestAsync(mod, stored, fname, size, ocr_, apCat, user, password,
            await ImportCompanyAsync(company));
        var outDoc = await repo.GetDocumentAsync(docId);
        outDoc["ocrNote"] = note ?? "";
        return Ok(outDoc);
    }

    // Batch import: accept up to 10 files for the CURRENT module, queue each, and let the
    // background worker read them one by one. Returns immediately with a batchId the client polls.
    // The document itself is created only when OCR finishes (unchanged pipeline), so AP still routes
    // to AP/II per file. Encrypted PDFs can't be prompted for a password mid-batch, so each such
    // file is recorded as FAILED with a note to import it on its own.
    // Default Kestrel body limit is ~30 MB; 10 files can exceed it, so raise it for this endpoint.
    [RequestSizeLimit(209_715_200)]
    [RequestFormLimits(MultipartBodyLengthLimit = 209_715_200)]
    [HttpPost("api/documents/upload-batch")]
    public async Task<IActionResult> UploadBatch([FromForm] string module, [FromForm] string ocr_,
        [FromForm(Name = "apDocCategory")] string? apDocCategory, [FromForm] List<IFormFile> files,
        [FromForm(Name = "company")] string? company = null)
    {
        var mod = ValidateModule(module);
        var user = await ActorAsync();
        if (files is null || files.Count == 0) throw new HttpApiException(400, "No files were uploaded");
        if (files.Count > 10) throw new HttpApiException(400, "Please import at most 10 files at a time");
        var apCat = ValidateApDocCategory(mod == "AP" ? "AP" : mod, apDocCategory);
        var engine = string.IsNullOrEmpty(ocr_) || ocr_ == "auto" ? "gemini" : ocr_;

        var batchId = Guid.NewGuid();
        var result = new List<object>();
        // Resolved once: every file in one batch is filed under the same company.
        var importCompany = await ImportCompanyAsync(company);
        foreach (var file in files)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var fname = ExtConversion.SafeName(file.FileName);
            var stored = Path.Combine(config.UploadDir, $"{stamp}_{fname}");
            await using (var fs = System.IO.File.Create(stored))
                await file.CopyToAsync(fs);
            var size = (int)new FileInfo(stored).Length;

            if (Path.GetExtension(fname).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                && PdfExtraction.CheckPassword(stored, null) != PdfExtraction.PdfOpenStatus.Ok)
            {
                try { System.IO.File.Delete(stored); } catch { /* best effort */ }
                const string msg = "Password-protected PDF — import this file on its own to enter the password";
                var failId = await jobs.EnqueueAsync(batchId, mod, apCat, engine, fname, "", size, user, "FAILED", msg,
                    importCompany);
                result.Add(new { jobId = failId, fileName = fname, status = "FAILED", error = msg });
                continue;
            }

            var jobId = await jobs.EnqueueAsync(batchId, mod, apCat, engine, fname, stored, size, user,
                salesOrg: importCompany);
            result.Add(new { jobId, fileName = fname, status = "QUEUED", error = (string?)null });
        }
        return Ok(new { batchId, module = mod, jobs = result });
    }

    // Poll one batch's per-file status (QUEUED / PROCESSING / DONE + resultDocId / FAILED + error).
    [HttpGet("api/documents/batch/{batchId:guid}")]
    public async Task<IActionResult> BatchStatus(Guid batchId)
    {
        var rows = (await jobs.ListByBatchAsync(batchId)).ToList();
        return Ok(new { batchId, jobs = rows });
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
            invModule, page, pageSize, allowed, await CompanyScopeAsync());
        return Ok(new
        {
            results = r.Rows,
            total = r.Total,
            counts = r.CountAll == null ? null : new { all = r.CountAll, AP = r.CountAP, II = r.CountII },
            retentionHours = config.DraftRetentionHours,   // UI: "file will be removed in ..." warning
            retentionModules = config.CleanupModules,       // ... only for these modules
        });
    }

    [HttpGet("api/documents/{docId:int}")]
    public async Task<IActionResult> ReadDocument(int docId)
    {
        var d = await repo.GetDocumentAsync(docId);
        d["retentionHours"] = config.DraftRetentionHoursFor(d.GetStr("module"));   // UI: "file will be removed in ..." warning
        return Ok(d);
    }

    // 404 text for a missing source file: say so plainly when the cleanup removed it after the retention period.
    private static string MissingFileMessage(object? fileExpiredAt) =>
        fileExpiredAt is DateTime t
            ? $"The original file was removed on {t:dd/MM/yyyy HH:mm} because the document was not posted within the retention period"
            : "Original file not found";

    [HttpPut("api/documents/{docId:int}")]
    public async Task<IActionResult> SaveDocument(int docId, [FromBody] Dictionary<string, object?> rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody);
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") is "POSTED" or "PARTIAL") throw new HttpApiException(400, "This document has been posted (fully or partly) and cannot be edited");
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

        // Changing the document type has to move the document between the two invoice modules, not
        // just relabel it — they validate completely differently. EXPENSE lines are service charges
        // (STORAGE CHARGE, TRUCKING, ...) posted against G/L accounts, so they belong to II (FB60)
        // and must NOT be matched against the material master; the goods/asset categories are
        // matched line by line against materials in AP (MIRO). Before this, the category was stored
        // but the module never moved, so picking "Expense" on an already-uploaded document left it
        // in AP and it kept demanding a Material for every cost line. Same rule as the upload path.
        var current = await repo.GetDocumentAsync(docId);
        var currentModule = current.GetStr("module");
        var newModule = currentModule;
        if (currentModule is "AP" or "II" && cat.Length > 0)
            newModule = cat == "EXPENSE"
                ? "II"
                : ((Dictionary<string, object?>)current["header"]!).GetStr("poRef").Trim().Length > 0 ? "AP" : "II";

        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync(
                $"UPDATE {docT} SET ApDocCategory=@cat, Module=@mod WHERE DocId=@docId",
                new { cat = cat.Length > 0 ? cat : null, mod = newModule, docId });

        var doc = await repo.GetDocumentAsync(docId);
        var moved = !string.Equals(newModule, currentModule, StringComparison.Ordinal)
            ? $" (moved {currentModule} -> {newModule})"
            : "";
        await repo.LogAuditAsync(docId, doc.GetStr("module"), "UPDATE", await ActorAsync(),
            detail: "Changed document type to: " + (cat.Length > 0 ? cat : "-") + moved, fileName: doc.GetStr("fileName"));
        return Ok(doc);
    }

    [HttpDelete("api/documents/{docId:int}")]
    public async Task<IActionResult> DeleteDocument(int docId)
    {
        var user = await ActorAsync();
        var doc = await repo.GetDocumentAsync(docId);
        // Already in SAP/Zoho (all or some of its Sales Orders) -> never delete; the list hides the
        // checkbox, this stops a direct API call too.
        if (doc.GetStr("status") is "POSTED" or "PARTIAL")
            throw new HttpApiException(400, "This document has been posted (fully or partly) and cannot be deleted");
        var module = doc.GetStr("module");
        var docT = DocumentTables.For(module).Doc;
        dynamic? fileRow = await GetDbInstance().QueryOneAsync($"SELECT StoredPath FROM {docT} WHERE DocId=@docId", new { docId });
        string storedPath = (string?)fileRow?.StoredPath ?? "";
        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"DELETE FROM {docT} WHERE DocId=@docId", new { docId });
        // Only modules listed in Archive:CleanupModules (default SO) lose their file with the document.
        var fileNote = config.CleansModule(module) ? await DeleteStoredFileIfUnusedAsync(storedPath) : "";
        var header = (Dictionary<string, object?>)doc["header"]!;
        var docNo = header.GetStr("invoiceNo") is { Length: > 0 } inv ? inv : header.GetStr("poNo");
        await repo.LogAuditAsync(docId, module, "DELETE", user, detail: "Deleted document" + fileNote, docNo: docNo, fileName: doc.GetStr("fileName"));
        return Ok(new { ok = true });
    }

    // Deleting a document now removes its uploaded file too (before, only the DB row went and the
    // file stayed in uploads forever). Kept when: another document still uses it (split parent /
    // children share one file), an OCR job is still waiting on it, it isn't under THIS installation's
    // uploads folder (dev PC and server share the database), or it is already gone. Best effort:
    // the document is already deleted, a file that can't be removed is only logged.
    private async Task<string> DeleteStoredFileIfUnusedAsync(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return "";
        try
        {
            if (await archive.IsFileInUseAsync(storedPath)) return " (file kept: still used by another document)";
            var full = Path.GetFullPath(storedPath, config.UploadDir);
            var root = Path.GetFullPath(config.UploadDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return " (file kept: not in this server's uploads folder)";
            if (!System.IO.File.Exists(full)) return "";
            System.IO.File.Delete(full);
            return " and its uploaded file";
        }
        catch (Exception e)
        {
            HttpContext.RequestServices.GetRequiredService<ILogger<DocumentsController>>()
                .LogWarning(e, "Document deleted but its file {Path} could not be removed", storedPath);
            return " (file could not be removed: " + e.Message + ")";
        }
    }

    [HttpPost("api/documents/{docId:int}/reocr")]
    public async Task<IActionResult> ReocrDocument(int docId, [FromBody] Dictionary<string, object?>? rawBody)
    {
        var body = JsonBodyHelpers.Unwrap(rawBody ?? new());
        var docT = DocumentTables.ForId(docId).Doc;
        dynamic? row = await GetDbInstance().QueryOneAsync($"SELECT Module, StoredPath, FileName, FileExpiredAt FROM {docT} WHERE DocId=@docId", new { docId });
        if (row == null) throw new HttpApiException(404, "Document not found");
        string storedPath = row.StoredPath ?? ""; string module = row.Module; string fileNameOnDisk = row.FileName ?? "";
        object? fileExpiredAt = row.FileExpiredAt;

        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("status") is "POSTED" or "PARTIAL") throw new HttpApiException(400, "This document has been posted (fully or partly) and cannot be re-read");
        if (doc.GetStr("status") == "SPLIT") throw new HttpApiException(400, "This document has already been split and cannot be re-read (kept as a reference for the split documents)");
        if (doc.Get("sourceDocId") != null) throw new HttpApiException(400, "This document is a split part of another document and cannot be re-read (it would overwrite the split lines with the full document data)");
        if (fileExpiredAt is DateTime) throw new HttpApiException(400, MissingFileMessage(fileExpiredAt) + " - it cannot be re-read");
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
        // Re-reading follows the company the document is already filed under, so a second read
        // never silently swaps MGT's rules for GLC's (CompanyRules treats an empty value as GLC).
        var pd = await ocr.ExtractAsync(storedPath, module, reocrEngine, reocrPw, await repo.GetCompanyAsync(docId));
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
        await repo.SaveLinesAsync(module, docId, pd.Lines.Select(l => ExtConversion.ToLineDict(l, pd.Header.GetValueOrDefault("deliveryDate") as string)).ToList());
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

        var img = await repo.GetChatImageAsync(docId, chatId);
        if (img == null) throw new HttpApiException(404, "Image not found");
        return File(img.Value.Data, img.Value.Mime);

        var chatT = DocumentTables.ForId(docId).Chat;
        dynamic? r = await GetDbInstance().QueryOneAsync($"SELECT ImagePath FROM {chatT} WHERE DocId=@docId AND ChatId=@chatId", new { docId, chatId });
        string? path = r?.ImagePath;
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) throw new HttpApiException(404, "Image not found");
        // Same reason as DocumentFile: an <img> needs a real image media type, not octet-stream.
        return PhysicalFile(Path.GetFullPath(path), MediaTypeForFile(path));

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
        if (doc.GetStr("status") is "POSTED" or "PARTIAL") throw new HttpApiException(400, "This document has been posted (fully or partly) and cannot be edited");

        string? imageB64 = null; var imageMediaType = "image/png"; byte[]? imageBytes = null;
        if (imageDataUrl.Length > 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(imageDataUrl, @"^data:(image/([a-zA-Z0-9.+-]+));base64,(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) throw new HttpApiException(400, "Invalid image format");
            imageMediaType = m.Groups[1].Value; imageB64 = m.Groups[3].Value;
            try { imageBytes = Convert.FromBase64String(imageB64); }
            catch { throw new HttpApiException(400, "Failed to decode image"); }
        }

        var provider = body.GetStr("provider") is { Length: > 0 } pr && ChatFixProviderLabel.ContainsKey(pr) ? pr : "claude";
        var history = await repo.GetChatHistoryAsync(docId);
        await repo.SaveChatMessageAsync(docId, "user", message, imageBytes, imageMediaType, user);

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
        // Hoisted so the read-only SAP lookup loop below (after this block) can use them.
        var isGlc = false;
        var authorizationGroup = "";
        if (module == "SO")
        {
            var salesOrg = await SalesOrgAsync(header);
            var companyCode = CompanyCodeForSalesOrg(salesOrg);
            authorizationGroup = AuthorizationGroupForSalesOrg(salesOrg);
            var partnerCode = doc.GetStr("partnerCode");
            var masterData = MasterSchema.ForSalesOrg(await masters.LoadForMappingAsync(module, companyCode), companyCode);
            var currentUserInfo = await currentUser.RequireAsync();
            isGlc = currentUserInfo.PrimaryCompany?.CompanyCode != "MGT";

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

        // Read-only SAP lookup loop: the AI may reply asking to search SAP for a material/customer it
        // wants to map (only on an SO + SAP/GLC document). We run just those read searches, add the
        // hits to the option lists (so its next pick still passes the anti-hallucination validation),
        // and re-invoke — bounded to a few rounds. The AI never gets a save/post tool here; only the
        // person's explicit Save/Post actions ever write anything.
        var lookupsEnabled = module == "SO" && isGlc;
        // MGT/Zoho SO documents: the AI can instead ask the screen to run its existing Zoho customer
        // search (read-only). GLC uses the backend SAP lookups above; MGT uses this on-screen action.
        var actionsEnabled = module == "SO" && !isGlc;
        const int maxRounds = 3; // rounds 0..1 may search; the last round forces a real answer
        var lookupLog = new System.Text.StringBuilder();
        ChatFixResult? result = null; string? chatFixErr = null;
        for (var iter = 0; iter < maxRounds; iter++)
        {
            // On the last round, turn lookups off so the AI must give a final edit/answer (using
            // whatever it has already found) instead of asking to search yet again.
            var enableThisRound = lookupsEnabled && iter < maxRounds - 1;
            (result, chatFixErr) = await ChatFix.ChatFixDocumentAsync(module, header, lines, history, promptMessage,
                imageB64, imageMediaType, provider, config, materialOptions, shipToOptions, accountOptions,
                enableThisRound, lookupLog.ToString(), actionsEnabled);
            if (result == null)
            {
                var (label, envVar) = ChatFixProviderLabel[provider];
                throw new HttpApiException(400, chatFixErr ?? $"Could not connect to {label}, or {envVar} is not set in .env");
            }
            if (!enableThisRound || result.Lookups.Count == 0) break; // AI gave its final answer

            // Execute each requested read-only search and fold the hits into the option lists, so the
            // AI's pick next round still passes the anti-hallucination validation. Found-or-not is
            // logged back so the AI can either choose or tell the user it couldn't find a match.
            foreach (var lk in result.Lookups)
            {
                try
                {
                    if (lk.Type == "material")
                    {
                        var seen = new HashSet<string>(materialOptions.Select(o => o.Code), StringComparer.OrdinalIgnoreCase);
                        var mats = await sapProduct.SearchByDescriptionAsync(lk.Query, null, 15);
                        var hits = 0;
                        foreach (var mt in mats)
                        {
                            if (mt.MaterialCode.Length == 0 || !seen.Add(mt.MaterialCode)) continue;
                            materialOptions.Add((mt.MaterialCode, mt.MaterialDescription, false));
                            hits++;
                        }
                        lookupLog.AppendLine(hits > 0
                            ? $"ค้น material '{lk.Query}' ใน SAP: พบ {hits} รายการ (เพิ่มเข้ารายการให้เลือกแล้ว)"
                            : $"ค้น material '{lk.Query}' ใน SAP: ไม่พบรายการที่ตรง");
                    }
                    else if (lk.Type == "customer")
                    {
                        var seen = new HashSet<string>(accountOptions.Select(o => o.Code), StringComparer.OrdinalIgnoreCase);
                        var digitsOnly = lk.Query.All(char.IsDigit);
                        var bps = digitsOnly
                            ? await sapBp.FindByTaxIdAsync(lk.Query, authorizationGroup, 10)
                            : await sapBp.FindByNameAsync(lk.Query, authorizationGroup, 10);
                        var hits = 0;
                        foreach (var bp in bps)
                        {
                            if (bp.BusinessPartnerId.Length == 0 || !seen.Add(bp.BusinessPartnerId)) continue;
                            var desc = $"{bp.BusinessPartnerFullName ?? bp.BusinessPartnerName}" +
                                (string.IsNullOrWhiteSpace(bp.AddressStreet) && string.IsNullOrWhiteSpace(bp.AddressCity)
                                    ? "" : $" ({bp.AddressStreet} {bp.AddressCity})".Trim());
                            accountOptions.Add((bp.BusinessPartnerId, desc));
                            hits++;
                        }
                        lookupLog.AppendLine(hits > 0
                            ? $"ค้นลูกค้า '{lk.Query}' ใน SAP: พบ {hits} รายการ (เพิ่มเข้ารายการให้เลือกแล้ว)"
                            : $"ค้นลูกค้า '{lk.Query}' ใน SAP: ไม่พบรายการที่ตรง");
                    }
                }
                catch
                {
                    // SAP unreachable/misconfigured this turn — record it so the AI can tell the user
                    // instead of silently looping. Never fatal to the rest of the chat.
                    lookupLog.AppendLine($"ค้น {lk.Type} '{lk.Query}' ใน SAP: เชื่อมต่อ SAP ไม่ได้ในตอนนี้");
                }
            }
        }
        if (result is null) throw new HttpApiException(500, "AI chat produced no result"); // unreachable: the loop always runs and assigns

        await repo.SaveChatMessageAsync(docId, "assistant", result.Reply, null, null, "AI");
        // A front-end action turn (e.g. "search Zoho") is NOT an edit: it must not write the document
        // or reset its mapping status. Only persist header/lines when the AI actually made an edit.
        if (result.Action is null)
        {
            // MERGE, never replace. The AI is given (and returns) only the extraction schema's own
            // header keys — invoice no, dates, vendor, totals. Everything the SAP tabs add lives in
            // the same header dictionary (taxItems, whtItems, glItems, taxReportingDate,
            // businessPlace, sapDocType, refDocType, …), so writing the AI's header straight over
            // the stored one silently erased the whole Tax / Withholding Tax / G/L tab — one chat
            // correction and the document had to be rebuilt by hand.
            var mergedHeader = new Dictionary<string, object?>(header);
            foreach (var kv in result.Header) mergedHeader[kv.Key] = kv.Value;
            await repo.UpdateHeaderAsync(docId, module, mergedHeader);
            await repo.SaveLinesAsync(module, docId, result.Lines);
            var docT = DocumentTables.For(module).Doc;
            await using (var conn = await GetDbAsync())
                await conn.ExecuteAsync($"UPDATE {docT} SET Status=CASE WHEN Status='POSTED' THEN Status ELSE 'NEW' END, MapStatus=NULL, MapMessage=NULL WHERE DocId=@docId", new { docId });
            await repo.LogAuditAsync(docId, module, "UPDATE", user, detail: "Edited via AI chat: " + message[..Math.Min(200, message.Length)], fileName: doc.GetStr("fileName"));
        }
        var outDoc = await repo.GetDocumentAsync(docId);
        // materialCodes: line index -> Material code the AI picked for that line, if any.
        // shipToCode: the Ship-to code the AI picked for the whole document, if any.
        // customerCode: the Account/Business Partner code the AI picked from the live SAP search, if any.
        // None of these are persisted here -- the frontend applies each as a plain selection (see
        // sendChat()), same as picking manually; only an explicit save action creates/updates master
        // data.
        return Ok(new { reply = result.Reply, document = outDoc, materialCodes = result.MaterialCodes, shipToCode = result.ShipToCode, customerCode = result.CustomerCode,
            action = result.Action is null ? null : new { type = result.Action.Type, query = result.Action.Query } });
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
        dynamic? d = await GetDbInstance().QueryOneAsync($"SELECT StoredPath, FileName, Module, FileExpiredAt FROM {docT} WHERE DocId=@docId", new { docId });
        string? path = d?.StoredPath;
        if (d == null || string.IsNullOrEmpty(path)) throw new HttpApiException(404, "Original file not found");
        object? expiredAt = d.FileExpiredAt;
        string fileName = d.FileName ?? "";
        string docModule = (string?)d.Module ?? "";
        if (!System.IO.File.Exists(path))
        {
            // Local copy is gone (removed after archiving) - stream it back from SharePoint if it was archived.
            var arch = await archive.FindAsync(path);
            var target = arch is { Status: "DONE", RemoteItemId: not null } ? graph.TargetFor(arch.CompanyCode, docModule) : null;
            if (arch?.RemoteItemId == null || target == null) throw new HttpApiException(404, MissingFileMessage(expiredAt));
            try
            {
                var res = await graph.OpenAsync(target, arch.RemoteItemId, HttpContext.RequestAborted);
                Response.RegisterForDispose(res);
                var stream = await res.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
                // same inline / ?download=1 behaviour as the local file below
                return string.Equals(Request.Query["download"], "1", StringComparison.Ordinal)
                    ? File(stream, MediaTypeForFile(fileName), fileName)
                    : File(stream, MediaTypeForFile(fileName));
            }
            catch (Exception e) when (e is not HttpApiException)
            {
                logger.LogError(e,
                    "Could not retrieve archived source file for document {DocId} from SharePoint",
                    docId);
                throw new HttpApiException(502, "Original file is archived in SharePoint but could not be retrieved: " + e.Message);
            }
        }

        // Served INLINE, with the file's real media type. Passing a download name here (the third
        // PhysicalFile argument) makes ASP.NET send Content-Disposition: attachment, and that plus
        // "application/octet-stream" is why the "View document" modal's <iframe> came up blank —
        // the browser treated the response as a download instead of something to render.
        // ?download=1 still gets the attachment behaviour for a real Save-as.
        var download = string.Equals(Request.Query["download"], "1", StringComparison.Ordinal);
        var contentType = MediaTypeForFile(fileName);
        return download
            ? PhysicalFile(Path.GetFullPath(path), contentType, fileName)
            : PhysicalFile(Path.GetFullPath(path), contentType);
    }

    // Media type from the file's extension. Only the types this app actually stores are listed;
    // anything else falls back to octet-stream, which the browser offers as a download.
    private static string MediaTypeForFile(string fileName) =>
        (Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };

    // GET /api/documents/123/export/input-vat
    // The Input VAT workbook Finance uploads into SAP — one row per tax invoice found in the
    // bundle, laid out like 2000_Input Vat Template 1.xlsx. Reads what is on screen in the Tax tab,
    // so whatever the person corrected there is what comes out.
    [HttpGet("api/documents/{docId:int}/export/input-vat")]
    public async Task<IActionResult> ExportInputVat(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var module = doc.GetStr("module");
        if (module is not ("AP" or "II"))
            throw new HttpApiException(400, "The input-VAT file is only produced for supplier invoices");

        var header = (Dictionary<string, object?>)doc["header"]!;
        // Supplier invoices used to be GLC's alone, and the company code was pinned to GLC here.
        // MGT files them now too, so it comes from the document's own SalesOrg; a document with no
        // company stamped on it (anything imported before that column existed) still falls back to
        // GLC, which is what every one of those rows actually was.
        var company = await ExportCompanyAsync(docId);
        var bytes = Export.InputVatExcel.Build(
            doc,
            companyName: config.ExportCompanyName,
            companyCode: company?.CompanyCode ?? "2000",
            accounts: new Export.InputVatExcel.Accounts(config.InputVatLine1Account, config.InputVatLine2Account));

        await repo.LogAuditAsync(docId, module, "EXPORT", await ActorAsync(),
            detail: "Exported the input-VAT file", fileName: doc.GetStr("fileName"));

        var invoiceNo = new string(header.GetStr("invoiceNo").Where(char.IsLetterOrDigit).ToArray());
        var name = $"InputVat_{docId}{(invoiceNo.Length > 0 ? "_" + invoiceNo : "")}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name);
    }

    // GET /api/documents/123/export/journal-voucher
    // The Journal Voucher workbook (Control Sheet + one voucher sheet), built from the G/L Account
    // Items table on screen. The running number starts at Export:JournalVoucher:StartRunning and
    // is offset by the document id so two documents never collide; Finance renumbers when they
    // merge files, which is why it is a plain configurable base rather than a stored counter.
    [HttpGet("api/documents/{docId:int}/export/journal-voucher")]
    public async Task<IActionResult> ExportJournalVoucher(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var module = doc.GetStr("module");
        if (module is not ("AP" or "II"))
            throw new HttpApiException(400, "The journal-voucher file is only produced for supplier invoices");

        var company = await ExportCompanyAsync(docId);
        var bytes = Export.JournalVoucherExcel.Build(
            doc,
            companyName: config.ExportCompanyName,
            companyCode: company?.CompanyCode ?? "2000",
            runningNumber: config.JournalVoucherStartRunning + docId);

        await repo.LogAuditAsync(docId, module, "EXPORT", await ActorAsync(),
            detail: "Exported the journal-voucher file", fileName: doc.GetStr("fileName"));

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"JournalVoucher_{docId}.xlsx");
    }

    // GET /api/documents/123/export/sap-import
    // The workbook the SAP Fiori app "Import Supplier Invoices" takes. Same data the direct post
    // would send, but as a file, so Finance can read it before anything reaches SAP — the two
    // routes the user asked for (review-by-Excel, or post straight through the API).
    [HttpGet("api/documents/{docId:int}/export/sap-import")]
    public async Task<IActionResult> ExportSapImport(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var module = doc.GetStr("module");
        if (module is not ("AP" or "II"))
            throw new HttpApiException(400, "The SAP import file is only produced for supplier invoices");

        var company = await ExportCompanyAsync(docId);
        var bytes = Export.SapImportSupplierInvoiceExcel.Build(
            doc,
            // A bundle can carry costs from several suppliers, and one SAP supplier invoice takes
            // exactly one; the export splits them and numbers the invoices inside the file itself.
            companyCode: company?.CompanyCode ?? "2000",
            // The document says payment terms in words ("30 days"); SAP wants the key (5004). The
            // company's own list is the only place that mapping exists.
            paymentTerms: await PaymentTermsMasterAsync());

        await repo.LogAuditAsync(docId, module, "EXPORT", await ActorAsync(),
            detail: "Exported the SAP import file", fileName: doc.GetStr("fileName"));

        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SapImportSupplierInvoice_{docId}.xlsx");
    }

    // The payment-terms code/text list from dbo.SysDataMapping, in the shape the export wants.
    // Absent table or empty list is fine: nothing resolves, and the column is simply left blank.
    private async Task<Export.SapImportSupplierInvoiceExcel.PaymentTerms> PaymentTermsMasterAsync()
    {
        var all = await masters.LoadAllAsync();
        var rows = new List<(string, string)>();
        if (all.TryGetValue("paymentterms", out var list) && list is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                var d = DynamicRow.ToDict(item);
                var code = d.GetStr("Code");
                if (code.Length > 0) rows.Add((code, d.GetStr("Text")));
            }
        }
        return new Export.SapImportSupplierInvoiceExcel.PaymentTerms(rows);
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

        // A posted (or split-parent) document is read-only: mapping may still run so the page can
        // show the result, but nothing is written back - otherwise reopening it after posting would
        // silently replace the customer / ship-to / materials that were actually sent.
        var locked = doc.GetStr("status") is "POSTED" or "SPLIT" or "PARTIAL";

        // Keep the customer / ship-to saved on the document (the person's earlier pick) unless this
        // call picks a new one or the header text itself was edited. Without this, a reload re-ran
        // the automatic match and, when two customer codes share one Tax ID, jumped back to the
        // first code.
        var headerEdited = newHeader != null && !DictEquals(newHeader, oldHeader);
        if (module == "SO" && (locked || !headerEdited))
        {
            if (manual.Get("header") is not Dictionary<string, object?> mh) { mh = new(); manual["header"] = mh; }
            if (string.IsNullOrEmpty(mh.Get("customer")?.ToString()) && doc.GetStr("partnerCode") is { Length: > 0 } savedP)
                mh["savedCustomer"] = savedP;
            if (string.IsNullOrEmpty(mh.Get("shipTo")?.ToString()) && doc.GetStr("shipToCode") is { Length: > 0 } savedS)
                mh["savedShipTo"] = savedS;
        }

        var header = oldHeader; var lines = oldLines;
        if (locked) { newHeader = null; newLines = null; }
        if (newHeader != null) { await repo.UpdateHeaderAsync(docId, module, newHeader); header = newHeader; }
        if (newLines != null)
        {
            await repo.SaveLinesAsync(module, docId, newLines.OfType<Dictionary<string, object?>>().ToList());
            lines = (List<Dictionary<string, object?>>)(await repo.GetDocumentAsync(docId))["lines"]!;
        }

        if (module == "SO" && !locked)
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

        // What the document does not print, the matched master supplies. A shipping bundle is
        // headed by the FORM SHIPPING EXPENSE sheet, an internal cost summary that carries no tax
        // id and no branch, so those fields came back empty and the person had to type in what
        // the system already knows — the vendor has just been matched, and its master row holds
        // the number Finance registered. Only empty fields are filled, so anything actually read
        // off the document always wins, and nothing is invented: a field with no master value
        // stays empty.
        if (!locked && module is "AP" or "II"
            && resHeader.Get("vendor") is Dictionary<string, object?> venRes
            && venRes.GetStr("status") is "ok" or "manual"
            && masterData.Vendors.FirstOrDefault(x => x.GetStr("VendorCode") == venRes.GetStr("code")) is { } venMaster)
        {
            var filled = new List<string>();
            void FillFromMaster(string headerField, string masterField)
            {
                if (header.GetStr(headerField).Trim().Length > 0) return;
                var value = venMaster.GetStr(masterField).Trim();
                if (value.Length == 0) return;
                header[headerField] = value;
                filled.Add(headerField);
            }
            FillFromMaster("vendorTaxId", "TaxId");
            FillFromMaster("branch", "Branch");
            FillFromMaster("paymentTerms", "PaymentTerms");
            if (filled.Count > 0) await repo.UpdateHeaderAsync(docId, module, header);
        }

        if (locked)
        {
            res["document"] = doc;
            return Ok(res);
        }

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
                    Status=CASE WHEN Status='POSTED' THEN 'POSTED' WHEN Status='SPLIT' THEN 'SPLIT' WHEN @pass=1 THEN 'MAPPED' ELSE 'INCOMPLETE' END,
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
        var (source, created) = await SplitCoreAsync(docId, body.GetStr("mode") == "vendor",
            body.Get("assign") as Dictionary<string, object?> ?? new(), await ActorAsync());
        return Ok(new { source, created });
    }

    // Shared by the endpoint and by Upload's automatic vendor split.
    private async Task<(Dictionary<string, object?> Source, List<Dictionary<string, object?>> Created)> SplitCoreAsync(
        int docId, bool byVendor, Dictionary<string, object?> assign, string user)
    {
        var doc = await repo.GetDocumentAsync(docId);
        var module = doc.GetStr("module");
        // "vendor" mode splits a shipping bundle by the FORM's VENDOR column — one MIRO / FB60
        // document per vendor code, since SAP posts one document to one vendor. The manual
        // group-assignment mode stays Sales-Order-only.
        if (byVendor)
        {
            if (module is not ("AP" or "II"))
                throw new HttpApiException(400, "Splitting by vendor is only available for Supplier Invoice / Incoming Invoice documents");
        }
        else if (module != "SO")
        {
            throw new HttpApiException(400, "Split is only available for Sales Order documents");
        }
        if (doc.GetStr("status") is "POSTED" or "SPLIT" or "PARTIAL") throw new HttpApiException(400, "This document has been posted (fully or partly) or already split");
        if (doc.Get("sourceDocId") != null) throw new HttpApiException(400, "A document split from another cannot be split again");

        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var groups = new SortedDictionary<int, List<Dictionary<string, object?>>>();
        var groupVendor = new Dictionary<int, string>();
        if (byVendor)
        {
            // Tax rows (WHT / VAT) belong to the document's own vendor, which is the one carrying
            // the largest cost total — the shipping agent that re-bills everything else — unless
            // the row names a vendor itself: the import VAT on a customs receipt is paid to the
            // Customs Department and has to be posted on that vendor's own document.
            static string VendorOf(Dictionary<string, object?> l) =>
                ((l.Get("extra") as Dictionary<string, object?>)?.GetStr("vendorCode") ?? "").Trim();
            static bool IsTaxRow(Dictionary<string, object?> l) =>
                l.GetStr("extCode") is "WHT" or "VAT";

            var byCode = lines.Where(l => !IsTaxRow(l) && VendorOf(l).Length > 0)
                              .GroupBy(VendorOf)
                              .OrderByDescending(g => g.Sum(l => Num(l.Get("amount"))))
                              .ToList();
            if (byCode.Count < 2) throw new HttpApiException(400, "This document's lines carry fewer than 2 vendor codes, so there is nothing to split");
            var mainVendor = byCode[0].Key;
            var vendorsWithCosts = byCode.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            var gNo = 0;
            foreach (var g in byCode.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                gNo++;
                groupVendor[gNo] = g.Key;
                var list = g.ToList();
                // A tax row follows its own vendor code when that vendor is one of the groups;
                // a row with no code, or with one that has no costs of its own to be posted
                // against, stays with the main vendor.
                list.AddRange(lines.Where(IsTaxRow).Where(l =>
                {
                    var v = VendorOf(l);
                    return v.Length > 0 && vendorsWithCosts.Contains(v) ? v == g.Key : g.Key == mainVendor;
                }));
                groups[gNo] = list;
            }
            var orphans = lines.Where(l => !IsTaxRow(l) && VendorOf(l).Length == 0).ToList();
            if (orphans.Count > 0)
            {
                var mainNo = groupVendor.First(kv => kv.Value == mainVendor).Key;
                groups[mainNo].AddRange(orphans); // no vendor code read — keep them with the main vendor
            }
        }
        else
        {
            foreach (var l in lines)
            {
                var itemNo = l.Get("itemNo")?.ToString() ?? "";
                var g = (int)Num(assign.Get(itemNo));
                if (g <= 0) continue;
                if (!groups.TryGetValue(g, out var list)) groups[g] = list = [];
                list.Add(l);
            }
            if (groups.Count < 2) throw new HttpApiException(400, "At least 2 groups are required to split");
        }

        var docT = DocumentTables.For(module).Doc;
        dynamic? src = await GetDbInstance().QueryOneAsync($"SELECT StoredPath, FileSize, RawText FROM {docT} WHERE DocId=@docId", new { docId });
        var header = (Dictionary<string, object?>)doc["header"]!;
        var created = new List<Dictionary<string, object?>>();
        foreach (var (gNo, gLines) in groups)
        {
            var gHeader = new Dictionary<string, object?>(header);
            var costTotal = gLines.Where(l => l.GetStr("extCode") is not ("WHT" or "VAT")).Sum(l => Num(l.Get("amount")));
            var gVat = gLines.Where(l => l.GetStr("extCode") == "VAT").Sum(l => Num(l.Get("amount")));
            var gWht = gLines.Where(l => l.GetStr("extCode") == "WHT").Sum(l => Num(l.Get("amount")));
            if (byVendor)
            {
                gHeader["subTotal"] = costTotal;
                gHeader["vatAmount"] = gVat;
                gHeader["whtAmount"] = gWht;
                gHeader["totalAmount"] = costTotal + gVat;
                gHeader["vendorCode"] = groupVendor.GetValueOrDefault(gNo, "");
                // only the main vendor keeps the tax tabs' seeded rows; the rest start clean
                if (gVat <= 0) gHeader.Remove("taxItems");
                if (gWht <= 0) gHeader.Remove("whtItems");
                gHeader.Remove("glItems");
            }
            else
            {
                var gTotal = gLines.Sum(l => Num(l.Get("amount")));
                gHeader["totalAmount"] = gTotal; gHeader["subTotal"] = gTotal;
                if (gHeader.GetStr("poNo").Length > 0) gHeader["poNo"] = $"{gHeader.GetStr("poNo")}-{gNo}";
                // Requested Delivery Date follows the group's own lines: after "split by delivery
                // date" every line in a child ships on one date, but the child used to keep the
                // PARENT's header date (e.g. 24/07 on the 24/08 child). When the group's lines agree
                // on one date, that date becomes the child's header date; mixed/blank keeps the old one.
                var gDates = gLines
                    .Select(l => (l.Get("extra") as Dictionary<string, object?>).GetStr("deliveryDate").Trim())
                    .Where(x => x.Length > 0).Distinct().ToList();
                if (gDates.Count == 1) gHeader["deliveryDate"] = gDates[0];
            }
            var d = DocumentRepository.Denorm(module, gHeader);
            var newId = await GetDbInstance().InsertReturningIdAsync($"""
                INSERT {docT}(Module,FileName,StoredPath,FileSize,OcrProvider,OcrConfidence,OcrConfidenceNote,Status,
                      DocNo,DocDate,PostingDate,PartnerName,PartnerTaxId,Currency,SubTotal,VatRate,VatAmount,
                      WhtAmount,TotalAmount,HeaderJson,RawText,CreatedBy,SourceDocId)
                VALUES(@module,@fileName,@storedPath,@fileSize,@provider,@confidence,@confidenceNote,'NEW',
                      @docNo,@docDate,@postingDate,@partnerName,@partnerTaxId,@currency,@subTotal,@vatRate,@vatAmount,
                      @whtAmount,@totalAmount,@headerJson,@rawText,@user,@sourceDocId);
                SELECT SCOPE_IDENTITY();
                """, new
            {
                module, fileName = doc.GetStr("fileName"), storedPath = (string?)src?.StoredPath, fileSize = (int?)src?.FileSize,
                provider = doc.Get("provider"), confidence = doc.Get("confidence"), confidenceNote = doc.GetStr("confidenceNote"),
                docNo = d.Get("DocNo"), docDate = d.Get("DocDate"), postingDate = d.Get("PostingDate"),
                partnerName = d.Get("PartnerName"), partnerTaxId = d.Get("PartnerTaxId"), currency = d.Get("Currency"),
                subTotal = d.Get("SubTotal"), vatRate = d.Get("VatRate"), vatAmount = d.Get("VatAmount"),
                whtAmount = d.Get("WhtAmount"), totalAmount = d.Get("TotalAmount"),
                headerJson = JsonSerializer.Serialize(gHeader, PyJson.Options), rawText = (string?)src?.RawText, user, sourceDocId = docId,
            });
            await repo.SaveLinesAsync(module, newId, gLines.Select(l => new Dictionary<string, object?>
            {
                ["extCode"] = l.Get("extCode"), ["desc"] = l.Get("desc"), ["qty"] = l.Get("qty"), ["uom"] = l.Get("uom"),
                ["price"] = l.Get("price"), ["amount"] = l.Get("amount"), ["materialCode"] = l.Get("materialCode"), ["extra"] = l.Get("extra"),
            }).ToList());
            await repo.LogAuditAsync(newId, module, "CREATE", user, detail: $"Split from document #{docId}", docNo: d.GetStr("DocNo"), fileName: doc.GetStr("fileName"));
            created.Add(await repo.GetDocumentAsync(newId));
        }

        await using (var conn = await GetDbAsync())
            await conn.ExecuteAsync($"UPDATE {docT} SET Status='SPLIT', UpdatedAt=SYSDATETIME() WHERE DocId=@docId", new { docId });
        await repo.LogAuditAsync(docId, module, "UPDATE", user,
            detail: $"Split into {groups.Count} document(s){(byVendor ? " by vendor" : "")}: {string.Join(", ", created.Select(c => c["docId"]))}", fileName: doc.GetStr("fileName"));
        return (await repo.GetDocumentAsync(docId), created);
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
                INSERT ocr.PostLog(DocId,Module,SapDocNo,Endpoint,PayloadJson,Success,Message,PostedBy,SalesOrg)
                VALUES(@docId,@module,@sapDocNo,@endpoint,@payloadJson,@success,@message,@user,@salesOrg)
                """, new
            {
                docId, module, sapDocNo = r.SapDocNo, endpoint = r.Endpoint,
                payloadJson = JsonSerializer.Serialize(payload, PyJson.Options), success = r.Success ? 1 : 0, message = r.Message, user,
                // Which company's submission history this belongs to — the poster's own, Admin
                // included, for the same reason a document is filed under the importer's company.
                salesOrg = await OwnCompanyAsync() is { Length: > 0 } own ? own : null,
            }, tx);
            if (r.Success)
                await conn.ExecuteAsync($"UPDATE {docT} SET Status='POSTED', SapDocNo=@sapDocNo, CompanyCode=@companyCode, PostedAt=SYSDATETIME(), PostedBy=@user, UpdatedAt=SYSDATETIME() WHERE DocId=@docId",
                    new { sapDocNo = r.SapDocNo, companyCode, user, docId }, tx);
            await tx.CommitAsync();
        }
        // Queue the source file for SharePoint (real posts only, not simulation). Best effort:
        // the post is already committed above. Waits in the queue until a target covers it.
        if (r.Success && !r.Simulated)
        {
            try
            {
                // GLC Sales Order: folder "{CustomerCode}_{CustomerName}" (the GLC target puts year/month
                // in front of it - DateFirst). Other GLC documents keep the default {Company}/{Module}.
                string? subPath = null;
                if (module == "SO" && companyCode == "GLC")
                {
                    var code = doc.GetStr("partnerCode");
                    var hdr = (Dictionary<string, object?>)doc["header"]!;
                    dynamic? cust = code.Length == 0 ? null : await GetDbInstance().QueryOneAsync(
                        "SELECT TOP 1 CompanyNameSAP, CompanyName FROM ocr.Customer WHERE ComcompyCodeSAP=@code " +
                        "ORDER BY CASE WHEN SalesOrg=@org THEN 0 ELSE 1 END", new { code, org = hdr.GetStr("salesOrg") });
                    string name = (string?)cust?.CompanyNameSAP is { Length: > 0 } en ? en
                        : (string?)cust?.CompanyName is { Length: > 0 } th ? th : hdr.GetStr("customerName");
                    subPath = MgtOcr.Api.Services.ZohoArchiveFolder.Build(null, code, name).Customer;
                }
                await archive.EnqueueAsync(docId, docT, companyCode ?? "", module, subPath);
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "Could not queue doc {DocId} for SharePoint (run sql/27 + sql/29?)",
                    docId);
            }
        }
        return Ok(new { success = r.Success, simulated = r.Simulated, sapDocNo = r.SapDocNo, endpoint = r.Endpoint, message = r.Message, document = await repo.GetDocumentAsync(docId) });
    }

    // ===================================================================================
    // Sales Order: one document -> one Sales Order PER DELIVERY DATE, sent from the same page
    // (replaces "split into child documents", 5 Oct 2026). Lines are grouped by their delivery date
    // (SoDeliveryGroups); each group is posted as its own SAP Sales Order with the document's own
    // Customer PO number. Each line records the SO it went into (ocr.SalesOrderLine.PostedSoNo,
    // sql/31), so a retry re-sends only the groups whose lines are not posted yet.
    // Status: all groups posted -> POSTED, some -> PARTIAL, none -> MAPPED.
    // PostDocument above is left untouched (AP/II, and SO documents from before this change).
    // ===================================================================================

    // Same mapping context PayloadForAsync builds, without building one payload for the whole document.
    private async Task<(Dictionary<string, object?> Header, List<Dictionary<string, object?>> Lines,
        Dictionary<string, object?> Res, Dictionary<string, object?>? Pm, Dictionary<string, object?> Source)> SoMappingAsync(Dictionary<string, object?> doc)
    {
        var header = (Dictionary<string, object?>)doc["header"]!;
        header["salesOrg"] = await SalesOrgAsync(header);
        var companyCode = CompanyCodeForSalesOrg(header.GetStr("salesOrg"));
        var masterData = MasterSchema.ForSalesOrg(await masters.LoadForMappingAsync("SO", companyCode), companyCode);
        var lines = (List<Dictionary<string, object?>>)doc["lines"]!;
        var res = MappingEngine.RunMapping("SO", header, lines, masterData, StoredManual(doc), companyCode,
            IsGlcSalesOrg(header.GetStr("salesOrg")));
        var custCode = ((Dictionary<string, object?>)res["header"]!).Get("customer") is Dictionary<string, object?> cr ? cr.GetStr("code") : "";
        var pm = masterData.Customers.FirstOrDefault(c => c.GetStr("CustomerCode") == custCode);
        var source = new Dictionary<string, object?>
        {
            ["docId"] = doc.Get("docId"), ["file"] = doc.Get("fileName"), ["ocrProvider"] = doc.Get("provider"), ["confidence"] = doc.Get("confidence"),
        };
        return (header, lines, res, pm, source);
    }

    // The SAP payload of ONE delivery-date group: the group's lines + their own mapping rows; the
    // header date is set to the group's date (the builder prefers the lines' common date anyway).
    private Dictionary<string, object?> SoGroupPayload(SoGroup g, Dictionary<string, object?> header,
        List<Dictionary<string, object?>> lines, Dictionary<string, object?> res, Dictionary<string, object?>? pm,
        Dictionary<string, object?> source)
    {
        var resLines = (List<Dictionary<string, object?>>)res["lines"]!;
        var gHeader = new Dictionary<string, object?>(header);
        if (g.Key.Length > 0) gHeader["deliveryDate"] = g.Key;
        var gRes = new Dictionary<string, object?>(res)
        {
            ["lines"] = g.Indexes.Select(i => resLines[i]).ToList(),
        };
        return SapPayloadBuilder.BuildPayload(config, "SO", gHeader, g.Indexes.Select(i => lines[i]).ToList(), gRes, pm, source);
    }

    // GET /api/documents/{id}/so-posts -- the delivery-date groups of this Sales Order and the send
    // state of each (posted SO no. / last error) for the Step 3 panel. Before sql/31 is run the
    // columns don't exist yet: groups come back with no state rather than failing the page.
    [HttpGet("api/documents/{docId:int}/so-posts")]
    public async Task<IActionResult> SoPostList(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("module") != "SO") throw new HttpApiException(400, "Only Sales Order documents have delivery-date groups");
        var groups = SoDeliveryGroups.Build((Dictionary<string, object?>)doc["header"]!, (List<Dictionary<string, object?>>)doc["lines"]!);
        List<SoGroupState> states;
        try
        {
            states = await soPosts.GroupStatesAsync(docId, groups);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            logger.LogWarning(ex,
                "Could not load Sales Order post states for document {DocId}; returning empty states",
                docId);
            states = groups
                .Select(g => new SoGroupState(g.Key, g.ItemNos, false, null, null, null))
                .ToList();
        }
        return Ok(new { groups = states });
    }

    // GET /api/documents/{id}/payload-so -- View Payload: one SAP payload per delivery-date group.
    [HttpGet("api/documents/{docId:int}/payload-so")]
    public async Task<IActionResult> SoPayloadPreview(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("module") != "SO") throw new HttpApiException(400, "Only Sales Order documents");
        var (header, lines, res, pm, source) = await SoMappingAsync(doc);
        var groups = SoDeliveryGroups.Build(header, lines);
        return Ok(new
        {
            pass = res.Get("pass"), errors = res["errors"],
            payloads = groups.Select(g => new { key = g.Key, itemNos = g.ItemNos, payload = SoGroupPayload(g, header, lines, res, pm, source) }),
        });
    }

    // POST /api/documents/{id}/post-so -- GLC: send every delivery-date group that has not been
    // posted yet as its own SAP Sales Order. Groups already posted are skipped (no duplicates);
    // every group is attempted even if an earlier one fails, and each result is recorded.
    [HttpPost("api/documents/{docId:int}/post-so")]
    public async Task<IActionResult> PostSalesOrderGroups(int docId)
    {
        var doc = await repo.GetDocumentAsync(docId);
        if (doc.GetStr("module") != "SO") throw new HttpApiException(400, "Only Sales Order documents");
        var status = doc.GetStr("status");
        if (status == "POSTED") throw new HttpApiException(400, $"This document has already been posted to SAP ({doc.GetStr("sapDocNo")})");
        if (status == "SPLIT") throw new HttpApiException(400, "This document was split into separate documents — send those instead");
        if (status != "PARTIAL" && (status != "MAPPED" || doc.GetStr("mapStatus") != "PASS"))
            throw new HttpApiException(400, "Mapping must pass before posting to SAP");

        var (header, lines, res, pm, source) = await SoMappingAsync(doc);
        if (res.Get("pass") is not true)
            throw new HttpApiException(400, $"Mapping has not passed — {((List<Dictionary<string, object?>>)res["errors"]!).Count} missing item(s)");

        var user = await ActorAsync();
        var companyCode = await CompanyNameForPostAsync("SO", header);
        var salesOrg = await OwnCompanyAsync() is { Length: > 0 } own ? own : null;
        var groups = SoDeliveryGroups.Build(header, lines);
        var states = (await soPosts.GroupStatesAsync(docId, groups)).ToDictionary(x => x.Key);
        var results = new List<object>();
        var anySimulated = false;
        foreach (var g in groups)
        {
            if (states.TryGetValue(g.Key, out var done) && done.Posted)
            {
                results.Add(new { key = g.Key, itemNos = g.ItemNos, success = true, skipped = true, docNo = done.DocNo, message = "Already posted" });
                continue;
            }
            var payload = SoGroupPayload(g, header, lines, res, pm, source);
            SapPostResult r;
            try
            {
                r = await sap.PostAsync("SO", payload);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SAP Sales Order post failed for document {DocId}, delivery-date group {DeliveryDate}, items {ItemNos}",
                    docId, g.Key, string.Join(",", g.ItemNos));
                r = new SapPostResult(false, false, "", SapPayloadBuilder.SoEndpoint, ex.Message);
            }
            anySimulated |= r.Simulated;
            await soPosts.RecordAsync(docId, "SO", g.ItemNos, r.Success, r.SapDocNo, r.Message,
                r.Endpoint, JsonSerializer.Serialize(payload, PyJson.Options), user, salesOrg);
            results.Add(new { key = g.Key, itemNos = g.ItemNos, success = r.Success, skipped = false, docNo = r.SapDocNo, message = r.Message, simulated = r.Simulated });
        }
        var newStatus = await soPosts.FinalizeAsync(docId, groups, companyCode, user);
        if (newStatus == "POSTED" && !anySimulated) await EnqueueSoArchiveAsync(docId, doc, header, companyCode);
        return Ok(new { status = newStatus, simulated = anySimulated, results, document = await repo.GetDocumentAsync(docId) });
    }

    // Queue the source file for SharePoint once every group is posted (same folder rule as PostDocument).
    private async Task EnqueueSoArchiveAsync(int docId, Dictionary<string, object?> doc, Dictionary<string, object?> hdr, string? companyCode)
    {
        try
        {
            string? subPath = null;
            if (companyCode == "GLC")
            {
                var code = doc.GetStr("partnerCode");
                dynamic? cust = code.Length == 0 ? null : await GetDbInstance().QueryOneAsync(
                    "SELECT TOP 1 CompanyNameSAP, CompanyName FROM ocr.Customer WHERE ComcompyCodeSAP=@code " +
                    "ORDER BY CASE WHEN SalesOrg=@org THEN 0 ELSE 1 END", new { code, org = hdr.GetStr("salesOrg") });
                string name = (string?)cust?.CompanyNameSAP is { Length: > 0 } en ? en
                    : (string?)cust?.CompanyName is { Length: > 0 } th ? th : hdr.GetStr("customerName");
                subPath = MgtOcr.Api.Services.ZohoArchiveFolder.Build(null, code, name).Customer;
            }
            await archive.EnqueueAsync(docId, DocumentTables.For("SO").Doc, companyCode ?? "", "SO", subPath);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not queue doc {DocId} for SharePoint (run sql/27 + sql/29?)", docId);
        }
    }

    // ---- small local helpers (avoid threading Db through every method signature) ----
    private Db GetDbInstance() => HttpContext.RequestServices.GetRequiredService<Db>();
    private Task<Microsoft.Data.SqlClient.SqlConnection> GetDbAsync() => GetDbInstance().OpenAsync();

    private static bool DictEquals(Dictionary<string, object?> a, Dictionary<string, object?> b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private static bool ListEquals(List<object?> a, List<Dictionary<string, object?>> b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}

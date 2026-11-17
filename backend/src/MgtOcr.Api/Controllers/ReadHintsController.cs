using System.Text.Json;
using MgtOcr.Api.Auth;
using MgtOcr.Api.Services;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Data;
using MgtOcr.Ocr;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

/// <summary>Sales Order "Reading rules" screen (sql/39, Megachem 17 Nov 2026): CS sees and corrects, on the
/// document's own page images, where each field of this customer's PO is printed.
/// The department / company checks of DocumentsController apply here too (DepartmentAccessFilter on {docId}).</summary>
[ApiController]
[ServiceFilter(typeof(DepartmentAccessFilter))]
public class ReadHintsController(DocumentRepository repo, ReadHintService hints, Db db, ICurrentUserAccessor currentUser,
    ILogger<ReadHintsController> log) : ControllerBase
{
    private const string Module = "SO";
    private const int PageDpi = 110;

    public sealed class RuleBody
    {
        public int? PageNo { get; set; }
        public int[]? Box { get; set; }
        public string? Note { get; set; }
    }

    public sealed class StatusBody
    {
        public bool Active { get; set; }
    }

    private static void RequireSo(int docId)
    {
        if (docId < DocumentTables.SoIdBase) throw new HttpApiException(400, "Reading rules are for Sales Orders only");
    }

    private static void RequireField(string field)
    {
        if (!ReadHints.Fields.Contains(field)) throw new HttpApiException(400, $"Unknown field \"{field}\"");
    }

    private async Task<(string Key, string? TaxId, string Name, string Org)> CustomerAsync(int docId)
    {
        var c = await hints.CustomerOfAsync(docId, repo);
        if (c.Key == null)
            throw new HttpApiException(400, "This document has no customer Tax ID or name yet — fill the customer first, then set reading rules");
        return (c.Key, c.TaxId, c.Name, c.Org);
    }

    private async Task<string?> StoredPathAsync(int docId)
    {
        var r = await db.QueryOneAsync("SELECT StoredPath FROM ocr.SalesOrder WHERE DocId=@docId", new { docId });
        string? p = r?.StoredPath;
        return !string.IsNullOrEmpty(p) && System.IO.File.Exists(p) ? p : null;
    }

    // GET /api/documents/100000123/read-hints
    [HttpGet("api/documents/{docId:int}/read-hints")]
    public async Task<IActionResult> Get(int docId)
    {
        RequireSo(docId);
        var c = await hints.CustomerOfAsync(docId, repo);
        var path = await StoredPathAsync(docId);
        var isPdf = path != null && Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var isImage = path != null && OcrEngine.ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant());
        try
        {
            var (positions, hintsUsed, textKey) = await hints.DocPositionsAsync(docId);
            List<ReadHintService.RuleDto> rules = c.Key == null ? [] : await hints.RulesAsync(c.Org, Module, c.Key);
            return Ok(new
            {
                fields = ReadHints.Fields,
                customerKey = c.Key,
                customerTaxId = c.TaxId,
                customerName = c.Name,
                // Customer found in the PDF text before the read (null = none / scanned file)
                textCustomerKey = textKey,
                pageCount = isPdf ? PdfRasterizer.PageCount(path!) : isImage ? 1 : 0,
                fileAvailable = isPdf || isImage,
                positions = string.IsNullOrWhiteSpace(positions) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(positions),
                hintsUsed = string.IsNullOrWhiteSpace(hintsUsed) ? Array.Empty<string>() : hintsUsed.Split(','),
                rules,
            });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            log.LogWarning("[ReadHints] {Message}", ex.Message);
            throw new HttpApiException(503, "Reading rules are not set up in the database yet (run sql/39_customer_read_hints.sql)");
        }
    }

    // GET /api/documents/100000123/page-image/1  -> JPEG of page 1, for drawing the boxes on
    [HttpGet("api/documents/{docId:int}/page-image/{page:int}")]
    public async Task<IActionResult> PageImage(int docId, int page)
    {
        RequireSo(docId);
        var path = await StoredPathAsync(docId)
            ?? throw new HttpApiException(404, "The original file is no longer on the server (removed or archived)");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        Response.Headers.CacheControl = "private, max-age=600";
        if (ext != ".pdf")
        {
            if (page != 1 || !OcrEngine.ImageExt.Contains(ext)) throw new HttpApiException(404, "Page not found");
            return PhysicalFile(Path.GetFullPath(path), ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : "image/png");
        }
        if (page < 1 || page > PdfRasterizer.PageCount(path)) throw new HttpApiException(404, "Page not found");
        var jpg = PdfRasterizer.RenderPageToJpeg(path, page - 1, PageDpi, 80)
            ?? throw new HttpApiException(500, "Could not render the page");
        return File(jpg, "image/jpeg");
    }

    // PUT /api/documents/100000123/read-hints/deliveryDate  {pageNo, box:[ymin,xmin,ymax,xmax] | null, note}
    [HttpPut("api/documents/{docId:int}/read-hints/{field}")]
    public async Task<IActionResult> Save(int docId, string field, [FromBody] RuleBody body)
    {
        RequireSo(docId); RequireField(field);
        var box = body.Box;
        if (box != null && !ReadHints.ValidBox(box)) throw new HttpApiException(400, "The box is not valid — draw it again");
        if (box != null && (body.PageNo is not int p || p < 1)) throw new HttpApiException(400, "Page is required with a box");
        if (box == null && string.IsNullOrWhiteSpace(body.Note)) throw new HttpApiException(400, "Draw a box or write a note");
        var c = await CustomerAsync(docId);
        var user = (await currentUser.RequireAsync()).AuditName;
        await hints.SaveCsAsync(c.Org, Module, c.Key, c.TaxId, c.Name, field, body.PageNo, box, body.Note, user);
        await repo.LogAuditAsync(docId, Module, "READ_RULE", user,
            detail: $"Reading rule {field} set for {c.Name} ({c.Key})" + (box != null ? $": page {body.PageNo} box {string.Join(",", box)}" : "") +
                    (string.IsNullOrWhiteSpace(body.Note) ? "" : $" note: {body.Note!.Trim()}"));
        return await Get(docId);
    }

    // POST /api/documents/100000123/read-hints/deliveryDate/status  {active:false}
    [HttpPost("api/documents/{docId:int}/read-hints/{field}/status")]
    public async Task<IActionResult> SetStatus(int docId, string field, [FromBody] StatusBody body)
    {
        RequireSo(docId); RequireField(field);
        var c = await CustomerAsync(docId);
        var user = (await currentUser.RequireAsync()).AuditName;
        await hints.SetStatusAsync(c.Org, Module, c.Key, c.TaxId, c.Name, field, body.Active, user);
        await repo.LogAuditAsync(docId, Module, "READ_RULE", user,
            detail: $"Reading rule {field} {(body.Active ? "turned on" : "turned off")} for {c.Name} ({c.Key})");
        return await Get(docId);
    }

    // DELETE /api/documents/100000123/read-hints/deliveryDate  -> forget it (the system may learn it again)
    [HttpDelete("api/documents/{docId:int}/read-hints/{field}")]
    public async Task<IActionResult> Delete(int docId, string field)
    {
        RequireSo(docId); RequireField(field);
        var c = await CustomerAsync(docId);
        var user = (await currentUser.RequireAsync()).AuditName;
        await hints.DeleteAsync(c.Org, Module, c.Key, field);
        await repo.LogAuditAsync(docId, Module, "READ_RULE", user, detail: $"Reading rule {field} cleared for {c.Name} ({c.Key})");
        return await Get(docId);
    }
}

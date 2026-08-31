using Dapper;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/audit-logs in app/main.py (lines 904-916).
[ApiController]
[Route("api/audit-logs")]
public class AuditLogController(Db db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> AuditLogs([FromQuery] string module = "", [FromQuery] int? docId = null, [FromQuery] int limit = 200)
    {
        var where = new List<string>();
        var p = new DynamicParameters();
        if (module.Length > 0) { where.Add("Module=@module"); p.Add("module", module.ToUpperInvariant()); }
        if (docId != null) { where.Add("DocId=@docId"); p.Add("docId", docId); }
        var sql = "SELECT TOP (@limit) LogId,DocId,Module,Action,DocNo,FileName,Detail,PerformedBy,CreatedAt,OcrProvider FROM ocr.AuditLog";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY LogId DESC";
        p.Add("limit", limit);
        return Ok(await db.QueryAsync(sql, p));
    }
}

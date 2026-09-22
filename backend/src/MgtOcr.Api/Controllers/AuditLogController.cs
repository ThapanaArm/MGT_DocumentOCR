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
    public async Task<IActionResult> AuditLogs([FromQuery] string module = "", [FromQuery] int? docId = null,
        [FromQuery] string dateFrom = "", [FromQuery] string dateTo = "", [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10)
    {
        page = page < 1 ? 1 : page;
        pageSize = Math.Clamp(pageSize, 1, 200);
        var where = new List<string>();
        var p = new DynamicParameters();
        if (module.Length > 0) { where.Add("Module=@module"); p.Add("module", module.ToUpperInvariant()); }
        if (docId != null) { where.Add("DocId=@docId"); p.Add("docId", docId); }
        if (dateFrom.Length > 0) { where.Add("CreatedAt >= @dateFrom"); p.Add("dateFrom", dateFrom); }
        if (dateTo.Length > 0) { where.Add("CreatedAt < DATEADD(day, 1, @dateTo)"); p.Add("dateTo", dateTo); }
        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var totalRow = await db.QueryOneAsync("SELECT COUNT(*) AS Cnt FROM ocr.AuditLog" + whereSql, p);
        var total = totalRow == null ? 0 : (int)totalRow.Cnt;
        p.Add("off", (page - 1) * pageSize);
        p.Add("ps", pageSize);
        var results = await db.QueryAsync(
            "SELECT LogId,DocId,Module,Action,DocNo,FileName,Detail,PerformedBy,CreatedAt,OcrProvider FROM ocr.AuditLog"
            + whereSql + " ORDER BY LogId DESC OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY", p);
        return Ok(new { results, total });
    }
}

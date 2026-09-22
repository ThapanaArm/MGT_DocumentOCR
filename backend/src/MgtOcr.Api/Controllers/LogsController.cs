using System.Text.Json;
using Dapper;
using MgtOcr.Core;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/logs and /api/logs/{log_id}/payload in app/main.py (lines 880-901).
[ApiController]
[Route("api/logs")]
public class LogsController(Db db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Logs([FromQuery] string dateFrom = "", [FromQuery] string dateTo = "",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        page = page < 1 ? 1 : page;
        pageSize = Math.Clamp(pageSize, 1, 200);
        var where = new List<string>();
        var p = new DynamicParameters();
        if (dateFrom.Length > 0) { where.Add("l.PostedAt >= @dateFrom"); p.Add("dateFrom", dateFrom); }
        if (dateTo.Length > 0) { where.Add("l.PostedAt < DATEADD(day, 1, @dateTo)"); p.Add("dateTo", dateTo); }
        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var totalRow = await db.QueryOneAsync("SELECT COUNT(*) AS Cnt FROM ocr.PostLog l" + whereSql, p);
        var total = totalRow == null ? 0 : (int)totalRow.Cnt;
        p.Add("off", (page - 1) * pageSize);
        p.Add("ps", pageSize);
        var results = await db.QueryAsync($"""
            SELECT l.LogId,l.DocId,l.Module,l.SapDocNo,l.Endpoint,l.Success,
                   l.Message,l.PostedAt,l.PostedBy,
                   d.FileName,d.DocNo,d.PartnerName,d.TotalAmount,d.Currency,d.OcrProvider,
                   (CASE WHEN l.DocId>={DocumentTables.SoIdBase}
                         THEN (SELECT COUNT(*) FROM ocr.SalesOrderLine WHERE DocId=l.DocId)
                         ELSE (SELECT COUNT(*) FROM ocr.DocumentLine WHERE DocId=l.DocId) END) AS Lines
            FROM ocr.PostLog l LEFT JOIN (
                SELECT DocId,FileName,DocNo,PartnerName,TotalAmount,Currency,OcrProvider FROM ocr.Document
                UNION ALL
                SELECT DocId,FileName,DocNo,PartnerName,TotalAmount,Currency,OcrProvider FROM ocr.SalesOrder
            ) d ON d.DocId=l.DocId{whereSql}
            ORDER BY l.LogId DESC OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY
            """, p);
        return Ok(new { results, total });
    }

    [HttpGet("{logId:int}/payload")]
    public async Task<IActionResult> LogPayload(int logId)
    {
        var r = await db.QueryOneAsync("SELECT PayloadJson FROM ocr.PostLog WHERE LogId=@logId", new { logId });
        if (r == null) throw new HttpApiException(404, "Log not found");
        string json = r.PayloadJson ?? "{}";
        return Content(json, "application/json");
    }
}

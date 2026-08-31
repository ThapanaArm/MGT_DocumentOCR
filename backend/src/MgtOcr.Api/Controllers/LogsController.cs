using System.Text.Json;
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
    public async Task<IActionResult> Logs([FromQuery] int limit = 200) => Ok(await db.QueryAsync($"""
        SELECT TOP (@limit) l.LogId,l.DocId,l.Module,l.SapDocNo,l.Endpoint,l.Success,
               l.Message,l.PostedAt,l.PostedBy,
               d.FileName,d.DocNo,d.PartnerName,d.TotalAmount,d.Currency,d.OcrProvider,
               (CASE WHEN l.DocId>={DocumentTables.SoIdBase}
                     THEN (SELECT COUNT(*) FROM ocr.SalesOrderLine WHERE DocId=l.DocId)
                     ELSE (SELECT COUNT(*) FROM ocr.DocumentLine WHERE DocId=l.DocId) END) AS Lines
        FROM ocr.PostLog l LEFT JOIN (
            SELECT DocId,FileName,DocNo,PartnerName,TotalAmount,Currency,OcrProvider FROM ocr.Document
            UNION ALL
            SELECT DocId,FileName,DocNo,PartnerName,TotalAmount,Currency,OcrProvider FROM ocr.SalesOrder
        ) d ON d.DocId=l.DocId
        ORDER BY l.LogId DESC
        """, new { limit }));

    [HttpGet("{logId:int}/payload")]
    public async Task<IActionResult> LogPayload(int logId)
    {
        var r = await db.QueryOneAsync("SELECT PayloadJson FROM ocr.PostLog WHERE LogId=@logId", new { logId });
        if (r == null) throw new HttpApiException(404, "ไม่พบ log");
        string json = r.PayloadJson ?? "{}";
        return Content(json, "application/json");
    }
}

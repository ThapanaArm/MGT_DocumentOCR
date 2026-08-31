using MgtOcr.Core;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/dashboard and GET /api/ocr-usage in app/main.py (lines 919-1058) — the
// Overview page's status/trend/cost/OCR-performance/daily-chart/recent-documents summary.
// byStatus/recent deliberately stay raw PascalCase SQL rows (matching Python's rows(db.query(...))
// for those two fields specifically) while every other field is a hand-built camelCase dict.
[ApiController]
public class DashboardController(Db db) : ControllerBase
{
    private static readonly string[] Statuses = ["NEW", "INCOMPLETE", "MAPPED", "POSTED", "SPLIT"];

    [HttpGet("api/dashboard")]
    public async Task<IActionResult> Dashboard([FromQuery] int days = 7)
    {
        days = Math.Max(1, Math.Min(days, 90));
        var today = DateTime.Today;
        var start = today.AddDays(-(days - 1));

        var byStatus = await db.QueryAsync("""
            SELECT Module, Status, COUNT(*) AS Cnt, SUM(TotalAmount) AS Amount FROM (
                SELECT Module, Status, TotalAmount FROM ocr.Document
                UNION ALL
                SELECT Module, Status, TotalAmount FROM ocr.SalesOrder
            ) x GROUP BY Module, Status
            """);

        var allDocsRaw = await db.QueryAsync("""
            SELECT DocId, Module, Status, CreatedAt, OcrConfidence, OcrProvider, OcrDurationMs,
                   OcrCost, OcrTokensIn, OcrTokensOut, OcrCostCurrency
            FROM (
                SELECT DocId, Module, Status, CreatedAt, OcrConfidence, OcrProvider, OcrDurationMs,
                       OcrCost, OcrTokensIn, OcrTokensOut, OcrCostCurrency FROM ocr.Document
                UNION ALL
                SELECT DocId, Module, Status, CreatedAt, OcrConfidence, OcrProvider, OcrDurationMs,
                       OcrCost, OcrTokensIn, OcrTokensOut, OcrCostCurrency FROM ocr.SalesOrder
            ) x
            """);
        var allDocs = DynamicRow.ToDictList(allDocsRaw);

        var statusCounts = Statuses.ToDictionary(s => s, _ => 0);
        var baselineCounts = Statuses.ToDictionary(s => s, _ => 0);
        var statusTotal = 0; var baselineTotal = 0;
        var byModuleRecent = new Dictionary<string, int>();
        var costByModule = new Dictionary<string, (int Count, long Tokens, double Cost)>();
        var costCurrency = "USD";
        var confidences = new List<double>(); var durations = new List<int>();

        foreach (var r in allDocs)
        {
            var st = r.GetStr("Status");
            var createdAt = r.Get("CreatedAt") as DateTime?;
            if (statusCounts.ContainsKey(st)) statusCounts[st]++;
            statusTotal++;
            if (createdAt is { } ca)
            {
                if (ca.Date < start.Date)
                {
                    if (baselineCounts.ContainsKey(st)) baselineCounts[st]++;
                    baselineTotal++;
                }
                if (ca.Date >= start.Date)
                {
                    var module = r.GetStr("Module");
                    byModuleRecent[module] = byModuleRecent.GetValueOrDefault(module) + 1;
                    var cm = costByModule.GetValueOrDefault(module, (0, 0, 0.0));
                    var tokensIn = Convert.ToInt64(r.Get("OcrTokensIn") ?? 0L);
                    var tokensOut = Convert.ToInt64(r.Get("OcrTokensOut") ?? 0L);
                    var cost = Convert.ToDouble(r.Get("OcrCost") ?? 0.0);
                    costByModule[module] = (cm.Count + 1, cm.Tokens + tokensIn + tokensOut, cm.Cost + cost);
                    if (r.GetStr("OcrCostCurrency").Length > 0) costCurrency = r.GetStr("OcrCostCurrency");
                }
            }
            if (r.Get("OcrConfidence") is not null) confidences.Add(Convert.ToDouble(r.Get("OcrConfidence")));
            if (r.Get("OcrDurationMs") is not null && Convert.ToInt32(r.Get("OcrDurationMs")) != 0) durations.Add(Convert.ToInt32(r.Get("OcrDurationMs")));
        }

        double Pct(int cur, int baseline) => baseline == 0 ? (cur > 0 ? 100.0 : 0.0) : Math.Round((cur - baseline) / (double)baseline * 100, 1);
        var trend = Statuses.Concat(["total"]).ToDictionary(k => k, k => k == "total" ? Pct(statusTotal, baselineTotal) : Pct(statusCounts[k], baselineCounts[k]));
        var byModule = byModuleRecent.OrderByDescending(kv => kv.Value).Select(kv => new { module = kv.Key, count = kv.Value }).ToList();
        var costByModuleList = costByModule.OrderByDescending(kv => kv.Value.Cost)
            .Select(kv => new { module = kv.Key, count = kv.Value.Count, tokens = kv.Value.Tokens, cost = Math.Round(kv.Value.Cost, 4), costCurrency })
            .ToList();

        var allDocIds = allDocs.Select(r => Convert.ToInt32(r.Get("DocId"))).ToHashSet();
        var editedDocIdsRaw = DynamicRow.ToDictList(await db.QueryAsync("SELECT DISTINCT DocId FROM ocr.AuditLog WHERE Action='UPDATE'"));
        var editedDocIds = editedDocIdsRaw.Select(r => Convert.ToInt32(r.Get("DocId"))).ToHashSet();
        var tokensTodayRow = await db.QueryOneAsync("""
            SELECT SUM(ISNULL(OcrTokensIn,0)+ISNULL(OcrTokensOut,0)) AS T FROM (
                SELECT CreatedAt, OcrTokensIn, OcrTokensOut FROM ocr.Document
                UNION ALL
                SELECT CreatedAt, OcrTokensIn, OcrTokensOut FROM ocr.SalesOrder
            ) x WHERE CAST(CreatedAt AS DATE)=CAST(SYSDATETIME() AS DATE)
            """);
        var ocrPerf = new
        {
            avgConfidencePct = confidences.Count > 0 ? Math.Round(confidences.Average() * 100, 1) : (double?)null,
            avgDurationSec = durations.Count > 0 ? Math.Round(durations.Average() / 1000, 1) : (double?)null,
            pctEditedByUser = allDocIds.Count > 0 ? Math.Round(editedDocIds.Intersect(allDocIds).Count() / (double)allDocIds.Count * 100, 1) : 0.0,
            tokensToday = tokensTodayRow == null ? 0 : Convert.ToInt32(((IDictionary<string, object>)tokensTodayRow).TryGetValue("T", out var t) ? t ?? 0 : 0),
        };

        var dailyRaw = DynamicRow.ToDictList(await db.QueryAsync("""
            SELECT CAST(CreatedAt AS DATE) AS Day, COUNT(*) AS DocCount,
                   SUM(CASE WHEN OcrProvider IS NOT NULL AND OcrProvider<>'failed' THEN 1 ELSE 0 END) AS OkCount
            FROM (
                SELECT CreatedAt, OcrProvider FROM ocr.Document WHERE CreatedAt >= @start
                UNION ALL
                SELECT CreatedAt, OcrProvider FROM ocr.SalesOrder WHERE CreatedAt >= @start
            ) x GROUP BY CAST(CreatedAt AS DATE)
            """, new { start }));
        var byDay = dailyRaw.ToDictionary(r => ((DateTime)r.Get("Day")!).ToString("yyyy-MM-dd"), r => r);
        var ocrDaily = new List<object>();
        for (var i = 0; i < days; i++)
        {
            var key = start.AddDays(i).ToString("yyyy-MM-dd");
            var r = byDay.GetValueOrDefault(key);
            ocrDaily.Add(new { date = key, docCount = r != null ? Convert.ToInt32(r.Get("DocCount")) : 0, okCount = r != null ? Convert.ToInt32(r.Get("OkCount") ?? 0) : 0 });
        }

        var recent = await db.QueryAsync("""
            SELECT TOP 8 DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,
                   SapDocNo,CreatedAt,UpdatedAt,CreatedBy,PostedBy FROM (
                SELECT DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,SapDocNo,
                       CreatedAt,UpdatedAt,CreatedBy,PostedBy FROM ocr.Document
                UNION ALL
                SELECT DocId,Module,FileName,DocNo,PartnerName,Status,TotalAmount,SapDocNo,
                       CreatedAt,UpdatedAt,CreatedBy,PostedBy FROM ocr.SalesOrder
            ) x ORDER BY DocId DESC
            """);

        return Ok(new
        {
            byStatus, statusCounts = new Dictionary<string, object?>(statusCounts.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))) { ["total"] = statusTotal },
            trend, byModule, costByModule = costByModuleList, ocrPerf, ocrDaily, recent,
        });
    }

    [HttpGet("api/ocr-usage")]
    public async Task<IActionResult> OcrUsage([FromQuery] int days = 7)
    {
        days = Math.Max(1, Math.Min(days, 90));
        var start = DateTime.Today.AddDays(-(days - 1));
        var raw = DynamicRow.ToDictList(await db.QueryAsync("""
            SELECT CAST(CreatedAt AS DATE) AS Day, COUNT(*) AS DocCount,
                   SUM(ISNULL(OcrTokensIn,0)) AS TokensIn, SUM(ISNULL(OcrTokensOut,0)) AS TokensOut,
                   SUM(ISNULL(OcrCost,0)) AS Cost
            FROM (
                SELECT CreatedAt, OcrTokensIn, OcrTokensOut, OcrCost FROM ocr.Document WHERE CreatedAt >= @start
                UNION ALL
                SELECT CreatedAt, OcrTokensIn, OcrTokensOut, OcrCost FROM ocr.SalesOrder WHERE CreatedAt >= @start
            ) x GROUP BY CAST(CreatedAt AS DATE)
            """, new { start }));
        var byDay = raw.ToDictionary(r => ((DateTime)r.Get("Day")!).ToString("yyyy-MM-dd"), r => r);
        var outList = new List<object>();
        for (var i = 0; i < days; i++)
        {
            var key = start.AddDays(i).ToString("yyyy-MM-dd");
            var r = byDay.GetValueOrDefault(key);
            outList.Add(new
            {
                date = key,
                docCount = r != null ? Convert.ToInt32(r.Get("DocCount")) : 0,
                tokensIn = r != null ? Convert.ToInt32(r.Get("TokensIn")) : 0,
                tokensOut = r != null ? Convert.ToInt32(r.Get("TokensOut")) : 0,
                cost = r != null ? Math.Round(Convert.ToDouble(r.Get("Cost")), 4) : 0.0,
            });
        }
        return Ok(outList);
    }
}

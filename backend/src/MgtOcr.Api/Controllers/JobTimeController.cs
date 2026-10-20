using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Admin-only "Job Time" card on the Overview page (Megachem, 20 Oct 2026): how many minutes each
// document took from the start of its upload (JobStartedAt) to a successful SAP / Zoho post
// (PostedAt) -- the computed column JobTaskTime on ocr.Document / ocr.SalesOrder
// (sql/34_job_tasktime.sql). It is wall-clock time, so a document left overnight counts its
// waiting time too: the summary uses the median (not the mean) plus a bucket breakdown, so a few
// stale documents don't distort the picture. Admin sees every company, so there is no company
// scope here -- the optional `company` filter (1000 = MGT, 2000 = GLC) narrows it on request.
[ApiController]
public class JobTimeController(Db db, ICurrentUserAccessor current) : ControllerBase
{
    // Upper bound in minutes (exclusive); null = no upper bound.
    private static readonly (string Label, double? Max)[] Buckets =
    [
        ("< 5 min", 5), ("5-15 min", 15), ("15-60 min", 60), ("1-8 h", 480), ("8-24 h", 1440), ("> 1 day", null),
    ];

    private const string Source = """
        SELECT DocId, Module, SalesOrg, JobNumber, DocNo, PartnerName, CreatedBy, PostedBy,
               SapDocNo, JobStartedAt, PostedAt, JobTaskTime, OcrDurationMs
        FROM ocr.Document
        UNION ALL
        SELECT DocId, Module, SalesOrg, JobNumber, DocNo, PartnerName, CreatedBy, PostedBy,
               SapDocNo, JobStartedAt, PostedAt, JobTaskTime, OcrDurationMs
        FROM ocr.SalesOrder
        """;

    [HttpGet("api/dashboard/job-time")]
    public async Task<IActionResult> JobTime([FromQuery] int days = 7, [FromQuery] string? company = null,
        [FromQuery] string? module = null, [FromQuery] string? sort = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken ct = default)
    {
        var u = await current.RequireAsync(ct);
        if (!DepartmentAccess.IsAdmin(u)) throw new HttpApiException(403, "Admin only");

        days = Math.Clamp(days, 1, 366);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var start = DateTime.Today.AddDays(-(days - 1));

        // Only fully posted documents that went through the upload flow have a JobTaskTime.
        var where = new List<string> { "JobTaskTime IS NOT NULL", "PostedAt >= @start" };
        var p = new DynamicParameters();
        p.Add("start", start);
        var comp = (company ?? "").Trim();
        if (comp is "1000" or "2000") { where.Add("SalesOrg = @company"); p.Add("company", comp); }
        var mod = (module ?? "").Trim().ToUpperInvariant();
        if (mod is "SO" or "AP" or "II") { where.Add("Module = @module"); p.Add("module", mod); }
        var whereSql = " WHERE " + string.Join(" AND ", where);

        // Summary + buckets from the minutes alone (at most a few thousand numbers per period).
        var all = DynamicRow.ToDictList(await db.QueryAsync(
            $"SELECT Module, SalesOrg, CAST(JobTaskTime AS float) AS Minutes FROM ({Source}) x{whereSql}", p, ct));
        var minutes = all.Select(r => Convert.ToDouble(r.Get("Minutes"))).ToList();

        var buckets = Buckets.Select((b, i) =>
        {
            var min = i == 0 ? double.MinValue : Buckets[i - 1].Max!.Value;
            var max = b.Max ?? double.MaxValue;
            return new { label = b.Label, count = minutes.Count(m => m >= min && m < max) };
        }).ToList();

        var groups = all
            .GroupBy(r => (Module: r.GetStr("Module"), SalesOrg: r.GetStr("SalesOrg")))
            .Select(g =>
            {
                var ms = g.Select(r => Convert.ToDouble(r.Get("Minutes"))).ToList();
                return new { module = g.Key.Module, salesOrg = g.Key.SalesOrg, count = ms.Count, medianMin = Median(ms) };
            })
            .OrderBy(g => g.salesOrg).ThenBy(g => g.module)
            .ToList();

        // The documents themselves, one page at a time: slowest first by default.
        var dir = string.Equals(sort, "fast", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
        p.Add("off", (page - 1) * pageSize);
        p.Add("ps", pageSize);
        var rows = DynamicRow.ToDictList(await db.QueryAsync(
            $"SELECT * FROM ({Source}) x{whereSql} ORDER BY JobTaskTime {dir}, DocId DESC OFFSET @off ROWS FETCH NEXT @ps ROWS ONLY",
            p, ct));

        return Ok(new
        {
            days,
            summary = new
            {
                count = minutes.Count,
                medianMin = Median(minutes),
                within15Pct = minutes.Count == 0 ? (double?)null : Math.Round(100.0 * minutes.Count(m => m < 15) / minutes.Count, 1),
                overDayCount = minutes.Count(m => m >= 1440),
            },
            buckets,
            groups,
            total = minutes.Count,
            results = rows.Select(r => new
            {
                docId = r.Get("DocId"),
                module = r.GetStr("Module"),
                salesOrg = r.GetStr("SalesOrg"),
                jobNumber = r.GetStr("JobNumber"),
                docNo = r.GetStr("DocNo"),
                partnerName = r.GetStr("PartnerName"),
                createdBy = r.GetStr("CreatedBy"),
                postedBy = r.GetStr("PostedBy"),
                sapDocNo = r.GetStr("SapDocNo"),
                jobStartedAt = r.Get("JobStartedAt"),
                postedAt = r.Get("PostedAt"),
                jobTaskTime = r.Get("JobTaskTime") is { } t ? Convert.ToDouble(t) : (double?)null,
                ocrSec = r.Get("OcrDurationMs") is { } ms ? Math.Round(Convert.ToDouble(ms) / 1000, 1) : (double?)null,
            }).ToList(),
        });
    }

    private static double? Median(List<double> xs)
    {
        if (xs.Count == 0) return null;
        var s = xs.OrderBy(x => x).ToList();
        var n = s.Count;
        var m = n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2;
        return Math.Round(m, 2);
    }
}

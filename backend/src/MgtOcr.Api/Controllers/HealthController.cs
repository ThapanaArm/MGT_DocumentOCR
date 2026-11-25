using MgtOcr.Core.Config;
using MgtOcr.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/health in app/main.py — same response shape:
// {ok, db:{db,usr,srv}, counts:{customers,vendors,materials,documents}, ocrProvider, sapMode}
// Monitoring hits this without a token, so it opts out of the global authentication requirement.
[AllowAnonymous]
[ApiController]
[Route("api/health")]
public class HealthController(Db db, AppConfig config) : ControllerBase
{
    // System.IO is spelled out: inside a controller a bare "File" is ControllerBase.File(...).
    private static string? BuiltAt(string dll)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, dll);
            return System.IO.File.Exists(path)
                ? System.IO.File.GetLastWriteTimeUtc(path).ToString("yyyy-MM-dd HH:mm") + "Z"
                : null;
        }
        catch { return null; }
    }

    private static long? SizeOf(string dll)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, dll);
            return System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : (long?)null;
        }
        catch { return null; }
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // TEMP DIAGNOSTIC: capture PingAsync() before the count queries so a failure below
        // still reports exactly which server/database/login this request actually connected
        // as, instead of only the bare SQL exception message.
        object? pingInfo = null;
        try
        {
            var (dbName, usr, srv) = await db.PingAsync();
            pingInfo = new { db = dbName, usr, srv };
            int customers = (int)(await db.QueryOneAsync("SELECT COUNT(*) AS n FROM ocr.Customer"))!.n;
            int vendors = (int)(await db.QueryOneAsync("SELECT COUNT(*) AS n FROM ocr.Vendor"))!.n;
            int materials = (int)(await db.QueryOneAsync("SELECT COUNT(*) AS n FROM ocr.CustomerMaterial"))!.n;
            // Sales Order lives in its own physical table (ocr.SalesOrder) since the AP/SO split —
            // this count must include both, matching the current app/main.py health() query.
            int documents = (int)(await db.QueryOneAsync("SELECT (SELECT COUNT(*) FROM ocr.Document) + (SELECT COUNT(*) FROM ocr.SalesOrder) AS n"))!.n;

            return Ok(new
            {
                ok = true,
                db = pingInfo,
                counts = new { customers, vendors, materials, documents },
                ocrProvider = config.OcrProvider,
                // Which binaries are running (file time of the DLLs next to the app) — shown under the
                // sidebar status line so a deploy that did not take effect is visible (AP, 2026-11-24).
                buildApi = BuiltAt("MgtOcr.Api.dll"),
                buildOcr = BuiltAt("MgtOcr.Ocr.dll"),
                ocrBytes = SizeOf("MgtOcr.Ocr.dll"),
                sapMode = string.IsNullOrEmpty(config.SapWriteBaseUrl) && string.IsNullOrEmpty(config.SapSalesOrderWriteBaseUrl)
                    ? "not-configured" : "live",
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, error = ex.Message, db = pingInfo });
        }
    }
}

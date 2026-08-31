using MgtOcr.Core.Config;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Ported from GET /api/health in app/main.py — same response shape:
// {ok, db:{db,usr,srv}, counts:{customers,vendors,materials,documents}, ocrProvider, sapMode}
[ApiController]
[Route("api/health")]
public class HealthController(Db db, AppConfig config) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            var (dbName, usr, srv) = await db.PingAsync();
            int customers = (int)(await db.QueryOneAsync("SELECT COUNT(*) AS n FROM ocr.Customer"))!.n;
            int vendors = (int)(await db.QueryOneAsync("SELECT COUNT(*) AS n FROM ocr.Vendor"))!.n;
            int materials = (int)(await db.QueryOneAsync("SELECT COUNT(*) AS n FROM ocr.Material"))!.n;
            // Sales Order lives in its own physical table (ocr.SalesOrder) since the AP/SO split —
            // this count must include both, matching the current app/main.py health() query.
            int documents = (int)(await db.QueryOneAsync("SELECT (SELECT COUNT(*) FROM ocr.Document) + (SELECT COUNT(*) FROM ocr.SalesOrder) AS n"))!.n;

            return Ok(new
            {
                ok = true,
                db = new { db = dbName, usr, srv },
                counts = new { customers, vendors, materials, documents },
                ocrProvider = config.OcrProvider,
                sapMode = string.IsNullOrEmpty(config.SapBaseUrl) ? "simulate" : "live",
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }
}

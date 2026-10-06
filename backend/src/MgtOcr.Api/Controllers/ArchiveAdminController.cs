using MgtOcr.Api.Services;
using MgtOcr.Core;
using MgtOcr.Core.Auth;
using MgtOcr.Core.Config;
using MgtOcr.Data;
using Microsoft.AspNetCore.Mvc;

namespace MgtOcr.Api.Controllers;

// Admin page "SharePoint Archive": shows the archive configuration (never the secret itself),
// the ocr.FileArchive status, and runs a live connection test per target (token -> library ->
// upload a tiny file -> delete it). Admin (Ms_User.UserRole = Admin) only.
[ApiController]
public class ArchiveAdminController(AppConfig config, GraphArchiveClient graph, FileArchiveRepository repo,
    ICurrentUserAccessor current) : ControllerBase
{
    private async Task RequireAdminAsync(CancellationToken ct)
    {
        var u = await current.RequireAsync(ct);
        if (!DepartmentAccess.IsAdmin(u)) throw new HttpApiException(403, "Admin only");
    }

    private static object Describe(SharePointTarget t, int index) => new
    {
        index,
        company = t.Company,
        module = t.Module,
        siteUrl = t.SiteUrl,
        dateFirst = t.DateFirst,
        rootFolder = t.RootFolder,
        tenantId = t.TenantId,
        clientId = t.ClientId,
        driveId = t.DriveId,
        secretSet = t.ClientSecret != "",
        usable = t.IsUsable,
        missing = Missing(t),
    };

    private static string[] Missing(SharePointTarget t) => new[]
    {
        t.TenantId == "" ? "TenantId" : null,
        t.ClientId == "" ? "ClientId" : null,
        t.ClientSecret == "" ? "ClientSecret" : null,
        t.DriveId == "" ? "DriveId" : null,
    }.Where(x => x != null).Select(x => x!).ToArray();

    [HttpGet("api/admin/archive")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        object counts, recent;
        string? dbError = null;
        try
        {
            counts = await repo.CountByStatusAsync(ct);
            recent = await repo.RecentAsync(20, ct);
        }
        catch (Exception e)
        {
            // Most likely sql/27_file_archive.sql has not been run yet.
            counts = Array.Empty<object>(); recent = Array.Empty<object>();
            dbError = e.Message;
        }
        return Ok(new
        {
            enabled = config.ArchiveEnabled,
            cleanupEnabled = config.CleanupEnabled,
            cleanupDryRun = config.CleanupDryRun,
            cleanupGraceHours = config.CleanupGraceHours,
            cleanupGraceMinutes = config.GraceMinutes,
            intervalSeconds = config.ArchiveIntervalSeconds,
            targets = config.ArchiveTargets.Select(Describe).ToArray(),
            counts,
            recent,
            dbError,
        });
    }

    [HttpPost("api/admin/archive/test/{index:int}")]
    public async Task<IActionResult> Test(int index, CancellationToken ct)
    {
        await RequireAdminAsync(ct);
        if (index < 0 || index >= config.ArchiveTargets.Length) throw new HttpApiException(404, "No such SharePoint target");
        var t = config.ArchiveTargets[index];
        if (!t.IsUsable)
            return Ok(new
            {
                ok = false,
                steps = new[] { new ArchiveTestStep("Configuration", false,
                    "Missing: " + string.Join(", ", Missing(t))
                    + (t.ClientSecret == "" ? $" - set it as environment variable / user-secret Archive__Targets__{index}__ClientSecret" : "")) },
            });
        var steps = await graph.TestAsync(t, ct);
        return Ok(new { ok = steps.All(s => s.Ok), steps });
    }
}

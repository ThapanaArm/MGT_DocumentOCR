using MgtOcr.Core.Config;
using MgtOcr.Data;

namespace MgtOcr.Api.Services;

// Step 3 of the upload lifecycle: removes local files that are no longer needed.
//   1. archived   - copy exists in SharePoint (ocr.FileArchive DONE) and the grace period has passed
//   2. drafts     - never posted, untouched for CleanupDraftDays        (0 = off)
//   3. failed OCR - job failed, no document, older than CleanupFailedDays (0 = off)
//   4. orphans    - file in the uploads folder that nothing in the DB refers to, older than CleanupOrphanDays (0 = off)
// Files are only ever deleted from inside UploadDir. With Archive:CleanupDryRun=true (the default)
// it only logs what it WOULD delete. DB rows are never deleted - the DocumentFile endpoint falls
// back to SharePoint for archived files, and answers 404 for the rest.
public class FileCleanupWorker(AppConfig config, FileArchiveRepository repo, ILogger<FileCleanupWorker> log)
    : BackgroundService
{
    private const int Batch = 500;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.CleanupEnabled) { log.LogInformation("File cleanup is disabled (Archive:CleanupEnabled=false)"); return; }
        log.LogInformation("File cleanup enabled (DryRun={DryRun}, every {Min} min, modules {Modules})", config.CleanupDryRun,
            config.CleanupIntervalMinutes, string.Join(",", config.CleanupModules));

        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        var outage = new DbOutage("File cleanup", log);
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); outage.Succeeded(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { outage.Failed(e, "File cleanup run"); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, config.CleanupIntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (config.CleanupModules.Length == 0) { log.LogInformation("File cleanup: Archive:CleanupModules is empty - nothing is deleted"); return; }
        var freed = 0L; var count = 0;

        // Archived files past the grace period. Counted per outcome so a run that deletes nothing still
        // says WHY in the log (before, "nothing happened" and "every file belonged to the other
        // installation" looked the same: silence).
        var archived = (await repo.FindArchivedToDeleteAsync(config.GraceMinutes, Batch, config.CleanupModules, ct)).ToList();
        int otherInstall = 0, alreadyGone = 0, archivedDeleted = 0;
        var me = Environment.MachineName;
        foreach (var a in archived)
        {
            var c = new CleanupCandidate(a.StoredPath, "archived to SharePoint");
            // A file stored by another installation sharing this database (dev PC vs server) is not
            // ours to delete or to mark - leave it for the backend whose uploads folder holds it.
            var full = SafeFull(c.StoredPath);
            if (full is null) { otherInstall++; continue; }
            var ours = string.Equals(a.MachineName, me, StringComparison.OrdinalIgnoreCase);
            var missing = !File.Exists(full);
            if (missing) alreadyGone++;
            var (deleted, bytes) = Delete(c);
            if (deleted) { count++; archivedDeleted++; freed += bytes; }
            if (config.CleanupDryRun) continue;
            // Mark LocalDeletedAt only when WE deleted it, or the row is ours and the file is really
            // gone. Before sql/32 (MachineName NULL) a missing file proves nothing: the dev PC and the
            // server can have the same uploads path, and the "missing" one used to mark the row so
            // the machine actually holding the file never deleted it.
            if ((deleted && !File.Exists(full)) || (missing && ours))
                await repo.MarkLocalDeletedAsync(c.StoredPath);
        }
        if (archived.Count > 0)
            log.LogInformation(
                "File cleanup: {N} archived file(s) past the {M} min grace period - deleted {Del}, already gone {Gone}, " +
                "belong to another installation (not under {Dir}) {Other}{Dry}",
                archived.Count, config.GraceMinutes, archivedDeleted, alreadyGone, config.UploadDir, otherInstall,
                config.CleanupDryRun ? " [DRY RUN - nothing actually deleted]" : "");
        else
            log.LogInformation("File cleanup: no archived file is past the {M} min grace period yet", config.GraceMinutes);

        // Never-posted drafts: CleanupDraftMinutes / CleanupDraftHours (tests) win over CleanupDraftDays.
        var draftMinutes = config.DraftMinutes;
        if (draftMinutes > 0)
        {
            var drafts = 0;
            foreach (var c in await repo.FindStaleDraftsAsync(draftMinutes, Batch, config.CleanupModules, ct))
            {
                var (d, b) = Delete(c);
                if (!d) continue;
                count++; drafts++; freed += b;
                // Tell the UI the file is gone for good ("file expired" badge/banner).
                if (!config.CleanupDryRun) await repo.MarkFileExpiredAsync(c.StoredPath, ct);
            }
            log.LogInformation("File cleanup: {N} unposted draft file(s) idle > {M} min removed{Dry}", drafts, draftMinutes,
                config.CleanupDryRun ? " [DRY RUN]" : "");
        }

        if (config.CleanupFailedDays > 0)
            foreach (var c in await repo.FindFailedJobFilesAsync(config.CleanupFailedDays, Batch, config.CleanupModules, ct))
            { var (d, b) = Delete(c); if (d) { count++; freed += b; } }

        // An orphan has no document, so its module is unknown: only sweep when every module is cleaned.
        var allModules = new[] { "SO", "AP", "II", "PODP" }.All(config.CleansModule);
        if (config.CleanupOrphanDays > 0 && !allModules)
            log.LogInformation("File cleanup: orphan sweep skipped (Archive:CleanupModules={Modules} does not cover every module)",
                string.Join(",", config.CleanupModules));
        if (config.CleanupOrphanDays > 0 && allModules)
        {
            var cutoff = DateTime.Now.AddDays(-config.CleanupOrphanDays);
            foreach (var f in Directory.EnumerateFiles(config.UploadDir, "*", SearchOption.AllDirectories).Take(20000))
            {
                if (ct.IsCancellationRequested) break;
                var fi = new FileInfo(f);
                if (fi.LastWriteTime > cutoff) continue;
                if (!(await repo.IsUnreferencedAsync(f) && await repo.IsUnreferencedAsync(Path.GetRelativePath(config.UploadDir, f)))) continue;
                var (d, b) = Delete(new CleanupCandidate(f, $"orphan > {config.CleanupOrphanDays} days"));
                if (d) { count++; freed += b; }
            }
        }

        if (count > 0)
            log.LogInformation("File cleanup {Mode}: {Count} file(s), {MB:F1} MB", config.CleanupDryRun ? "(dry run) would delete" : "deleted", count, freed / 1048576.0);
    }

    // Resolves the path and refuses anything outside the uploads folder.
    private string? SafeFull(string path)
    {
        try
        {
            var full = Path.GetFullPath(path, config.UploadDir);
            var root = Path.GetFullPath(config.UploadDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private (bool Deleted, long Bytes) Delete(CleanupCandidate c)
    {
        var full = SafeFull(c.StoredPath);
        if (full is null) { log.LogWarning("Cleanup skipped (outside uploads dir): {Path}", c.StoredPath); return (false, 0); }
        if (!File.Exists(full)) return (false, 0);
        long size = 0;
        try
        {
            size = new FileInfo(full).Length;
            if (config.CleanupDryRun) { log.LogInformation("[dry-run] would delete {Path} ({Reason})", full, c.Reason); return (true, size); }
            File.Delete(full);
            log.LogInformation("Deleted {Path} ({Reason})", full, c.Reason);
            return (true, size);
        }
        catch (Exception e) { log.LogWarning("Could not delete {Path}: {Message}", full, e.Message); return (false, 0); }
    }
}

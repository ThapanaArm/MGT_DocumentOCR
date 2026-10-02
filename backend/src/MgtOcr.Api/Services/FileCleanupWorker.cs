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
        log.LogInformation("File cleanup enabled (DryRun={DryRun}, every {Min} min)", config.CleanupDryRun, config.CleanupIntervalMinutes);

        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "File cleanup run failed"); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, config.CleanupIntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var freed = 0L; var count = 0;

        foreach (var c in await repo.FindArchivedToDeleteAsync(Math.Max(0, config.CleanupGraceHours), Batch, ct))
        {
            // A file stored by another installation sharing this database (dev PC vs server) is not
            // ours to delete or to mark - leave it for the backend whose uploads folder holds it.
            var full = SafeFull(c.StoredPath);
            if (full is null) continue;
            var (deleted, bytes) = Delete(c);
            if (deleted) { count++; freed += bytes; }
            // Mark even when the file was already gone, so the row stops being re-selected.
            if (!config.CleanupDryRun && !File.Exists(full)) await repo.MarkLocalDeletedAsync(c.StoredPath);
        }

        if (config.CleanupDraftDays > 0)
            foreach (var c in await repo.FindStaleDraftsAsync(config.CleanupDraftDays, Batch, ct))
            { var (d, b) = Delete(c); if (d) { count++; freed += b; } }

        if (config.CleanupFailedDays > 0)
            foreach (var c in await repo.FindFailedJobFilesAsync(config.CleanupFailedDays, Batch, ct))
            { var (d, b) = Delete(c); if (d) { count++; freed += b; } }

        if (config.CleanupOrphanDays > 0)
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

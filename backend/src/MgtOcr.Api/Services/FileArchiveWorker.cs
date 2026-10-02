using MgtOcr.Core.Config;
using MgtOcr.Data;

namespace MgtOcr.Api.Services;

// Step 2 of the upload lifecycle: once a document has been posted to SAP/Zoho (Status='POSTED',
// CompanyCode stamped) its source file is copied to the company's SharePoint library and recorded
// in ocr.FileArchive. The local copy is NOT touched here - FileCleanupWorker removes it later,
// after a grace period. Polling (rather than hooking the post) keeps PostDocument untouched, so a
// SharePoint outage can never affect posting. Disabled unless Archive:Enabled=true.
public class FileArchiveWorker(AppConfig config, FileArchiveRepository repo, GraphArchiveClient graph,
    CompressionState compression, ILogger<FileArchiveWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.ArchiveEnabled) { log.LogInformation("File archive is disabled (Archive:Enabled=false)"); return; }
        if (!config.ArchiveTargets.Any(t => t.IsUsable))
        {
            log.LogWarning("Archive:Enabled=true but no usable SharePoint target (Archive:Targets) - archiving will not run");
            return;
        }

        // When compression is on, upload only files it has already processed, so SharePoint always
        // gets the compressed copy.
        _waitForCompression = await compression.WhenReady.WaitAsync(ct);
        log.LogInformation("File archive enabled (waits for compression: {Wait})", _waitForCompression);

        var idle = TimeSpan.FromSeconds(Math.Max(10, config.ArchiveIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            var worked = 0;
            try { worked = await RunBatchAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError(e, "File archive batch failed"); }

            try { await Task.Delay(worked > 0 ? TimeSpan.FromSeconds(2) : idle, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private bool _waitForCompression;

    private async Task<int> RunBatchAsync(CancellationToken ct)
    {
        // Only fetch what some target covers ("*" on either axis = no filter on that axis).
        var usable = config.ArchiveTargets.Where(t => t.IsUsable).ToList();
        string[]? companies = usable.Any(t => t.Company.Trim() == "*") ? null : usable.Select(t => t.Company.Trim()).Distinct().ToArray();
        string[]? modules = usable.Any(t => t.Module.Trim() == "*") ? null : usable.Select(t => t.Module.Trim()).Distinct().ToArray();
        var list = (await repo.FindToArchiveAsync(Math.Max(1, config.ArchiveBatchSize), Math.Max(1, config.ArchiveMaxAttempts),
            companies, modules, config.UploadDir, _waitForCompression, ct)).ToList();
        foreach (var c in list)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var target = graph.TargetFor(c.CompanyCode, c.Module);
                if (target == null) continue;   // e.g. GLC while only MGT is configured - not an error, just not archived yet
                var local = Path.GetFullPath(c.StoredPath);
                if (!File.Exists(local)) { await repo.MarkMissingAsync(c); continue; }   // permanent - not retried
                var remote = GraphArchiveClient.BuildRemotePath(target, c.CompanyCode, c.Module, c.PostedAt, local, c.SubPath);
                var r = await graph.UploadAsync(target, local, remote, ct);
                await repo.MarkDoneAsync(c, r.ItemId, r.RemotePath, r.WebUrl);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                log.LogWarning("Archive failed for {File} ({Company}): {Message}", c.StoredPath, c.CompanyCode, e.Message);
                try { await repo.MarkFailedAsync(c, e.Message); } catch { /* best effort */ }
            }
        }
        return list.Count;
    }
}

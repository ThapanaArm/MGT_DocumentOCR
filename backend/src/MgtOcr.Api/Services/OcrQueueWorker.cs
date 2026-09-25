using MgtOcr.Data;

namespace MgtOcr.Api.Services;

// Background queue worker for batch OCR. Polls ocr.OcrJob for QUEUED rows and reads up to
// MaxConcurrent at a time (kept low to respect the OCR provider's rate limits and cost), marking
// each DONE (with the created DocId) or FAILED. Runs for the life of the app process.
public class OcrQueueWorker(OcrJobRepository jobs, DocumentIngestService ingest, ILogger<OcrQueueWorker> log)
    : BackgroundService
{
    private const int MaxConcurrent = 3;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var reset = await jobs.ResetOrphansAsync(ct);
            if (reset > 0) log.LogInformation("Requeued {Count} orphaned OCR job(s) left PROCESSING from a previous run", reset);
        }
        catch (Exception e) { log.LogError(e, "Failed to reset orphaned OCR jobs on startup"); }

        var running = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);

            OcrJobClaim? job = null;
            if (running.Count < MaxConcurrent)
            {
                try { job = await jobs.ClaimNextAsync(ct); }
                catch (Exception e) { log.LogError(e, "Failed to claim next OCR job"); }
            }
            if (job is not null) { running.Add(ProcessAsync(job, ct)); continue; }

            try
            {
                if (running.Count == 0) await Task.Delay(1500, ct);              // idle — poll every 1.5s
                else await Task.WhenAny(Task.WhenAny(running), Task.Delay(500, ct)); // busy & full — wait for a slot
            }
            catch (OperationCanceledException) { break; }
        }

        try { await Task.WhenAll(running); } catch { /* app shutting down */ }
    }

    private async Task ProcessAsync(OcrJobClaim job, CancellationToken ct)
    {
        try
        {
            var (docId, mod, _) = await ingest.IngestAsync(
                job.Module, job.StoredPath, job.FileName, job.FileSize, job.Engine, job.ApDocCategory, job.CreatedBy);
            await jobs.MarkDoneAsync(job.JobId, docId);
            log.LogInformation("OCR job {JobId} ({File}) -> doc {DocId} ({Module})", job.JobId, job.FileName, docId, mod);
        }
        catch (Exception e)
        {
            log.LogError(e, "OCR job {JobId} ({File}) failed", job.JobId, job.FileName);
            try { await jobs.MarkFailedAsync(job.JobId, e.Message); } catch { /* best effort */ }
        }
    }
}

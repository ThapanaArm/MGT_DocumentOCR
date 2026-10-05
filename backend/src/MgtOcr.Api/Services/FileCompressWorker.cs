using MgtOcr.Core.Config;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

// Shrinks stored upload files IN PLACE (same path and extension, so nothing else changes) right
// after OCR has read the full-quality original - a file is picked up as soon as its document row
// exists. Uses MgtOcr.Ocr.FileCompressor (PDFtoImage + SkiaSharp, no external program).
// The smaller copy replaces the original only if it is valid and at least CompressMinSavingPercent
// smaller; otherwise the original is kept. Every processed file gets a row in ocr.FileCompress
// (sql/28_file_compress.sql), and FileArchiveWorker waits for that row before uploading.
// Off unless Archive:CompressEnabled=true.
public class FileCompressWorker(AppConfig config, FileArchiveRepository repo, CompressionState state,
    ILogger<FileCompressWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.CompressEnabled)
        {
            state.Set(false);
            log.LogInformation("File compression is disabled (Archive:CompressEnabled=false)");
            return;
        }
        state.Set(true);
        log.LogInformation("File compression enabled ({Dpi} dpi, JPEG quality {Q}, images max {Px}px)",
            config.CompressDpi, config.CompressQuality, config.CompressImageMaxPx);

        var outage = new DbOutage("File compression", log);
        while (!ct.IsCancellationRequested)
        {
            var n = 0;
            var backoff = TimeSpan.Zero;
            try
            {
                foreach (var stored in await repo.FindToCompressAsync(Math.Max(0, config.CompressMinAgeMinutes), 10, config.UploadDir, ct))
                {
                    if (ct.IsCancellationRequested) break;
                    n++;
                    await Task.Run(() => ProcessAsync(stored), ct);
                }
                outage.Succeeded();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { backoff = outage.Failed(e, "File compression batch"); }

            var wait = backoff > TimeSpan.Zero ? backoff : n > 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10);
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessAsync(string stored)
    {
        string? tmp = null;
        try
        {
            var full = Path.GetFullPath(stored);
            var fi = new FileInfo(full);
            if (!fi.Exists) { await repo.MarkCompressedAsync(stored, "SKIPPED", 0, 0, "file missing"); return; }
            var before = fi.Length;
            if (before < config.CompressMinBytes) { await repo.MarkCompressedAsync(stored, "SKIPPED", before, before, "below min size"); return; }

            tmp = full + ".cmp.tmp" + fi.Extension;
            if (File.Exists(tmp)) File.Delete(tmp);
            var kind = FileCompressor.Compress(full, tmp, config.CompressDpi, config.CompressQuality, config.CompressImageMaxPx);
            if (kind is null) { await repo.MarkCompressedAsync(stored, "SKIPPED", before, before, $"type {fi.Extension} not handled"); return; }

            var after = File.Exists(tmp) ? new FileInfo(tmp).Length : 0;
            var saving = before > 0 ? (before - after) * 100.0 / before : 0;
            if (after <= 0) { await repo.MarkCompressedAsync(stored, "FAILED", before, 0, "empty output"); return; }
            if (saving < config.CompressMinSavingPercent)
            { await repo.MarkCompressedAsync(stored, "SKIPPED", before, after, $"saving only {saving:F0}%"); return; }

            File.Move(tmp, full, overwrite: true);   // fails while someone has the file open -> retried next round
            tmp = null;
            await repo.MarkCompressedAsync(stored, "DONE", before, after, kind);
            log.LogInformation("Compressed {File}: {Before:N0} -> {After:N0} bytes (-{Saving:F0}%)", fi.Name, before, after, saving);
        }
        catch (IOException e)
        {
            log.LogInformation("Compress deferred for {File}: {Message}", stored, e.Message);   // in use; next round
        }
        catch (Exception e)
        {
            log.LogWarning("Compress failed for {File}: {Message} (original kept)", stored, e.Message);
            try { await repo.MarkCompressedAsync(stored, "FAILED", 0, 0, e.Message); } catch { }
        }
        finally { if (tmp is not null) try { File.Delete(tmp); } catch { } }
    }
}

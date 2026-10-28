using MgtOcr.Core.Config;
using MgtOcr.Data;
using MgtOcr.Ocr;
using MgtOcr.Ocr.Providers;

namespace MgtOcr.Api.Services;

// Page memory backfill: goes back over AP/II documents that were read before page memory existed
// and records what can be known about them (Megachem, 28 Oct 2026: "update backwards").
//
// - Free and certain: the FORM SHIPPING EXPENSE / PURCHASE ORDER pages found through the PDF's text
//   layer, with a picture of their header. These become reference pictures straight away.
// - Optional and billed: Ocr:PageMemory:BackfillClassify = N also runs the page classifier on up to N
//   old documents. That only adds statistics (how often the classifier agrees with the text layer);
//   an AI pick on an old document is never used as a reference, because nobody confirmed it.
//
// Off unless Ocr:PageMemory:Backfill=true. Each document is looked at once: a document with nothing
// to record gets a placeholder row (PageNo 0) so a restart does not look at it again. Scanned files
// (no text layer) and files already shrunk by Archive:CompressEnabled (re-rendered as images, so the
// text layer is gone) yield no anchor here.
public class PageMemoryBackfillWorker(AppConfig config, Db db, PageMemoryStore store,
    ILogger<PageMemoryBackfillWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.OcrPageMemoryBackfill || config.OcrPageMemoryMode == "off") return;
        // Let the app finish starting (and the first requests through) before reading old files.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }

        var looked = new HashSet<int>();
        int seen = 0, anchored = 0, classified = 0, nothing = 0, noFile = 0, failed = 0;
        var classifyLeft = Math.Max(0, config.OcrPageMemoryBackfillClassify);
        log.LogInformation("Page memory backfill started (classify up to {N} document(s))", classifyLeft);

        while (!ct.IsCancellationRequested)
        {
            List<dynamic> batch;
            try
            {
                batch = (await db.QueryAsync("""
                    SELECT TOP (20) d.DocId, d.StoredPath, d.ApDocCategory
                    FROM ocr.Document d
                    WHERE d.Module IN ('AP','II')
                      AND d.StoredPath LIKE '%.pdf'
                      AND d.FileExpiredAt IS NULL
                      AND NOT EXISTS (SELECT 1 FROM ocr.DocPageMap m WHERE m.DocId = d.DocId)
                    ORDER BY CASE WHEN d.Status = 'POSTED' THEN 0 ELSE 1 END, d.DocId DESC
                    """, ct: ct)).ToList();
            }
            catch (Exception ex)
            {
                log.LogWarning("Page memory backfill stopped — could not list documents (has sql/35_page_memory.sql been run?): {Msg}", ex.Message);
                return;
            }
            if (batch.Count == 0) break;
            // A document comes back only if its row could not be written (database trouble) —
            // stop rather than go round the same batch forever.
            if (batch.All(r => looked.Contains((int)r.DocId)))
            {
                log.LogWarning("Page memory backfill stopped — results could not be saved (see [PageMemory] messages above)");
                break;
            }

            foreach (var r in batch)
            {
                if (ct.IsCancellationRequested) break;
                int docId = (int)r.DocId;
                if (!looked.Add(docId)) continue;
                string path = (string?)r.StoredPath ?? "";
                string category = (string?)r.ApDocCategory ?? "";
                seen++;
                try
                {
                    if (!File.Exists(path))
                    {
                        noFile++;
                        await store.SaveMarkerAsync(docId, "NO_FILE");
                        continue;
                    }
                    var classify = classifyLeft > 0;
                    var hint = classify ? await store.LoadHintAsync() : null;
                    var (map, _, _) = await Task.Run(
                        () => GeminiOcr.BackfillPageMapAsync(path, category, classify, hint, config), ct);
                    if (classify && map?.Any(p => p.AiRole != null) == true) { classifyLeft--; classified++; }
                    if (map is { Count: > 0 })
                    {
                        await store.SaveAsync(docId, new ParsedDocument { PageMap = map, PageMemoryMode = "backfill" });
                        if (map.Any(p => p.Method == "TEXT")) anchored++;
                    }
                    else
                    {
                        nothing++;
                        await store.SaveMarkerAsync(docId, "NO_ANCHOR");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failed++;
                    log.LogWarning("Page memory backfill: document {DocId} failed: {Msg}", docId, ex.Message);
                    await store.SaveMarkerAsync(docId, "FAILED");
                }
                // Rendering shares one PDFium lock with live uploads — leave room for them.
                try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
            }
        }
        log.LogInformation(
            "Page memory backfill finished: {Seen} document(s) — {Anchored} with an anchor page from the text layer, "
            + "{Classified} classified, {Nothing} with nothing to record, {NoFile} without a file, {Failed} failed",
            seen, anchored, classified, nothing, noFile, failed);
    }
}

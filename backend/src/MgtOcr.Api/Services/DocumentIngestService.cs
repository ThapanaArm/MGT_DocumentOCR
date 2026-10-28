using MgtOcr.Core;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

// The shared OCR->create pipeline, used by both the single-file upload endpoint and the batch
// queue worker. Mirrors DocumentsController.Upload's core: read the saved file, route AP->AP/II by
// PO number, then create the document row. Password prompting stays in the sync endpoint; the batch
// path rejects encrypted PDFs before enqueuing, so `password` is normally null here.
public class DocumentIngestService(OcrEngine ocr, DocumentRepository repo, JobLogRepository jobs, PageMemoryStore pageMemory, FileDuplicateStore duplicates, Db db, MgtOcr.Core.Config.AppConfig config)
{
    public async Task<(int DocId, string Module, string? Note)> IngestAsync(
        string module, string storedPath, string fileName, int size, string engine,
        string apDocCategory, string user, string? password = null, string? salesOrg = null)
    {
        var mod = module.ToUpperInvariant();
        var detect = mod == "AP";
        // Locked to Gemini for the auto/empty path (per Megachem), same as the sync upload.
        var uploadEngine = "gemini"; // Gemini only (Megachem, 7 Oct 2026), whatever engine was requested
        var t0 = DateTime.UtcNow;
        // ocr.JobLog: the clock starts BEFORE the call, so a read that fails or times out still
        // leaves a row saying when it began and how long it ran. OcrDurationMs on the document
        // only ever records a read that finished.
        var jobNumber = await jobs.NextJobNumberAsync();
        var job = await jobs.StartAsync(jobNumber, "OCR", module: mod, provider: uploadEngine,
            fileName: fileName, fileSize: size, performedBy: user);
        // Same file uploaded before? (AP/II, FileDuplicateStore.) A checked earlier document of the
        // same type is copied instead of read; an earlier post is warned about either way.
        string? fileHash = null;
        FileDuplicateStore.Match? dup = null;
        if (mod is "AP" or "II")
        {
            try { fileHash = FileDuplicateStore.HashFile(storedPath); }
            catch (Exception ex) { Console.Error.WriteLine($"[FileDuplicate] could not hash {fileName}: {ex.Message}"); }
            if (fileHash != null) dup = await duplicates.FindAsync(fileHash, size, apDocCategory, salesOrg);
        }
        var dupWarning = dup?.PostedDocId is int postedId
            ? $"Warning: this same file is already {(dup.PostedStatus == "SPLIT" ? "split into documents" : "posted")} as document #{postedId}"
              + (string.IsNullOrWhiteSpace(dup.PostedSapDocNo) ? "" : $" (SAP document {dup.PostedSapDocNo})")
              + " - check that this is not the same liability recorded twice before posting"
            : "";
        if (dup?.CopyFromDocId is int copyFrom)
        {
            try
            {
                var copied = await CopyAsync(copyFrom, fileName, storedPath, size, user, apDocCategory, salesOrg, dupWarning, t0);
                await duplicates.SaveAsync(copied.DocId, fileHash!, copyFrom);
                await StampReadStartAsync(copied.DocId, mod, t0);
                await jobs.FinishAsync(job, "SUCCESS", docId: copied.DocId, provider: "copy",
                    message: $"Same file as document {copyFrom} - copied, not read again");
                return copied;
            }
            catch (Exception ex)
            {
                // Copying is a shortcut, never a reason to fail an upload: read it as usual.
                Console.Error.WriteLine($"[FileDuplicate] copy from document {copyFrom} failed, reading instead: {ex.Message}");
            }
        }
        // Page memory: reference pictures of anchor pages from posted bundles (AP/II only; nothing
        // is loaded when Ocr:PageMemory:Mode is "off").
        var pageHint = await pageMemory.LoadReadHintAsync(mod, apDocCategory);
        ParsedDocument pd;
        try
        {
            pd = await ocr.ExtractAsync(storedPath, mod, uploadEngine, password, salesOrg, apDocCategory, pageHint);
        }
        catch (Exception ex)
        {
            await jobs.FinishAsync(job, "FAILED", message: ex.Message);
            throw;
        }
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        // Expense is always an Incoming Invoice (FB60, no PO). Its bundles carry a FORM SHIPPING
        // EXPENSE sheet whose "PO. NO." (the goods PO the costs relate to) would otherwise be read
        // as poRef and wrongly route the document to Supplier Invoice (MIRO). Other categories
        // keep the PO-based routing. (Ported from Kanomwan's 0cd227c "Edit From ACC GLC".)
        if (detect)
        {
            var category = (apDocCategory ?? "").Trim();
            var isExpense = string.Equals(category, "EXPENSE", StringComparison.OrdinalIgnoreCase);
            // Inventory (Purchase Order) says so in its name: it is keyed against a purchase order,
            // so it is a Supplier Invoice (MIRO) whether or not the read found the number.
            var isInventoryPo = string.Equals(category, "INVENTORY_PO", StringComparison.OrdinalIgnoreCase);

            // The PO is often not printed on the supplier's invoice at all — it rides along as our
            // own PURCHASE ORDER page behind it, which a read centred on page 1 misses. Before
            // deciding there is no PO, look for one in the file's text layer. Skipped for Expense,
            // where the form sheet's "PO. NO." refers to the goods PO and must not route the
            // document to MIRO.
            if (!isExpense && pd.Header.GetStr("poRef").Trim().Length == 0)
            {
                var found = Path.GetExtension(storedPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? PoNumberFinder.Find(PdfExtraction.PdfText(storedPath))
                    : "";
                // Purchasing saves these scans as "A-MatDoc_<material document>_<PO>.pdf", so the
                // name itself carries the PO when the page it is printed on will not read.
                if (found.Length == 0) found = PoNumberFinder.FindInFileName(fileName);
                if (found.Length > 0) pd.Header["poRef"] = found;
            }

            mod = isExpense ? "II"
                : isInventoryPo ? "AP"
                : pd.Header.GetStr("poRef").Trim().Length > 0 ? "AP" : "II";
        }
        if (dup?.AnyDocId is int sameFile)
        {
            // Read again (the earlier copy was not checked yet, or was filed as another type) — say so.
            var seen = $"Same file as document #{sameFile}";
            pd.ConfidenceNote = string.Join(" / ", new[] { pd.ConfidenceNote, seen, dupWarning }.Where(x => !string.IsNullOrEmpty(x)));
        }
        var ext = ExtConversion.ToExtDict(pd);
        var docId = await repo.CreateDocumentAsync(mod, ext, fileName, storedPath, size, user, apDocCategory, durationMs, salesOrg);
        await pageMemory.SaveReadAsync(docId, mod, apDocCategory, pd);
        if (fileHash != null) await duplicates.SaveAsync(docId, fileHash, dup?.AnyDocId);
        await StampReadStartAsync(docId, mod, t0);
        // DocId is only known now, after the row exists — so it is stamped on the job at the end.
        // The module can change too: AP/II routing above is decided by what the read found.
        await jobs.FinishAsync(job, "SUCCESS", docId: docId, tokensIn: pd.TokensIn, tokensOut: pd.TokensOut,
            cost: pd.Cost, costCurrency: pd.CostCurrency, provider: pd.Provider);
        var note = dupWarning.Length > 0
            ? (string.IsNullOrEmpty(pd.Note) ? dupWarning : $"{pd.Note} / {dupWarning}")
            : pd.Note;
        return (docId, mod, note);
    }

    /// <summary>ocr.Document.OcrStartedAt (sql/37_read_timing.sql): when this document's read began,
    /// so ReadToPostMinutes (read start → PostedAt) can be computed by the database. Written on the
    /// SQL server's own clock — PostedAt is SYSDATETIME() there, and the app server's clock or time
    /// zone may differ — by going back the time that has passed since the read started. Kept on a
    /// re-read: the measure runs from the FIRST read. Sales Orders live in another table; skipped.
    /// Never fails the upload (column missing → logged).</summary>
    private async Task StampReadStartAsync(int docId, string module, DateTime startedUtc)
    {
        if (module == "SO") return;
        try
        {
            var elapsedMs = (int)Math.Max(0, (DateTime.UtcNow - startedUtc).TotalMilliseconds);
            await db.ExecuteAsync(
                "UPDATE ocr.Document SET OcrStartedAt = DATEADD(MILLISECOND, -@elapsedMs, SYSDATETIME()) WHERE DocId = @docId AND OcrStartedAt IS NULL",
                new { elapsedMs, docId });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReadTiming] could not stamp the read start of document {docId} (has sql/37_read_timing.sql been run?): {ex.Message}");
        }
    }

    /// <summary>Creates the new document from an earlier, checked document of the same file: its
    /// saved header and lines — with every correction and line mapping made on it — instead of a
    /// new read. The document type follows the earlier one (it was already routed AP / II), the
    /// posting date is today's like any upload, and the new file is the one kept.</summary>
    private async Task<(int DocId, string Module, string? Note)> CopyAsync(int fromDocId, string fileName,
        string storedPath, int size, string user, string apDocCategory, string? salesOrg, string dupWarning, DateTime t0)
    {
        var prev = await repo.GetDocumentAsync(fromDocId);
        var module = prev.GetStr("module");
        if (module is not ("AP" or "II")) throw new InvalidOperationException($"document {fromDocId} is module {module}");
        var copiedNote = $"Same file as document #{fromDocId} - its checked values (including any corrections made there) were copied; the file was not read again";
        // Never copy an empty shell: if the saved document does not come back in the expected shape,
        // the caller falls back to reading the file.
        var header = prev.Get("header") as Dictionary<string, object?>
            ?? throw new InvalidOperationException($"document {fromDocId} has no header to copy");
        var lines = prev.Get("lines") as List<Dictionary<string, object?>>
            ?? throw new InvalidOperationException($"document {fromDocId} has no lines to copy");
        var ext = new Dictionary<string, object?>
        {
            ["header"] = header,
            ["lines"] = lines,
            ["provider"] = "copy",
            ["confidence"] = prev.Get("confidence"),
            ["confidenceNote"] = dupWarning.Length > 0 ? $"{copiedNote} / {dupWarning}" : copiedNote,
            ["rawText"] = "",
        };
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        var docId = await repo.CreateDocumentAsync(module, ext, fileName, storedPath, size, user, apDocCategory, durationMs, salesOrg);
        var note = dupWarning.Length > 0 ? $"{copiedNote} / {dupWarning}" : copiedNote;
        return (docId, module, note);
    }
}

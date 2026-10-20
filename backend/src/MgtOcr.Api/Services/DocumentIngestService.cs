using MgtOcr.Core;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

// The shared OCR->create pipeline, used by both the single-file upload endpoint and the batch
// queue worker. Mirrors DocumentsController.Upload's core: read the saved file, route AP->AP/II by
// PO number, then create the document row. Password prompting stays in the sync endpoint; the batch
// path rejects encrypted PDFs before enqueuing, so `password` is normally null here.
public class DocumentIngestService(OcrEngine ocr, DocumentRepository repo, JobLogRepository jobs)
{
    public async Task<(int DocId, string Module, string? Note)> IngestAsync(
        string module, string storedPath, string fileName, int size, string engine,
        string apDocCategory, string user, string? password = null, string? salesOrg = null,
        DateTime? jobStartedAt = null)
    {
        // Job Tasktime starts when the upload started (passed in by the caller); fall back to now.
        var jobStart = jobStartedAt ?? DateTime.Now;
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
        ParsedDocument pd;
        try
        {
            pd = await ocr.ExtractAsync(storedPath, mod, uploadEngine, password, salesOrg, apDocCategory);
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
        var ext = ExtConversion.ToExtDict(pd);
        // Keeps BOTH job clocks (merge, 20 Oct 2026). They answer different questions and write to
        // different places: jobStartedAt stamps when the upload began on the document row itself,
        // while ocr.JobLog records each step of the run with its own tokens and cost. Dropping
        // either one would quietly remove a column somebody's report already reads.
        var docId = await repo.CreateDocumentAsync(mod, ext, fileName, storedPath, size, user, apDocCategory, durationMs, salesOrg,
            jobStartedAt: jobStart);
        // DocId is only known now, after the row exists — so it is stamped on the job at the end.
        // The module can change too: AP/II routing above is decided by what the read found.
        await jobs.FinishAsync(job, "SUCCESS", docId: docId, tokensIn: pd.TokensIn, tokensOut: pd.TokensOut,
            cost: pd.Cost, costCurrency: pd.CostCurrency, provider: pd.Provider);
        return (docId, mod, pd.Note);
    }
}

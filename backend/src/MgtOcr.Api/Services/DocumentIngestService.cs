using MgtOcr.Core;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

// The shared OCR->create pipeline, used by both the single-file upload endpoint and the batch
// queue worker. Mirrors DocumentsController.Upload's core: read the saved file, route AP->AP/II by
// PO number, then create the document row. Password prompting stays in the sync endpoint; the batch
// path rejects encrypted PDFs before enqueuing, so `password` is normally null here.
public class DocumentIngestService(OcrEngine ocr, DocumentRepository repo)
{
    public async Task<(int DocId, string Module, string? Note)> IngestAsync(
        string module, string storedPath, string fileName, int size, string engine,
        string apDocCategory, string user, string? password = null)
    {
        var mod = module.ToUpperInvariant();
        var detect = mod == "AP";
        // Locked to Gemini for the auto/empty path (per Megachem), same as the sync upload.
        var uploadEngine = string.IsNullOrEmpty(engine) || engine == "auto" ? "gemini" : engine;
        var t0 = DateTime.UtcNow;
        var pd = await ocr.ExtractAsync(storedPath, mod, uploadEngine, password);
        var durationMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
        // Expense is always an Incoming Invoice (FB60, no PO). Its bundles carry a FORM SHIPPING
        // EXPENSE sheet whose "PO. NO." (the goods PO the costs relate to) would otherwise be read
        // as poRef and wrongly route the document to Supplier Invoice (MIRO). Other categories
        // keep the PO-based routing. (Ported from Kanomwan's 0cd227c "Edit From ACC GLC".)
        if (detect)
            mod = string.Equals((apDocCategory ?? "").Trim(), "EXPENSE", StringComparison.OrdinalIgnoreCase)
                ? "II"
                : pd.Header.GetStr("poRef").Trim().Length > 0 ? "AP" : "II";
        var ext = ExtConversion.ToExtDict(pd);
        var docId = await repo.CreateDocumentAsync(mod, ext, fileName, storedPath, size, user, apDocCategory, durationMs);
        return (docId, mod, pd.Note);
    }
}

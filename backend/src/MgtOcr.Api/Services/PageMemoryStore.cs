using MgtOcr.Core.Config;
using MgtOcr.Data;
using MgtOcr.Ocr;

namespace MgtOcr.Api.Services;

// Page memory storage (sql/35_page_memory.sql). Lives in the API project because it moves
// MgtOcr.Ocr types in and out of the database, and MgtOcr.Data does not reference MgtOcr.Ocr.
// One row per page of an AP/II bundle: what the page is, whether it was read, and — for anchor
// pages only — a small picture of its header. The anchor pictures of documents that reached POSTED are the "memory": they are handed to the page
// classifier as references for the next bundle.
//
// Everything here is best-effort. Page memory must never fail an upload or a re-read: until the
// SQL script is run (or if the database blips) these calls log and return nothing.
public class PageMemoryStore(Db db, AppConfig config)
{
    /// <summary>Everything handed to the read for an AP/II document: page-memory reference pictures
    /// (Ocr:PageMemory:Mode shadow/on) and the positions recorded per vendor for this document
    /// type (Ocr:PageMemory:PositionHints). Null when neither is switched on.</summary>
    public async Task<PageMemoryHint?> LoadReadHintAsync(string module, string? apDocCategory)
    {
        if (module is not ("AP" or "II")) return null;
        PageMemoryHint? hint = config.OcrPageMemoryMode is "shadow" or "on" ? await LoadHintAsync() : null;
        if (config.OcrPositionHints)
        {
            hint ??= new PageMemoryHint();
            hint.Positions = await LoadPositionsAsync(apDocCategory);
        }
        return hint;
    }

    /// <summary>Everything recorded after a read: the page map, and the read measurements
    /// (thinking tokens, whether position hints were used, the positions it reported).</summary>
    public async Task SaveReadAsync(int docId, string module, string? apDocCategory, ParsedDocument pd)
    {
        if (module is not ("AP" or "II")) return;
        await SaveAsync(docId, pd);
        try
        {
            await db.ExecuteAsync("UPDATE ocr.Document SET OcrThinkingTokens=@thinking, OcrHint=@hint WHERE DocId=@docId",
                new { thinking = pd.ThinkingTokens, hint = pd.PositionHintUsed ? "positions" : null, docId });
            if (!string.IsNullOrWhiteSpace(pd.PositionsJson))
                await db.ExecuteAsync("""
                    INSERT ocr.FieldPositionMemory(DocId, ApDocCategory, VendorTaxId, PositionsJson)
                    VALUES(@docId, @category, @vendorTaxId, @json)
                    """, new
                {
                    docId,
                    category = string.IsNullOrWhiteSpace(apDocCategory) ? null : apDocCategory.Trim().ToUpperInvariant(),
                    vendorTaxId = pd.Header.TryGetValue("vendorTaxId", out var t) ? Convert.ToString(t) : null,
                    json = pd.PositionsJson,
                });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReadHints] could not save read measurements of document {docId} (has sql/38_read_hints.sql been run?): {ex.Message}");
        }
    }

    /// <summary>Position hints: for this document type, the newest positions of each vendor (by the
    /// vendor tax ID Gemini read), newest 3 vendors — one line per vendor, so Gemini uses the layout of
    /// the vendor on the document in front of it and ignores the others. Null when nothing is recorded.</summary>
    private async Task<string?> LoadPositionsAsync(string? apDocCategory)
    {
        try
        {
            var rows = (await db.QueryAsync("""
                SELECT TOP 3 f.VendorTaxId, f.PositionsJson
                FROM (SELECT Id, VendorTaxId, PositionsJson,
                             ROW_NUMBER() OVER (PARTITION BY ISNULL(VendorTaxId, '') ORDER BY Id DESC) AS rn
                      FROM ocr.FieldPositionMemory
                      WHERE ISNULL(ApDocCategory, '') = @category) f
                WHERE f.rn = 1
                ORDER BY f.Id DESC
                """, new { category = (apDocCategory ?? "").Trim().ToUpperInvariant() })).ToList();
            if (rows.Count == 0) return null;
            return string.Join("\n", rows.Select(r =>
            {
                var tax = ((string?)r.VendorTaxId ?? "").Trim();
                return $"- ผู้ขายเลขประจำตัวผู้เสียภาษี {(tax.Length > 0 ? tax : "(อ่านไม่ได้)")}: {(string)r.PositionsJson}";
            }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReadHints] could not load positions: {ex.Message}");
            return null;
        }
    }

    /// <summary>Reference pictures for the classifier: the most recent anchor header strips.
    /// - Found through the text layer (Method TEXT): the regex read the title "FORM SHIPPING EXPENSE"
    ///   / "PURCHASE ORDER" off the page itself, so it is certain — used right after the upload, no
    ///   need to wait for the post (Megachem, 28 Oct 2026: start learning from the upload).
    /// - Picked by the AI: only once a person has confirmed it by posting a document that was read
    ///   that way (mode "on" and the page was used). An AI pick nobody checked could be wrong, and a
    ///   wrong reference picture would teach every later read the same mistake.</summary>
    public async Task<PageMemoryHint?> LoadHintAsync(int perRole = 2)
    {
        try
        {
            var rows = await db.QueryAsync("""
                SELECT PageRole, HeaderJpeg FROM (
                    SELECT m.PageRole, m.HeaderJpeg,
                           ROW_NUMBER() OVER (PARTITION BY m.PageRole ORDER BY m.Id DESC) AS rn
                    FROM ocr.DocPageMap m
                    JOIN ocr.Document d ON d.DocId = m.DocId
                    WHERE m.HeaderJpeg IS NOT NULL
                      AND (m.Method = 'TEXT'
                           OR (d.Status = 'POSTED' AND m.Mode = 'on' AND m.UsedForRead = 1))
                ) x WHERE rn <= @perRole
                """, new { perRole });
            var hint = new PageMemoryHint();
            foreach (var r in rows)
                if (r.HeaderJpeg is byte[] img && img.Length > 0)
                    hint.References.Add(new PageReference((string)r.PageRole, img));
            return hint;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PageMemory] could not load references (has sql/35_page_memory.sql been run?): {ex.Message}");
            return null;
        }
    }

    /// <summary>A placeholder row (PageNo 0) for a document the backfill looked at but could not
    /// record anything for — no file, no anchor in the text layer, or an error — so the next run
    /// does not look at it again. Never used as a reference (no picture).</summary>
    public async Task SaveMarkerAsync(int docId, string reason)
    {
        try
        {
            await db.ExecuteAsync("""
                INSERT ocr.DocPageMap(DocId, PageNo, PageRole, Method, KeepForRead, UsedForRead, Mode)
                VALUES(@docId, 0, @reason, 'NONE', 1, 0, 'backfill')
                """, new { docId, reason });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PageMemory] could not mark document {docId}: {ex.Message}");
        }
    }

    /// <summary>Stores the page map of one read, replacing any earlier map of the same document
    /// (a re-read overwrites the first read's map).</summary>
    public async Task SaveAsync(int docId, ParsedDocument pd)
    {
        if (pd.PageMap is not { Count: > 0 } map) return;
        try
        {
            await db.ExecuteAsync("DELETE FROM ocr.DocPageMap WHERE DocId=@docId", new { docId });
            await db.ExecuteAsync("""
                INSERT ocr.DocPageMap(DocId, PageNo, PageRole, Confidence, Method, AiRole, AiConfidence,
                                      KeepForRead, UsedForRead, HeaderJpeg, Mode)
                VALUES(@DocId, @PageNo, @PageRole, @Confidence, @Method, @AiRole, @AiConfidence,
                       @KeepForRead, @UsedForRead, @HeaderJpeg, @Mode)
                """, map.Select(p => new
            {
                DocId = docId, p.PageNo, PageRole = p.Role, Confidence = (decimal)p.Confidence, p.Method,
                p.AiRole, AiConfidence = (decimal?)p.AiConfidence,
                KeepForRead = p.Keep, UsedForRead = p.Used, p.HeaderJpeg, Mode = pd.PageMemoryMode,
            }).ToList());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PageMemory] could not save the page map of document {docId}: {ex.Message}");
        }
    }
}

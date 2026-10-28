using System.Security.Cryptography;
using MgtOcr.Core.Config;
using MgtOcr.Data;

namespace MgtOcr.Api.Services;

// Same file uploaded again (sql/36_file_hash.sql, Megachem 28 Oct 2026).
//
// People re-upload a file instead of finding the document they already made — the data shows one
// purchase-order bundle uploaded fourteen times — and every time it was read by Gemini and mapped
// from scratch. Each upload's SHA-256 is stored; an upload whose bytes match an earlier AP/II
// document is recognised before anything is read:
// - the earlier document was checked (MAPPED / POSTED / PARTIAL), same document type, not a split
//   part → its saved values are copied, including every correction made on it, and Gemini is not
//   called at all. No button: the newest checked copy is always the one used, so correcting a
//   document is what "teaches" the next upload.
// - it was posted (or split) already → a warning goes on the new document: a second liability for
//   the same invoice is a double payment.
//
// TEST switch Ocr:Duplicate:CopyUnchecked=true: also copy from an earlier document nobody has
// checked yet (still NEW / INCOMPLETE), so the time saved can be measured on a test system where
// documents cannot be mapped. A failed read is never copied. Leave it false in production: an
// unchecked read may be wrong, and the whole point of re-uploading may be to read it again.
//
// Best-effort like page memory: until the SQL script is run, every call finds nothing.
public class FileDuplicateStore(Db db, AppConfig config)
{
    public record Match(int? CopyFromDocId, int? PostedDocId, string? PostedSapDocNo, string? PostedStatus, int? AnyDocId);

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Earlier AP/II documents with exactly this file. Null when there are none (or the
    /// FileHash column does not exist yet).</summary>
    public async Task<Match?> FindAsync(string hash, int size, string apDocCategory, string? salesOrg)
    {
        try
        {
            var rows = await QueryAsync(hash, salesOrg);
            if (rows.Count == 0 && await HashOlderFilesAsync(size) > 0)
                rows = await QueryAsync(hash, salesOrg);
            if (rows.Count == 0) return null;

            var category = (apDocCategory ?? "").Trim().ToUpperInvariant();
            var copy = rows.FirstOrDefault(r =>
                r.SourceDocId == null
                && (r.Status is "MAPPED" or "POSTED" or "PARTIAL"
                    || (config.DuplicateCopyUnchecked && r.Status is "NEW" or "INCOMPLETE"
                        && r.OcrProvider is not ("failed" or "demo")))
                && string.Equals((r.ApDocCategory ?? "").Trim(), category, StringComparison.OrdinalIgnoreCase));
            var posted = rows.FirstOrDefault(r => r.Status is "POSTED" or "PARTIAL" or "SPLIT");
            return new Match(copy?.DocId, posted?.DocId, posted?.SapDocNo, posted?.Status, rows[0].DocId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileDuplicate] lookup skipped (has sql/36_file_hash.sql been run?): {ex.Message}");
            return null;
        }
    }

    public async Task SaveAsync(int docId, string hash, int? duplicateOf)
    {
        try
        {
            await db.ExecuteAsync("UPDATE ocr.Document SET FileHash=@hash, DuplicateOfDocId=@duplicateOf WHERE DocId=@docId",
                new { hash, duplicateOf, docId });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileDuplicate] could not store the file hash of document {docId}: {ex.Message}");
        }
    }

    private record Row(int DocId, string? Status, string? ApDocCategory, string? SapDocNo, int? SourceDocId, string? OcrProvider);

    private async Task<List<Row>> QueryAsync(string hash, string? salesOrg)
    {
        var rows = await db.QueryAsync("""
            SELECT DocId, Status, ApDocCategory, SapDocNo, SourceDocId, OcrProvider
            FROM ocr.Document
            WHERE FileHash = @hash AND Module IN ('AP','II')
              AND (@salesOrg IS NULL OR SalesOrg IS NULL OR SalesOrg = @salesOrg)
            ORDER BY COALESCE(UpdatedAt, CreatedAt) DESC, DocId DESC
            """, new { hash, salesOrg = string.IsNullOrWhiteSpace(salesOrg) ? null : salesOrg.Trim() });
        return rows.Select(r => new Row((int)r.DocId, (string?)r.Status, (string?)r.ApDocCategory,
            r.SapDocNo == null ? null : Convert.ToString(r.SapDocNo), (int?)r.SourceDocId, (string?)r.OcrProvider)).ToList();
    }

    /// <summary>Documents uploaded before FileHash existed have none. Only files of exactly the same
    /// size can be the same file, so those few are hashed now (and remembered). A file that has been
    /// compressed since (Archive:CompressEnabled) no longer has its upload size and is skipped —
    /// its bytes are not the uploaded ones any more.</summary>
    private async Task<int> HashOlderFilesAsync(int size)
    {
        var candidates = await db.QueryAsync("""
            SELECT TOP (20) DocId, StoredPath FROM ocr.Document
            WHERE FileHash IS NULL AND FileSize = @size AND Module IN ('AP','II')
              AND StoredPath IS NOT NULL AND StoredPath <> '' AND FileExpiredAt IS NULL
            """, new { size });
        var hashed = 0;
        foreach (var c in candidates)
        {
            string path = (string?)c.StoredPath ?? "";
            int docId = (int)c.DocId;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length != size) continue;
                await db.ExecuteAsync("UPDATE ocr.Document SET FileHash=@hash WHERE DocId=@docId AND FileHash IS NULL",
                    new { hash = HashFile(path), docId });
                hashed++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FileDuplicate] could not hash the file of document {docId}: {ex.Message}");
            }
        }
        return hashed;
    }
}

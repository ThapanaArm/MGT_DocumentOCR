using Dapper;

namespace MgtOcr.Data;

// A physical upload that is fully posted and still needs to be sent to SharePoint.
public record ArchiveCandidate(string StoredPath, string FileName, string CompanyCode, string Module, DateTime? PostedAt, string? SubPath = null);

// Per-status counts for the admin page.
public record ArchiveStatusCount(string Status, int Count);

// The archive row for a file (used to stream it back from SharePoint once the local copy is gone).
public record ArchiveRecord(string StoredPath, string CompanyCode, string? RemoteItemId, string? RemoteUrl, string Status);

// A local file the cleanup job may delete, with the reason (for the log).
public record CleanupCandidate(string StoredPath, string Reason);

// Persistence for ocr.FileArchive (sql/27_file_archive.sql) and the queries that decide which
// local files may be archived / removed. A file is keyed by StoredPath because split documents
// share one physical file. Uses the connection factory directly (worker writes are not retried).
public class FileArchiveRepository(DbConnectionFactory factory)
{
    // ---- The queue ----
    // A post that succeeds calls EnqueueAsync; the worker only ever reads queued rows, so documents
    // posted before the archive existed are never picked up.

    /// <summary>Queues the document's source file for SharePoint (Status=PENDING). A file already
    /// queued or archived (split documents share one file) is left as it is.</summary>
    public async Task EnqueueAsync(int docId, string docTable, string companyCode, string module, string? subPath)
    {
        await using var conn = await factory.OpenAsync();
        var d = await conn.QueryFirstOrDefaultAsync<(string? StoredPath, string? FileName, DateTime? PostedAt)>(
            $"SELECT StoredPath, FileName, PostedAt FROM {docTable} WHERE DocId=@docId", new { docId });
        if (string.IsNullOrWhiteSpace(d.StoredPath)) return;
        await conn.ExecuteAsync("""
            MERGE ocr.FileArchive AS t
            USING (SELECT @StoredPath AS StoredPath) s ON t.StoredPath=s.StoredPath
            WHEN NOT MATCHED THEN INSERT(StoredPath,FileName,CompanyCode,Status,Attempts,DocId,Module,SubPath,PostedAt)
                 VALUES(@StoredPath,@FileName,@companyCode,'PENDING',0,@docId,@module,@subPath,COALESCE(@PostedAt,SYSDATETIME()));
            """, new { d.StoredPath, d.FileName, d.PostedAt, companyCode, docId, module, subPath });
    }

    // Queued files to send now: PENDING, or FAILED with attempts left once its back-off has passed
    // (5, 10, 15 ... minutes). companies / modules come from the configured SharePoint targets
    // (null = any), so a queued file without a target simply waits until one is configured.
    // uploadDir: only files stored by THIS installation (dev PC and server may share a database).
    public async Task<IEnumerable<ArchiveCandidate>> FindToArchiveAsync(int batch, int maxAttempts,
        IReadOnlyCollection<string>? companies = null, IReadOnlyCollection<string>? modules = null,
        string? uploadDir = null, bool requireCompressed = false, CancellationToken ct = default)
    {
        var prefix = PrefixFor(uploadDir);
        var anyCompany = companies is null || companies.Count == 0;
        var anyModule = modules is null || modules.Count == 0;
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync<ArchiveCandidate>("""
            SELECT TOP(@batch) StoredPath, ISNULL(FileName,'') AS FileName, CompanyCode, ISNULL(Module,'') AS Module, PostedAt, SubPath
            FROM ocr.FileArchive
            WHERE (Status='PENDING'
                   OR (Status='FAILED' AND Attempts<@maxAttempts
                       AND COALESCE(UpdatedAt, CreatedAt) < DATEADD(MINUTE, -5 * Attempts, SYSDATETIME())))
              AND (@prefix='' OR StoredPath LIKE @prefix)
              -- when compression is on, wait until the file has been processed (DONE / SKIPPED / FAILED)
              AND (@requireCompressed=0 OR EXISTS(SELECT 1 FROM ocr.FileCompress c WHERE c.StoredPath=ocr.FileArchive.StoredPath))
              AND (@anyCompany=1 OR CompanyCode IN @companies)
              AND (@anyModule=1  OR Module      IN @modules)
            ORDER BY CreatedAt
            """, new { batch, maxAttempts, prefix, requireCompressed, anyCompany, anyModule,
                      companies = anyCompany ? new[] { "" } : companies!.ToArray(),
                      modules = anyModule ? new[] { "" } : modules!.ToArray() });
    }

    public async Task MarkDoneAsync(ArchiveCandidate c, string itemId, string remotePath, string? webUrl)
    {
        await using var conn = await factory.OpenAsync();
        await conn.ExecuteAsync("""
            MERGE ocr.FileArchive AS t
            USING (SELECT @StoredPath AS StoredPath) s ON t.StoredPath=s.StoredPath
            WHEN MATCHED THEN UPDATE SET Status='DONE', Attempts=t.Attempts+1, LastError=NULL,
                 RemoteItemId=@itemId, RemotePath=@remotePath, RemoteUrl=@webUrl, ArchivedAt=SYSDATETIME(), UpdatedAt=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT(StoredPath,FileName,CompanyCode,Status,Attempts,RemoteItemId,RemotePath,RemoteUrl,ArchivedAt)
                 VALUES(@StoredPath,@FileName,@CompanyCode,'DONE',1,@itemId,@remotePath,@webUrl,SYSDATETIME());
            """, new { c.StoredPath, c.FileName, c.CompanyCode, itemId, remotePath, webUrl });
    }

    // The local file is gone (deleted, or never on this disk): no point retrying.
    public async Task MarkMissingAsync(ArchiveCandidate c)
    {
        await using var conn = await factory.OpenAsync();
        await conn.ExecuteAsync("""
            MERGE ocr.FileArchive AS t
            USING (SELECT @StoredPath AS StoredPath) s ON t.StoredPath=s.StoredPath
            WHEN MATCHED THEN UPDATE SET Status='MISSING', Attempts=t.Attempts+1, LastError='Local file not found', UpdatedAt=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT(StoredPath,FileName,CompanyCode,Status,Attempts,LastError)
                 VALUES(@StoredPath,@FileName,@CompanyCode,'MISSING',1,'Local file not found');
            """, new { c.StoredPath, c.FileName, c.CompanyCode });
    }

    public async Task MarkFailedAsync(ArchiveCandidate c, string error)
    {
        await using var conn = await factory.OpenAsync();
        var e = error.Length > 1000 ? error[..1000] : error;
        await conn.ExecuteAsync("""
            MERGE ocr.FileArchive AS t
            USING (SELECT @StoredPath AS StoredPath) s ON t.StoredPath=s.StoredPath
            WHEN MATCHED THEN UPDATE SET Status='FAILED', Attempts=t.Attempts+1, LastError=@e, UpdatedAt=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT(StoredPath,FileName,CompanyCode,Status,Attempts,LastError)
                 VALUES(@StoredPath,@FileName,@CompanyCode,'FAILED',1,@e);
            """, new { c.StoredPath, c.FileName, c.CompanyCode, e });
    }

    public async Task<IEnumerable<ArchiveStatusCount>> CountByStatusAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync<ArchiveStatusCount>(
            "SELECT Status, COUNT(*) AS Count FROM ocr.FileArchive GROUP BY Status");
    }

    // Latest archive rows (newest first) for the admin page.
    public async Task<IEnumerable<dynamic>> RecentAsync(int top, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync("""
            SELECT TOP(@top) FileName, CompanyCode, Status, Attempts, LastError, RemotePath, RemoteUrl,
                   ArchivedAt, LocalDeletedAt, COALESCE(UpdatedAt, CreatedAt) AS UpdatedAt
            FROM ocr.FileArchive ORDER BY COALESCE(UpdatedAt, CreatedAt) DESC
            """, new { top });
    }

    public async Task<ArchiveRecord?> FindAsync(string storedPath)
    {
        await using var conn = await factory.OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<ArchiveRecord>(
            "SELECT StoredPath, CompanyCode, RemoteItemId, RemoteUrl, Status FROM ocr.FileArchive WHERE StoredPath=@storedPath",
            new { storedPath });
    }

    public async Task MarkLocalDeletedAsync(string storedPath)
    {
        await using var conn = await factory.OpenAsync();
        await conn.ExecuteAsync("UPDATE ocr.FileArchive SET LocalDeletedAt=SYSDATETIME(), UpdatedAt=SYSDATETIME() WHERE StoredPath=@storedPath",
            new { storedPath });
    }

    // Archived files whose grace period has passed and whose local copy still exists.
    public async Task<IEnumerable<CleanupCandidate>> FindArchivedToDeleteAsync(int graceHours, int batch, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync<CleanupCandidate>("""
            SELECT TOP(@batch) StoredPath, 'archived to SharePoint' AS Reason
            FROM ocr.FileArchive
            WHERE Status='DONE' AND LocalDeletedAt IS NULL AND RemoteItemId IS NOT NULL
              AND ArchivedAt < DATEADD(HOUR, -@graceHours, SYSDATETIME())
            ORDER BY ArchivedAt
            """, new { graceHours, batch });
    }

    // Files never posted: every referencing document is un-posted and untouched for `days`.
    public async Task<IEnumerable<CleanupCandidate>> FindStaleDraftsAsync(int days, int batch, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync<CleanupCandidate>("""
            ;WITH allDocs AS (
                SELECT StoredPath, Status, COALESCE(UpdatedAt,CreatedAt) AS Ts FROM ocr.Document   WHERE StoredPath IS NOT NULL AND StoredPath<>''
                UNION ALL
                SELECT StoredPath, Status, COALESCE(UpdatedAt,CreatedAt) AS Ts FROM ocr.SalesOrder WHERE StoredPath IS NOT NULL AND StoredPath<>''
            )
            SELECT TOP(@batch) StoredPath, 'unposted draft idle > ' + CAST(@days AS varchar(10)) + ' days' AS Reason
            FROM allDocs
            GROUP BY StoredPath
            HAVING SUM(CASE WHEN Status='POSTED' THEN 1 ELSE 0 END)=0
               AND MAX(Ts) < DATEADD(DAY, -@days, SYSDATETIME())
            """, new { days, batch });
    }

    // Files of OCR jobs that failed and produced no document.
    public async Task<IEnumerable<CleanupCandidate>> FindFailedJobFilesAsync(int days, int batch, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync<CleanupCandidate>("""
            SELECT TOP(@batch) j.StoredPath, 'failed OCR job idle > ' + CAST(@days AS varchar(10)) + ' days' AS Reason
            FROM ocr.OcrJob j
            WHERE j.Status='FAILED' AND j.StoredPath<>'' AND j.FinishedAt < DATEADD(DAY, -@days, SYSDATETIME())
              AND NOT EXISTS(SELECT 1 FROM ocr.Document   d WHERE d.StoredPath=j.StoredPath)
              AND NOT EXISTS(SELECT 1 FROM ocr.SalesOrder s WHERE s.StoredPath=j.StoredPath)
              AND NOT EXISTS(SELECT 1 FROM ocr.OcrJob o WHERE o.StoredPath=j.StoredPath AND o.Status IN ('QUEUED','PROCESSING'))
            GROUP BY j.StoredPath
            """, new { days, batch });
    }

    // True when nothing in the database refers to this file any more (used by the orphan sweep).
    public async Task<bool> IsUnreferencedAsync(string storedPath)
    {
        await using var conn = await factory.OpenAsync();
        return await conn.ExecuteScalarAsync<int>("""
            SELECT CASE WHEN EXISTS(SELECT 1 FROM ocr.Document   WHERE StoredPath=@storedPath)
                          OR EXISTS(SELECT 1 FROM ocr.SalesOrder WHERE StoredPath=@storedPath)
                          OR EXISTS(SELECT 1 FROM ocr.OcrJob     WHERE StoredPath=@storedPath)
                        THEN 1 ELSE 0 END
            """, new { storedPath }) == 0;
    }

    // ---- In-place compression bookkeeping (sql/28_file_compress.sql) ----

    private static string PrefixFor(string? uploadDir) => string.IsNullOrWhiteSpace(uploadDir) ? "" :
        (Path.GetFullPath(uploadDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar)
            .Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    // Stored files of this installation whose document exists (= OCR finished) for at least
    // `minAgeMinutes` and that were never processed.
    public async Task<IEnumerable<string>> FindToCompressAsync(int minAgeMinutes, int batch, string? uploadDir, CancellationToken ct = default)
    {
        var prefix = PrefixFor(uploadDir);
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryAsync<string>("""
            ;WITH allDocs AS (
                SELECT StoredPath, CreatedAt FROM ocr.Document   WHERE StoredPath IS NOT NULL AND StoredPath<>''
                UNION ALL
                SELECT StoredPath, CreatedAt FROM ocr.SalesOrder WHERE StoredPath IS NOT NULL AND StoredPath<>''
            )
            SELECT TOP(@batch) d.StoredPath
            FROM allDocs d
            WHERE NOT EXISTS(SELECT 1 FROM ocr.FileCompress c WHERE c.StoredPath=d.StoredPath)
              AND (@prefix='' OR d.StoredPath LIKE @prefix)
            GROUP BY d.StoredPath
            HAVING MAX(d.CreatedAt) <= DATEADD(MINUTE, -@minAgeMinutes, SYSDATETIME())
            ORDER BY MAX(d.CreatedAt) DESC
            """, new { minAgeMinutes, batch, prefix });
    }

    public async Task MarkCompressedAsync(string storedPath, string status, long before, long after, string? note)
    {
        await using var conn = await factory.OpenAsync();
        var n = note is { Length: > 500 } ? note[..500] : note;
        await conn.ExecuteAsync("""
            MERGE ocr.FileCompress AS t
            USING (SELECT @storedPath AS StoredPath) s ON t.StoredPath=s.StoredPath
            WHEN MATCHED THEN UPDATE SET Status=@status, BytesBefore=@before, BytesAfter=@after, Note=@n, ProcessedAt=SYSDATETIME()
            WHEN NOT MATCHED THEN INSERT(StoredPath,Status,BytesBefore,BytesAfter,Note) VALUES(@storedPath,@status,@before,@after,@n);
            """, new { storedPath, status, before, after, n });
    }
}

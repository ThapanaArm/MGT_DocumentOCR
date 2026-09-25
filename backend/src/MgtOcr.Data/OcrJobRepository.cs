using Dapper;

namespace MgtOcr.Data;

// A job the queue worker has claimed and will OCR now.
public record OcrJobClaim(int JobId, string Module, string ApDocCategory, string Engine,
    string FileName, string StoredPath, int FileSize, string CreatedBy);

// A job row as the frontend polls it (typed record -> serialized camelCase, unlike raw Dapper rows).
public record OcrJobStatus(int JobId, string FileName, string Status, string? Error, int? ResultDocId, string Module);

// CRUD + atomic claim for the ocr.OcrJob queue (sql/24_ocr_job.sql). Uses the connection factory
// directly (not Db) so the claim/marks run on non-retried write connections.
public class OcrJobRepository(DbConnectionFactory factory)
{
    public async Task<int> EnqueueAsync(Guid batchId, string module, string apDocCategory, string engine,
        string fileName, string storedPath, int size, string createdBy, string status = "QUEUED", string? error = null)
    {
        await using var conn = await factory.OpenAsync();
        var id = await conn.ExecuteScalarAsync<decimal>("""
            INSERT ocr.OcrJob(BatchId,Module,ApDocCategory,Engine,FileName,StoredPath,FileSize,Status,Error,CreatedBy,FinishedAt)
            VALUES(@batchId,@module,@apDocCategory,@engine,@fileName,@storedPath,@size,@status,@error,@createdBy,
                   CASE WHEN @status='QUEUED' THEN NULL ELSE SYSDATETIME() END);
            SELECT SCOPE_IDENTITY();
            """, new { batchId, module, apDocCategory, engine, fileName, storedPath, size, status, error, createdBy });
        return (int)id;
    }

    // Atomically take the oldest QUEUED job and flip it to PROCESSING. READPAST lets parallel
    // workers skip a row another worker is already claiming instead of blocking on it.
    public async Task<OcrJobClaim?> ClaimNextAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<OcrJobClaim>("""
            ;WITH nxt AS (
                SELECT TOP(1) * FROM ocr.OcrJob WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE Status='QUEUED' ORDER BY JobId
            )
            UPDATE nxt SET Status='PROCESSING', StartedAt=SYSDATETIME()
            OUTPUT inserted.JobId, inserted.Module, inserted.ApDocCategory, inserted.Engine,
                   inserted.FileName, inserted.StoredPath, inserted.FileSize, inserted.CreatedBy;
            """);
    }

    public async Task MarkDoneAsync(int jobId, int resultDocId)
    {
        await using var conn = await factory.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE ocr.OcrJob SET Status='DONE', ResultDocId=@resultDocId, Error=NULL, FinishedAt=SYSDATETIME() WHERE JobId=@jobId",
            new { jobId, resultDocId });
    }

    public async Task MarkFailedAsync(int jobId, string error)
    {
        await using var conn = await factory.OpenAsync();
        var e = error.Length > 1000 ? error[..1000] : error;
        await conn.ExecuteAsync(
            "UPDATE ocr.OcrJob SET Status='FAILED', Error=@e, FinishedAt=SYSDATETIME() WHERE JobId=@jobId",
            new { jobId, e });
    }

    // On startup any PROCESSING row is an orphan from a previous run (the app stopped mid-read) —
    // put it back in the queue so it is retried rather than stuck forever.
    public async Task<int> ResetOrphansAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("UPDATE ocr.OcrJob SET Status='QUEUED', StartedAt=NULL WHERE Status='PROCESSING'");
    }

    public async Task<IEnumerable<OcrJobStatus>> ListByBatchAsync(Guid batchId)
    {
        await using var conn = await factory.OpenAsync();
        return await conn.QueryAsync<OcrJobStatus>(
            "SELECT JobId, FileName, Status, Error, ResultDocId, Module FROM ocr.OcrJob WHERE BatchId=@batchId ORDER BY JobId",
            new { batchId });
    }
}

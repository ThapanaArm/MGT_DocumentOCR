using Dapper;

namespace MgtOcr.Data;

/// <summary>A job row that has been started and is waiting to be finished.</summary>
public readonly record struct JobHandle(long JobId, string JobNumber);

/// <summary>
/// Writes ocr.JobLog (sql/29_job_log.sql): when each step of a document started and finished, and
/// what it cost.
///
/// Not to be confused with <see cref="OcrJobRepository"/> / ocr.OcrJob, which is the BATCH UPLOAD
/// QUEUE — a single-file upload never goes through that queue, and it carries no tokens or cost.
/// This one records every run on every path, which is what a cost report needs.
///
/// Every method swallows its own errors. A failure to write a log row must never fail the work
/// being logged: losing one row of accounting data is cheaper than losing a posted SAP invoice,
/// and the two happen in the same request. Failures go to stderr so they are not silent.
///
/// Uses DbConnectionFactory rather than Db for the same reason OcrJobRepository does: these are
/// plain writes that should not go through the retrying read connection.
/// </summary>
public class JobLogRepository(DbConnectionFactory factory)
{
    /// <summary>A new reference number for one run of one document — "J-20261020-000123".
    /// Every step of that run (OCR, mapping, the SAP post) is written under the same number, which
    /// is what lets the timeline view put them on one line.</summary>
    public async Task<string> NextJobNumberAsync()
    {
        try
        {
            await using var conn = await factory.OpenAsync();
            var n = await conn.ExecuteScalarAsync<long>("SELECT NEXT VALUE FOR ocr.JobNumberSeq");
            return $"J-{DateTime.Now:yyyyMMdd}-{n:D6}";
        }
        catch (Exception ex)
        {
            // Without a number the steps cannot be grouped, but they can still be written
            // individually against the document, so fall back rather than give up.
            Console.Error.WriteLine($"[JobLog] could not read the sequence: {ex.Message}");
            return $"J-{DateTime.Now:yyyyMMddHHmmssfff}";
        }
    }

    /// <summary>Opens a RUNNING row and returns its handle. Null means the row could not be
    /// written — callers treat that as "no logging for this step" and carry on.</summary>
    public async Task<JobHandle?> StartAsync(
        string jobNumber, string jobType, int? docId = null, string? module = null,
        string? provider = null, string? model = null, string? fileName = null,
        int? fileSize = null, int? pageCount = null, string? performedBy = null)
    {
        try
        {
            await using var conn = await factory.OpenAsync();
            var id = await conn.ExecuteScalarAsync<long>("""
                INSERT ocr.JobLog(JobNumber,DocId,Module,JobType,Provider,Model,Status,StartedAt,
                                  FileName,FileSize,PageCount,PerformedBy)
                VALUES(@jobNumber,@docId,@module,@jobType,@provider,@model,'RUNNING',SYSDATETIME(),
                       @fileName,@fileSize,@pageCount,@performedBy);
                SELECT CAST(SCOPE_IDENTITY() AS bigint);
                """, new { jobNumber, docId, module, jobType, provider, model, fileName, fileSize, pageCount, performedBy });
            return new JobHandle(id, jobNumber);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JobLog] start {jobType} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Closes the row: stamps FinishedAt, works out DurationMs from the stored StartedAt
    /// (not from a clock the caller passes, so the two timestamps can never disagree), and records
    /// what the step used.</summary>
    public async Task FinishAsync(
        JobHandle? handle, string status, int? docId = null, int? tokensIn = null, int? tokensOut = null,
        decimal? cost = null, string? costCurrency = null, string? provider = null, string? model = null,
        string? sapDocNo = null, string? message = null, int? pageCount = null)
    {
        if (handle is not { } h) return;
        try
        {
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync("""
                UPDATE ocr.JobLog
                   SET Status      = @status,
                       FinishedAt  = SYSDATETIME(),
                       DurationMs  = DATEDIFF(MILLISECOND, StartedAt, SYSDATETIME()),
                       DocId       = ISNULL(@docId, DocId),
                       TokensIn    = ISNULL(@tokensIn, TokensIn),
                       TokensOut   = ISNULL(@tokensOut, TokensOut),
                       Cost        = ISNULL(@cost, Cost),
                       CostCurrency= ISNULL(@costCurrency, CostCurrency),
                       Provider    = ISNULL(@provider, Provider),
                       Model       = ISNULL(@model, Model),
                       PageCount   = ISNULL(@pageCount, PageCount),
                       SapDocNo    = ISNULL(@sapDocNo, SapDocNo),
                       Message     = @message
                 WHERE JobId = @jobId
                """, new { jobId = h.JobId, status, docId, tokensIn, tokensOut, cost, costCurrency,
                           provider, model, pageCount, sapDocNo, message });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JobLog] finish {h.JobId} failed: {ex.Message}");
        }
    }

    /// <summary>The run a later step belongs to: the most recent JobNumber already written for
    /// this document, or a fresh one when there is none. A SAP post can happen days after the read
    /// — grouping it with that read is what makes "started reading at X, posted at Y" answerable
    /// from one row of the timeline view. A re-read opens a NEW number, so a post that follows it
    /// is grouped with the read it was actually built from.</summary>
    public async Task<string> JobNumberForDocumentAsync(int docId)
    {
        try
        {
            await using var conn = await factory.OpenAsync();
            var existing = await conn.ExecuteScalarAsync<string?>(
                "SELECT TOP 1 JobNumber FROM ocr.JobLog WHERE DocId = @docId ORDER BY StartedAt DESC, JobId DESC",
                new { docId });
            if (!string.IsNullOrWhiteSpace(existing)) return existing!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JobLog] lookup for document {docId} failed: {ex.Message}");
        }
        return await NextJobNumberAsync();
    }

    /// <summary>Every step of one run, oldest first — what the Activity Log screen shows.</summary>
    public async Task<IEnumerable<dynamic>> ByJobNumberAsync(string jobNumber)
    {
        await using var conn = await factory.OpenAsync();
        return await conn.QueryAsync("""
            SELECT JobId, JobNumber, DocId, Module, JobType, Provider, Model, Status,
                   StartedAt, FinishedAt, DurationMs, TokensIn, TokensOut, TokensTotal,
                   Cost, CostCurrency, SapDocNo, Message, PerformedBy
              FROM ocr.JobLog WHERE JobNumber = @jobNumber ORDER BY StartedAt, JobId
            """, new { jobNumber });
    }

    /// <summary>Every step of one document, newest run first.</summary>
    public async Task<IEnumerable<dynamic>> ByDocumentAsync(int docId)
    {
        await using var conn = await factory.OpenAsync();
        return await conn.QueryAsync("""
            SELECT JobId, JobNumber, JobType, Provider, Model, Status,
                   StartedAt, FinishedAt, DurationMs, TokensIn, TokensOut, TokensTotal,
                   Cost, CostCurrency, SapDocNo, Message, PerformedBy
              FROM ocr.JobLog WHERE DocId = @docId ORDER BY StartedAt DESC, JobId DESC
            """, new { docId });
    }
}

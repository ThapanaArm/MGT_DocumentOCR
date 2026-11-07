using Dapper;

namespace MgtOcr.Data;

/// <summary>A queue row the worker has claimed and will send to SAP now.</summary>
public record SapPostQueueClaim(int QueueId, int DocId, string Module, string RequestedBy, string? UserJson);

/// <summary>A queue row as the screen polls it. Status: 1 Pending, 0 In process, 2 Completed, 9 Error.
/// Position is how many rows are ahead of it (0 = next / being sent).</summary>
public record SapPostQueueStatus(int QueueId, int DocId, string Module, byte Status, string RequestedBy,
    DateTime RequestedAt, DateTime? StartedAt, DateTime? FinishedAt, int Attempts,
    string? SapDocNos, string? ResultMessage, string? ResultJson, int Position);

/// <summary>ocr.SapPostQueue (sql/32_sap_post_queue.sql): Step 3 sends to SAP one document at a time,
/// first come first served. Same pattern as <see cref="OcrJobRepository"/>: an atomic claim with
/// READPAST/UPDLOCK so two app instances never take the same row.</summary>
public class SapPostQueueRepository(DbConnectionFactory factory)
{
    public const byte InProcess = 0, Pending = 1, Completed = 2, Error = 9;

    private const string StatusSelect = """
        SELECT q.QueueId, q.DocId, q.Module, q.Status, q.RequestedBy, q.RequestedAt, q.StartedAt, q.FinishedAt,
               q.Attempts, q.SapDocNos, q.ResultMessage, q.ResultJson,
               CASE WHEN q.Status = 1 THEN
                   (SELECT COUNT(*) FROM ocr.SapPostQueue a
                    WHERE a.Status = 0 OR (a.Status = 1 AND (a.RequestedAt < q.RequestedAt
                          OR (a.RequestedAt = q.RequestedAt AND a.QueueId < q.QueueId))))
               ELSE 0 END AS Position
        FROM ocr.SapPostQueue q
        """;

    /// <summary>Puts the document in the queue as Pending, or returns its row when it is already
    /// waiting or being sent — pressing Step 3 twice does not send twice.</summary>
    public async Task<int> EnqueueAsync(int docId, string module, string requestedBy, string? userJson, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var existing = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT TOP(1) QueueId FROM ocr.SapPostQueue WHERE DocId=@docId AND Status IN (0,1) ORDER BY QueueId DESC",
            new { docId });
        if (existing is int id) return id;
        try
        {
            return await conn.ExecuteScalarAsync<int>("""
                INSERT INTO ocr.SapPostQueue (DocId, Module, Status, RequestedBy, UserJson)
                OUTPUT inserted.QueueId
                VALUES (@docId, @module, 1, @requestedBy, @userJson)
                """, new { docId, module, requestedBy, userJson });
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2601 or 2627)
        {
            // Two clicks raced past the check above; the unique index kept one row — return it.
            return await conn.ExecuteScalarAsync<int>(
                "SELECT TOP(1) QueueId FROM ocr.SapPostQueue WHERE DocId=@docId AND Status IN (0,1) ORDER BY QueueId DESC",
                new { docId });
        }
    }

    /// <summary>Takes the oldest Pending row and marks it In process — only when nothing else is
    /// In process, so SAP receives one document at a time.</summary>
    public async Task<SapPostQueueClaim?> ClaimNextAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SapPostQueueClaim>("""
            IF NOT EXISTS (SELECT 1 FROM ocr.SapPostQueue WITH (READCOMMITTEDLOCK) WHERE Status = 0)
            BEGIN
                ;WITH nxt AS (
                    SELECT TOP(1) * FROM ocr.SapPostQueue WITH (READPAST, UPDLOCK, ROWLOCK)
                    WHERE Status = 1 ORDER BY RequestedAt, QueueId
                )
                UPDATE nxt SET Status = 0, StartedAt = SYSDATETIME(), Attempts = Attempts + 1
                OUTPUT inserted.QueueId, inserted.DocId, inserted.Module, inserted.RequestedBy, inserted.UserJson;
            END
            """);
    }

    public async Task FinishAsync(int queueId, bool success, string? sapDocNos, string? message, string? resultJson)
    {
        await using var conn = await factory.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE ocr.SapPostQueue
            SET Status = @status, FinishedAt = SYSDATETIME(), SapDocNos = @sapDocNos,
                ResultMessage = @message, ResultJson = @resultJson
            WHERE QueueId = @queueId
            """, new
        {
            queueId,
            status = success ? Completed : Error,
            sapDocNos = sapDocNos is { Length: > 400 } s ? s[..400] : sapDocNos,
            message,
            resultJson,
        });
    }

    /// <summary>Rows left In process by an app that stopped mid-send go back to Pending. Sending
    /// again is safe: suppliers already created in SAP are skipped (ocr.PostLog).</summary>
    public async Task<int> ResetOrphansAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("UPDATE ocr.SapPostQueue SET Status = 1, StartedAt = NULL WHERE Status = 0");
    }

    public async Task<SapPostQueueStatus?> GetAsync(int queueId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SapPostQueueStatus>(StatusSelect + " WHERE q.QueueId = @queueId", new { queueId });
    }

    public async Task<SapPostQueueStatus?> LatestForDocAsync(int docId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<SapPostQueueStatus>(
            StatusSelect + " WHERE q.DocId = @docId ORDER BY q.QueueId DESC", new { docId });
    }

    /// <summary>The queue as it stands: everything waiting or being sent, then the latest finished.</summary>
    public async Task<IReadOnlyList<SapPostQueueStatus>> ListAsync(int top = 50, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<SapPostQueueStatus>(
            "SELECT TOP(@top) * FROM (" + StatusSelect + ") x ORDER BY CASE x.Status WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE 2 END, "
            + "CASE WHEN x.Status IN (0,1) THEN x.RequestedAt END ASC, x.QueueId DESC", new { top });
        return rows.ToList();
    }
}

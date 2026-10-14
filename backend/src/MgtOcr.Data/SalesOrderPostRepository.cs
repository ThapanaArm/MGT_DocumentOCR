using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Mapping;

namespace MgtOcr.Data;

/// <summary>Post state of one Sales Order line (sql/31_so_post_group.sql columns).</summary>
public record SoLinePostState(string ItemNo, string? PostedSoNo, DateTime? PostedAt, string? PostError);

/// <summary>Send state of one delivery-date group = one SAP / Zoho Sales Order.</summary>
public record SoGroupState(string Key, List<string> ItemNos, bool Posted, string? DocNo, DateTime? PostedAt, string? Error);

/// <summary>
/// Per-delivery-date Sales Order posting (SAP and Zoho) without an extra table: every line stores the
/// Sales Order it went into (ocr.SalesOrderLine.PostedSoNo/PostedAt) or the last error of its group
/// (PostError). A group counts as posted when all its lines have a PostedSoNo. Each attempt is also
/// an ocr.PostLog row. FinalizeAsync derives the document status: all lines posted -> POSTED,
/// some -> PARTIAL.
/// </summary>
public class SalesOrderPostRepository(Db db)
{
    public async Task<Dictionary<string, SoLinePostState>> LineStateAsync(int docId)
    {
        await using var conn = await db.OpenAsync();
        var rows = await conn.QueryAsync<SoLinePostState>(
            "SELECT CAST(ItemNo AS nvarchar(20)) AS ItemNo, PostedSoNo, PostedAt, PostError FROM ocr.SalesOrderLine WHERE DocId=@docId",
            new { docId });
        return rows.GroupBy(r => r.ItemNo).ToDictionary(g => g.Key, g => g.First());
    }

    public async Task<List<SoGroupState>> GroupStatesAsync(int docId, IReadOnlyList<SoGroup> groups)
    {
        var st = await LineStateAsync(docId);
        return groups.Select(g =>
        {
            var mine = g.ItemNos.Select(n => st.GetValueOrDefault(n)).ToList();
            var posted = mine.Count > 0 && mine.All(x => !string.IsNullOrEmpty(x?.PostedSoNo));
            var docNo = mine.Select(x => x?.PostedSoNo).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            var at = mine.Select(x => x?.PostedAt).FirstOrDefault(x => x != null);
            var err = posted ? null : mine.Select(x => x?.PostError).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            return new SoGroupState(g.Key, g.ItemNos, posted, docNo, at, err);
        }).ToList();
    }

    /// <summary>Records one group's send: stamps its lines (SO no. on success, error on failure) and
    /// writes the ocr.PostLog row, in one transaction.</summary>
    public async Task RecordAsync(int docId, string module, IReadOnlyList<string> itemNos, bool success,
        string? extDocNo, string? message, string? endpoint, string payloadJson, string user, string? salesOrg)
    {
        var items = itemNos.Select(n => int.TryParse(n, out var v) ? v : -1).Where(v => v >= 0).ToArray();
        var error = success ? null : (message ?? "Send failed");
        if (error is { Length: > 1000 }) error = error[..1000];
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        if (success)
            await conn.ExecuteAsync(
                "UPDATE ocr.SalesOrderLine SET PostedSoNo=@extDocNo, PostedAt=SYSDATETIME(), PostError=NULL WHERE DocId=@docId AND ItemNo IN @items",
                new { extDocNo, docId, items }, tx);
        else
            await conn.ExecuteAsync(
                "UPDATE ocr.SalesOrderLine SET PostError=@error WHERE DocId=@docId AND ItemNo IN @items AND PostedSoNo IS NULL",
                new { error, docId, items }, tx);
        await conn.ExecuteAsync("""
            INSERT ocr.PostLog(DocId,Module,SapDocNo,Endpoint,PayloadJson,Success,Message,PostedBy,SalesOrg)
            VALUES(@docId,@module,@extDocNo,@endpoint,@payloadJson,@success,@message,@user,@salesOrg)
            """, new { docId, module, extDocNo, endpoint, payloadJson, success = success ? 1 : 0, message, user,
                       salesOrg = string.IsNullOrWhiteSpace(salesOrg) ? null : salesOrg }, tx);
        await tx.CommitAsync();
    }

    /// <summary>
    /// Claims the document for one send (12 Oct 2026): flips it to POSTING inside a locked
    /// transaction and returns the status it had, or null when it is already being sent (double
    /// click, a second tab, SAP and Zoho at once) or can't be sent (POSTED / SPLIT). A POSTING
    /// claim older than 10 minutes counts as abandoned (the server stopped mid-send) and can be
    /// taken over. Always pair with <see cref="ReleaseAsync"/> in a finally block.
    /// </summary>
    public async Task<string?> TryClaimAsync(int docId)
    {
        var t = DocumentTables.ForId(docId).Doc;
        await using var conn = await db.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<string?>($"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            DECLARE @prev nvarchar(20), @stale bit;
            SELECT @prev = Status,
                   @stale = CASE WHEN Status = 'POSTING'
                                  AND ISNULL(UpdatedAt, '19000101') < DATEADD(minute, -10, SYSDATETIME())
                                 THEN 1 ELSE 0 END
              FROM {t} WITH (UPDLOCK, HOLDLOCK) WHERE DocId = @docId;
            IF @prev IS NOT NULL AND (@prev NOT IN ('POSTED', 'SPLIT', 'POSTING') OR @stale = 1)
            BEGIN
                UPDATE {t} SET Status = 'POSTING', UpdatedAt = SYSDATETIME() WHERE DocId = @docId;
                COMMIT;
                SELECT @prev;
            END
            ELSE
            BEGIN
                COMMIT;
                SELECT CAST(NULL AS nvarchar(20));
            END
            """, new { docId });
    }

    /// <summary>Ends a send started by <see cref="TryClaimAsync"/>. FinalizeAsync has already set
    /// POSTED / PARTIAL when something went through; otherwise the document goes back to where it
    /// was (PARTIAL when an earlier send left posted lines; MAPPED for an abandoned claim).</summary>
    public async Task ReleaseAsync(int docId, string previousStatus)
    {
        var t = DocumentTables.ForId(docId).Doc;
        var restore = previousStatus is "POSTING" or "" ? "MAPPED" : previousStatus;
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync($"""
            UPDATE {t}
               SET Status = CASE WHEN EXISTS(SELECT 1 FROM ocr.SalesOrderLine WHERE DocId = @docId AND PostedSoNo IS NOT NULL)
                                 THEN 'PARTIAL' ELSE @restore END,
                   UpdatedAt = SYSDATETIME()
             WHERE DocId = @docId AND Status = 'POSTING'
            """, new { docId, restore });
    }

    /// <summary>
    /// Sets the document's status from its groups: all posted -> POSTED (+ PostedAt/PostedBy/
    /// CompanyCode); at least one -> PARTIAL; none -> unchanged (""). SapDocNo = the posted numbers in
    /// group order, comma-separated. Returns the new status.
    /// </summary>
    public async Task<string> FinalizeAsync(int docId, IReadOnlyList<SoGroup> groups, string? companyCode, string user)
    {
        var states = await GroupStatesAsync(docId, groups);
        var done = states.Where(s => s.Posted).ToList();
        if (done.Count == 0) return "";
        var nos = string.Join(", ", done.Select(s => s.DocNo).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
        if (nos.Length > 400) nos = nos[..400];
        var all = done.Count == states.Count;
        var t = DocumentTables.ForId(docId).Doc;
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(all
            ? $"UPDATE {t} SET Status='POSTED', SapDocNo=@nos, CompanyCode=@companyCode, PostedAt=SYSDATETIME(), PostedBy=@user, UpdatedAt=SYSDATETIME() WHERE DocId=@docId"
            : $"UPDATE {t} SET Status='PARTIAL', SapDocNo=@nos, CompanyCode=@companyCode, UpdatedAt=SYSDATETIME() WHERE DocId=@docId",
            new { nos, companyCode, user, docId });
        return all ? "POSTED" : "PARTIAL";
    }
}

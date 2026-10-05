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

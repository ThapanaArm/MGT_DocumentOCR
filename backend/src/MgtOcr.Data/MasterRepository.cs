using Dapper;
using Microsoft.Data.SqlClient;

namespace MgtOcr.Data;

// Ported from load_masters()/masters_list/masters_create/masters_update/masters_delete in
// app/main.py. Rows come back as Dapper's `dynamic` (DapperRow) objects, which serialize to JSON
// using their original PascalCase SQL column names — deliberately NOT camelCase, to match the
// Python side's `rows(db.query(...))` output exactly (see the AppConfig/Program.cs JSON policy note).
public class MasterRepository(Db db)
{
    public static bool TryGetKind(string kind, out MasterDefinition def) =>
        MasterDefinitions.All.TryGetValue(kind, out def!);

    // load_masters(): active-only for the 4 tables that have IsActive; unfiltered for the rest.
    public async Task<Dictionary<string, IEnumerable<dynamic>>> LoadAllAsync()
    {
        var result = new Dictionary<string, IEnumerable<dynamic>>();
        result["customers"] = await db.QueryAsync("SELECT * FROM ocr.Customer WHERE IsActive=1 ORDER BY CustomerCode");
        result["shiptos"] = await db.QueryAsync("SELECT * FROM ocr.ShipTo WHERE IsActive=1 ORDER BY CustomerCode, ShipToCode");
        result["materials"] = await db.QueryAsync("SELECT * FROM ocr.Material WHERE IsActive=1 ORDER BY MaterialCode");
        result["custmaterials"] = await db.QueryAsync("SELECT * FROM ocr.CustomerMaterial ORDER BY CustomerCode, ExtCode");
        result["vendors"] = await db.QueryAsync("SELECT * FROM ocr.Vendor WHERE IsActive=1 ORDER BY VendorCode");
        result["venmaterials"] = await db.QueryAsync("SELECT * FROM ocr.VendorMaterial ORDER BY VendorCode, ExtCode");
        result["uoms"] = await db.QueryAsync(
            "SELECT * FROM ocr.UomConversion ORDER BY CASE WHEN MaterialCode IS NULL THEN 0 ELSE 1 END, MaterialCode, ExtUom");
        return result;
    }

    // masters_list(): unfiltered (no IsActive check) + optional OR-across-all-columns LIKE search.
    public async Task<IEnumerable<dynamic>> ListAsync(MasterDefinition m, string? q)
    {
        var sql = $"SELECT * FROM {m.Table}";
        var p = new DynamicParameters();
        if (!string.IsNullOrEmpty(q))
        {
            var clauses = new List<string>();
            for (var i = 0; i < m.Cols.Length; i++)
            {
                clauses.Add($"CAST({m.Cols[i]} AS nvarchar(400)) LIKE @q{i}");
                p.Add($"q{i}", $"%{q}%");
            }
            sql += " WHERE (" + string.Join(" OR ", clauses) + ")";
        }
        sql += " ORDER BY " + m.OrderBy;
        return await db.QueryAsync(sql, p);
    }

    // masters_create(): only recognized columns present in the body are inserted.
    public async Task<bool> CreateAsync(MasterDefinition m, Dictionary<string, object?> body)
    {
        var cols = m.Cols.Where(body.ContainsKey).ToArray();
        if (cols.Length == 0) return false;
        var sql = $"INSERT {m.Table}({string.Join(",", cols)}) VALUES({string.Join(",", cols.Select((_, i) => $"@p{i}"))})";
        var p = new DynamicParameters();
        for (var i = 0; i < cols.Length; i++) p.Add($"p{i}", body[cols[i]]);
        await db.ExecuteAsync(sql, p);
        return true;
    }

    // masters_update(): SET only recognized columns present in the body, plus UpdatedAt.
    public async Task<bool> UpdateAsync(MasterDefinition m, string key, Dictionary<string, object?> body)
    {
        var cols = m.Cols.Where(body.ContainsKey).ToArray();
        var sets = string.Join(", ", cols.Select((c, i) => $"{c}=@p{i}")) +
                   (cols.Length > 0 ? ", " : "") + "UpdatedAt=SYSDATETIME()";
        var sql = $"UPDATE {m.Table} SET {sets} WHERE {m.Key}=@key";
        var p = new DynamicParameters();
        for (var i = 0; i < cols.Length; i++) p.Add($"p{i}", body[cols[i]]);
        p.Add("key", key);
        var n = await db.ExecuteAsync(sql, p);
        return n > 0;
    }

    // masters_delete(): SQL error 547 = FK constraint violation (mirrors Python's broad except).
    public async Task<(bool Ok, string? FkError)> DeleteAsync(MasterDefinition m, string key)
    {
        try
        {
            var n = await db.ExecuteAsync($"DELETE FROM {m.Table} WHERE {m.Key}=@key", new { key });
            return (n > 0, null);
        }
        catch (SqlException ex) when (ex.Number == 547)
        {
            return (false, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
        }
    }
}

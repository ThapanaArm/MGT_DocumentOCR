using System.Data.Common;
using Dapper;
using MgtOcr.Core;
using MgtOcr.Core.Mapping;
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
    public async Task<Dictionary<string, IEnumerable<dynamic>>> LoadAllAsync(bool includeInactive = false)
    {
        var result = new Dictionary<string, IEnumerable<dynamic>>();
        // Compatibility aliases are read-only: CRUD always uses the real schema columns and row id.
        var active = includeInactive ? "" : " WHERE IsActive=1";
        result["customers"] = await db.QueryAsync("SELECT *, ComcompyCodeSAP AS CustomerCode, ComcompyCodeSAP AS SapCustomerCode, CompanyName AS NameTh, CompanyNameSAP AS NameEn FROM ocr.Customer" + active + " ORDER BY SalesOrg, ComcompyCodeSAP, id");
        result["shiptos"] = await db.QueryAsync("SELECT *, ShipToAddress AS Address FROM ocr.ShipTo" + active + " ORDER BY CustomerCode, ShipToCode, id");
        result["custmaterials"] = await db.QueryAsync("SELECT *, MaterialCodeCode AS ExtCode, MaterialCodeName AS ExtDesc, MaterialCodeSAP AS MaterialCode FROM ocr.CustomerMaterial" + (includeInactive ? "" : " WHERE Isactive=1") + " ORDER BY SalesOrg, CustomerCode, MaterialCodeCode, Id");
        // Materials for the SO mapping come straight from CustomerMaterial. ocr.Material has been
        // dropped (unused), so Base Unit / Plant / Material Group are returned as NULL — the columns
        // are kept only so downstream code that reads them still finds the keys. With no base unit,
        // unit handling now relies entirely on ocr.UomConversion rules (ConvertUom no longer
        // short-circuits on a matching base unit). Wrapped in OBJECT_ID so it still enriches from
        // ocr.Material automatically if that table is ever re-created.
        result["materials"] = await db.QueryAsync(
            "IF OBJECT_ID('ocr.Material','U') IS NOT NULL " +
            "EXEC('SELECT cm.MaterialCodeSAP AS MaterialCode, cm.MaterialCodeSAP AS SapMaterialCode, " +
            "MAX(cm.MaterialCodeName) AS Description, MAX(m.Uom) AS Uom, MAX(m.Plant) AS Plant, MAX(m.MatGroup) AS MatGroup " +
            "FROM ocr.CustomerMaterial cm LEFT JOIN ocr.Material m ON m.MaterialCode = cm.MaterialCodeSAP " +
            "WHERE cm.Isactive=1 AND NULLIF(cm.MaterialCodeSAP,'''') IS NOT NULL " +
            "GROUP BY cm.MaterialCodeSAP ORDER BY cm.MaterialCodeSAP') " +
            "ELSE " +
            "EXEC('SELECT cm.MaterialCodeSAP AS MaterialCode, cm.MaterialCodeSAP AS SapMaterialCode, " +
            "MAX(cm.MaterialCodeName) AS Description, " +
            "CAST(NULL AS nvarchar(10)) AS Uom, CAST(NULL AS nvarchar(10)) AS Plant, CAST(NULL AS nvarchar(20)) AS MatGroup " +
            "FROM ocr.CustomerMaterial cm " +
            "WHERE cm.Isactive=1 AND NULLIF(cm.MaterialCodeSAP,'''') IS NOT NULL " +
            "GROUP BY cm.MaterialCodeSAP ORDER BY cm.MaterialCodeSAP')");
        result["vendors"] = await db.QueryAsync("SELECT * FROM ocr.Vendor WHERE IsActive=1 ORDER BY VendorCode");
        // ocr.Material (the old "apmaterials" master) has been dropped and is no longer loaded —
        // material data now comes solely from CustomerMaterial (the "materials" list above).
        result["venmaterials"] = await db.QueryAsync("SELECT * FROM ocr.VendorMaterial ORDER BY VendorCode, ExtCode");
        result["uoms"] = await db.QueryAsync(
            "SELECT * FROM ocr.UomConversion ORDER BY CASE WHEN MaterialCode IS NULL THEN 0 ELSE 1 END, MaterialCode, ExtUom");
        return result;
    }

    // Same data as LoadAllAsync(), reshaped into plain dicts for the mapping engine / SAP payload
    // builder (which need dict.get()-style field access, not raw dynamic Dapper rows).
    public async Task<MasterData> LoadForMappingAsync(string module = "SO")
    {
        var all = await LoadAllAsync();
        return new MasterData
        {
            Customers = DynamicRow.ToDictList(all["customers"]),
            ShipTos = DynamicRow.ToDictList(all["shiptos"]),
            // Both modules now use the CustomerMaterial-derived "materials" list (ocr.Material gone).
            Materials = DynamicRow.ToDictList(all["materials"]),
            CustomerMaterials = DynamicRow.ToDictList(all["custmaterials"]),
            Vendors = DynamicRow.ToDictList(all["vendors"]),
            VendorMaterials = DynamicRow.ToDictList(all["venmaterials"]),
            Uoms = DynamicRow.ToDictList(all["uoms"]),
        };
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

    // Save a CustomerMaterial together with its UoM-conversion rows in ONE transaction (used by the
    // "save material + its units from SAP" flow). UoM rows are de-duplicated on (MaterialCode,
    // ExtUom) so re-saving a material doesn't pile up duplicate rules. If anything fails the whole
    // save is rolled back — the CustomerMaterial is never persisted without its intended UoM rows.
    // Returns how many UoM rows were inserted vs skipped as already-present.
    public async Task<(int UomCreated, int UomSkipped)> CreateCustomerMaterialWithUomsAsync(
        Dictionary<string, object?> custMaterial, List<Dictionary<string, object?>> uomRows, CancellationToken ct = default)
    {
        var custDef = MasterDefinitions.All["custmaterials"];
        var uomDef = MasterDefinitions.All["uoms"];

        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await InsertRowAsync(conn, tx, custDef, custMaterial);

            int created = 0, skipped = 0;
            foreach (var row in uomRows)
            {
                var matCode = row.GetValueOrDefault("MaterialCode") as string;
                var extUom = (row.GetValueOrDefault("ExtUom") as string)?.Trim();
                if (string.IsNullOrEmpty(extUom)) continue; // ExtUom is the lookup key — skip blank rows

                // ISNULL('') keeps a global rule (MaterialCode NULL) and a material-specific rule
                // for the same ExtUom from colliding, matching how ConvertUom resolves them.
                var exists = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM ocr.UomConversion WHERE ISNULL(MaterialCode,'')=ISNULL(@mc,'') AND ExtUom=@eu",
                    new { mc = matCode, eu = extUom }, tx);
                if (exists > 0) { skipped++; continue; }

                await InsertRowAsync(conn, tx, uomDef, row);
                created++;
            }

            await tx.CommitAsync(ct);
            return (created, skipped);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // Same recognized-columns-only INSERT as CreateAsync, but on a caller-owned connection/transaction.
    private static async Task InsertRowAsync(SqlConnection conn, DbTransaction tx, MasterDefinition m, Dictionary<string, object?> body)
    {
        var cols = m.Cols.Where(body.ContainsKey).ToArray();
        if (cols.Length == 0) return;
        var sql = $"INSERT {m.Table}({string.Join(",", cols)}) VALUES({string.Join(",", cols.Select((_, i) => $"@p{i}"))})";
        var p = new DynamicParameters();
        for (var i = 0; i < cols.Length; i++) p.Add($"p{i}", body[cols[i]]);
        await conn.ExecuteAsync(sql, p, tx);
    }

    // masters_update(): SET only recognized columns present in the body, plus UpdatedAt.
    // Bug-for-bug with Python's masters_update (main.py:378-385, ", ".join(cols) + ", UpdatedAt=..."):
    // an empty/unrecognized body yields a leading comma ("SET , UpdatedAt=...") which SQL Server
    // rejects as a syntax error — a 500, not a validation error. Preserved deliberately, not fixed
    // (see the approved migration plan's bug-for-bug preservation policy).
    public async Task<bool> UpdateAsync(MasterDefinition m, string key, Dictionary<string, object?> body)
    {
        var cols = m.Cols.Where(body.ContainsKey).ToArray();
        if (cols.Length == 0) return false;
        var sets = string.Join(", ", cols.Select((c, i) => $"{c}=@p{i}")) + ", UpdatedAt=SYSDATETIME()";
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

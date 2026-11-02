using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

public record ZohoMaterial(string Id, string? Name, string? Code, string? MaterialGroup,
    // "Price and Unit Information" on the Material (manual 5.11.3): Unit = the big selling unit,
    // Conversion_Ratio = how many Sub_Unit (smallest unit) make one Unit. Filled by FindByCodeAsync.
    string? Unit = null, decimal? ConversionRatio = null, string? SubUnit = null);

/// <summary>
/// Zoho CRM "Materials" module (Zoho's standard Products module, relabelled) -- read-only lookup
/// of one material by its code (27 Oct 2026). Used for a non-WCD Sales Order line that is not one
/// of the Deal's own Items (Megachem: then look the Material up in Zoho directly), and for the
/// lines of a Deal the OCR flow opens itself. Field names from the manual (AO-CRM-UM-2026-003,
/// section 5.11.3, page 317): Product_Code (Material Code), Product_Name (Material Name),
/// Material_Group, Product_Active.
/// </summary>
public class ZohoMaterialClient(ZohoClient zoho)
{
    private string? _module;
    private readonly ConcurrentDictionary<string, ZohoMaterial?> _byCode = new(StringComparer.OrdinalIgnoreCase);

    private async Task<string> ResolveModuleAsync() =>
        _module ??= await ZohoRecordFields.ResolveModuleAsync(zoho, "Materials", "Material", "Products");

    /// <summary>Manual search in Zoho Materials by code or name (partial match), active ones first --
    /// used by the Material card when the document has no Deal or a non-WCD Deal (27 Oct 2026).</summary>
    public async Task<List<ZohoMaterial>> SearchAsync(string query, int top = 30)
    {
        var q = (query ?? "").Trim().Replace("'", "''");
        if (q.Length == 0) return [];
        var module = await ResolveModuleAsync();
        var where = $"(Product_Code like '%{q}%' or Product_Name like '%{q}%')";
        // Unit / Conversion_Ratio / Sub_Unit too (manual 5.11.3) so the card can show "1 DR = 160 KG";
        // without them if Zoho refuses those column names.
        List<JsonObject> rows;
        try
        {
            rows = await zoho.QueryCoqlAsync(module, "id, Product_Name, Product_Code, Material_Group, Product_Active, Unit, Conversion_Ratio, Sub_Unit",
                where, "Product_Code");
        }
        catch
        {
            rows = await zoho.QueryCoqlAsync(module, "id, Product_Name, Product_Code, Material_Group, Product_Active", where, "Product_Code");
        }
        static bool Active(JsonObject r) => r["Product_Active"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        return rows
            .OrderByDescending(Active)
            .Take(top)
            .Select(r => (Id: ZohoRecordFields.Str(r, "id"), Row: r))
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .Select(x => new ZohoMaterial(x.Id!, ZohoRecordFields.Str(x.Row, "Product_Name"),
                ZohoRecordFields.Str(x.Row, "Product_Code"), ZohoRecordFields.Str(x.Row, "Material_Group"),
                Unit: ZohoRecordFields.Str(x.Row, "Unit"), ConversionRatio: ZohoDealClient.Dec(x.Row, "Conversion_Ratio"),
                SubUnit: ZohoRecordFields.Str(x.Row, "Sub_Unit")))
            .ToList();
    }

    /// <summary>The one Material whose Material Code is exactly <paramref name="code"/>; null when
    /// none or more than one matches. An inactive Material is used only when it is the only hit.
    /// Results (also misses) are cached for the life of the process.</summary>
    public async Task<ZohoMaterial?> FindByCodeAsync(string code)
    {
        var clean = (code ?? "").Trim();
        if (clean.Length == 0) return null;
        if (_byCode.TryGetValue(clean, out var cached) && cached is not null) return cached;
        var module = await ResolveModuleAsync();
        var rows = await zoho.QueryCoqlAsync(module, "id, Product_Name, Product_Code, Material_Group, Product_Active",
            $"Product_Code = '{clean.Replace("'", "''")}'", "Product_Name");
        var hits = rows.Where(r => string.Equals((ZohoRecordFields.Str(r, "Product_Code") ?? "").Trim(), clean, StringComparison.OrdinalIgnoreCase)).ToList();
        var active = hits.Where(r => r["Product_Active"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b).ToList();
        var pick = active.Count == 1 ? active[0] : hits.Count == 1 ? hits[0] : null;
        var id = pick is null ? null : ZohoRecordFields.Str(pick, "id");
        if (string.IsNullOrEmpty(id)) return null;
        // Unit / Conversion_Ratio / Sub_Unit read from the full record (not COQL), so an unexpected field
        // name can never break the lookup itself -- the Sales Order line is sent in the Material's Unit.
        var full = await zoho.GetByIdAsync(module, id);
        var result = new ZohoMaterial(id, ZohoRecordFields.Str(pick!, "Product_Name"), ZohoRecordFields.Str(pick!, "Product_Code"),
            ZohoRecordFields.Str(pick!, "Material_Group"),
            Unit: full is null ? null : ZohoRecordFields.Str(full, "Unit")?.Trim() is { Length: > 0 } u ? u : null,
            ConversionRatio: full is null ? null : ZohoDealClient.Dec(full, "Conversion_Ratio"),
            SubUnit: full is null ? null : ZohoRecordFields.Str(full, "Sub_Unit")?.Trim() is { Length: > 0 } su ? su : null);
        if (result is not null) _byCode[clean] = result; // misses are not cached: the Material may be added later
        return result;
    }
}

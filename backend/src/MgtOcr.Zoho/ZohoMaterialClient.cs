using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MgtOcr.Zoho;

public record ZohoMaterial(string Id, string? Name, string? Code, string? MaterialGroup);

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
        var result = string.IsNullOrEmpty(id) ? null
            : new ZohoMaterial(id, ZohoRecordFields.Str(pick!, "Product_Name"), ZohoRecordFields.Str(pick!, "Product_Code"),
                ZohoRecordFields.Str(pick!, "Material_Group"));
        if (result is not null) _byCode[clean] = result; // misses are not cached: the Material may be added later
        return result;
    }
}

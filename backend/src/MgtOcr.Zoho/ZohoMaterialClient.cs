using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MgtOcr.Zoho;

public record ZohoMaterial(string Id, string? Name, string? Code, string? MaterialGroup,
    // "Price and Unit Information" on the Material (manual 5.11.3): Unit = the big selling unit,
    // Conversion_Ratio = how many Sub_Unit (smallest unit) make one Unit. Filled by FindByCodeAsync.
    string? Unit = null, decimal? ConversionRatio = null, string? SubUnit = null, string? Description = null);

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

    private static bool IsExcludedCode(string? code) =>
        code?.TrimStart().StartsWith("SMRM", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<string> ResolveModuleAsync() =>
        _module ??= await ZohoRecordFields.ResolveModuleAsync(zoho, "Materials", "Material", "Products");

    /// <summary>Search Zoho Materials (the Materials module) for the Material card / dropdown when the
    /// document has no Deal or a non-WCD Deal. Search order (Megachem, 10 Nov 2026), whatever the user
    /// types: Material Code first (Product_Code contains the text); only when no code matches, Material
    /// Name (Product_Name contains the text). Material Description is not searched.
    /// A whole PO line pasted in ("HPC - L (5 x 10 Kg.) กิโลกรัม(Nippon Soda / Japan)") is never one
    /// substring of a code or name, so if the text as typed finds nothing, its most distinctive words
    /// (code-like ones first) are tried the same way -- code first, then name -- and its two-word
    /// phrases on the name. Hits are ranked against the whole text (exact code first).</summary>
    public async Task<List<ZohoMaterial>> SearchAsync(string query, int top = 30)
    {
        var text = Regex.Replace(query ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return [];
        var module = await ResolveModuleAsync();

        var rows = new Dictionary<string, JsonObject>();
        var errors = new List<string>();
        var anySucceeded = false;
        const string baseCols = "id, Product_Name, Product_Code, Material_Group, Product_Active";
        const string unitCols = ", Unit, Conversion_Ratio, Sub_Unit";

        // One COQL query on one field; Unit / Conversion_Ratio / Sub_Unit too (manual 5.11.3) so the card
        // can show "1 DR = 160 KG", without them if Zoho refuses those column names. Errors are kept and
        // reported if every query failed, never turned into "nothing found".
        async Task<int> Find(string field, string term)
        {
            var where = $"{field} like '%{term.Replace("'", "''")}%'";
            List<JsonObject> found;
            try
            {
                try { found = await zoho.QueryCoqlAsync(module, baseCols + unitCols, where, "Product_Code"); }
                catch { found = await zoho.QueryCoqlAsync(module, baseCols, where, "Product_Code"); }
                anySucceeded = true;
            }
            catch (Exception e)
            {
                lock (errors) errors.Add(e.Message);
                return 0;
            }
            var added = 0;
            foreach (var r in found)
                if (!IsExcludedCode(ZohoRecordFields.Str(r, "Product_Code")) &&
                    ZohoRecordFields.Str(r, "id") is { Length: > 0 } id)
                    lock (rows) if (rows.TryAdd(id, r)) added++;
            return added;
        }

        // Code first; the name only when no code matched.
        async Task CodeThenName(IReadOnlyList<string> terms)
        {
            if (terms.Count == 0) return;
            var byCode = await Task.WhenAll(terms.Select(t => Find("Product_Code", t)));
            if (byCode.Sum() > 0) return;
            await Task.WhenAll(terms.Select(t => Find("Product_Name", t)));
        }

        var words = Words(text);
        if (words.Count <= 6) await CodeThenName([text]);
        if (rows.Count == 0) await CodeThenName(DistinctiveWords(text).Take(3).ToList());
        if (rows.Count == 0) await Task.WhenAll(KeyPhrases(text).Take(8).Select(t => Find("Product_Name", t)));

        if (!anySucceeded && errors.Count > 0)
            throw new InvalidOperationException("Zoho Materials search failed: " + errors[0]);

        static bool Active(JsonObject r) => r["Product_Active"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        return rows.Values
            .Select(r => (Row: r, Score: MatchScore(text, ZohoRecordFields.Str(r, "Product_Code"), ZohoRecordFields.Str(r, "Product_Name"))))
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => Active(x.Row))
            .ThenBy(x => ZohoRecordFields.Str(x.Row, "Product_Code"))
            .Take(top)
            .Select(x => new ZohoMaterial(ZohoRecordFields.Str(x.Row, "id")!, ZohoRecordFields.Str(x.Row, "Product_Name"),
                ZohoRecordFields.Str(x.Row, "Product_Code"), ZohoRecordFields.Str(x.Row, "Material_Group"),
                Unit: ZohoRecordFields.Str(x.Row, "Unit"), ConversionRatio: ZohoDealClient.Dec(x.Row, "Conversion_Ratio"),
                SubUnit: ZohoRecordFields.Str(x.Row, "Sub_Unit")))
            .ToList();
    }

    // Pack sizes / units / origin words that appear on almost every PO line and say nothing about
    // which Material it is -- never used on their own as the search word.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "kg", "kgs", "kilogram", "kilograms", "g", "gm", "ltr", "liter", "litre", "lt", "ml", "ton", "tons", "mt",
        "bag", "bags", "bg", "drum", "drums", "dr", "pail", "pl", "can", "cn", "box", "bx", "pc", "pcs", "ea", "set",
        "ibc", "tote", "carton", "ctn", "pallet", "loose", "lo", "std", "the", "and", "for", "with", "per", "x",
        // label words of a PO line ("PRODUCT NAME :", "ORIGIN :", "Specification :") -- not the product
        "product", "name", "chemical", "chemicals", "origin", "specification", "spec", "item", "code", "description",
        "grade", "type", "color", "colour", "value", "min", "max", "brand", "packing", "package", "qty", "quantity",
        "กิโลกรัม", "กก", "ถุง", "ถัง", "ลิตร", "กล่อง", "ชิ้น", "ตัน",
    };

    private static List<string> Words(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{M}\p{N}]+(?:-[\p{L}\p{M}\p{N}]+)*").Select(m => m.Value).ToList();

    // Two consecutive meaningful words, in the text's own order, each pair once ("tall oil", "fatty acid").
    internal static IEnumerable<string> KeyPhrases(string text)
    {
        var w = Words(text).Where(x => x.Length >= 2 && !NoiseWords.Contains(x) && !Regex.IsMatch(x, @"^[\d.,-]+$")).ToList();
        var seen = new HashSet<string>();
        for (var i = 0; i + 1 < w.Count; i++)
        {
            var phrase = $"{w[i]} {w[i + 1]}";
            if (w[i] != w[i + 1] && seen.Add(phrase)) yield return phrase;
        }
    }

    // Most useful single words to search on: code-like ones (letters + digits, e.g. "HYSU01-JP-BG-03",
    // "CA3000") first, then the longest. Pure numbers and noise words are skipped.
    internal static IEnumerable<string> DistinctiveWords(string text) =>
        Words(text)
            .Where(w => w.Length >= 3 && !NoiseWords.Contains(w) && !Regex.IsMatch(w, @"^[\d.,-]+$"))
            .Distinct()
            .OrderByDescending(w => Regex.IsMatch(w, @"\d") && Regex.IsMatch(w, @"\p{L}"))
            .ThenByDescending(w => w.Length);

    // How well a Material fits the typed / pasted text (higher = better): exact code, then the code or
    // the whole text found inside the other, then the share of meaningful words in common.
    internal static double MatchScore(string text, string? code, string? name)
    {
        string Compact(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"[^\p{L}\p{M}\p{N}]", "");
        var t = Compact(text);
        var c = Compact(code);
        var n = Compact(name);
        if (t.Length == 0) return 0;
        if (c.Length > 0 && c == t) return 1000;
        double score = 0;
        if (c.Length >= 4 && t.Contains(c)) score += 300;   // the pasted line carries the Material Code
        if (n.Length > 0 && (n.Contains(t) || (n.Length >= 6 && t.Contains(n)))) score += 200;
        var tw = Words(text).Where(w => !NoiseWords.Contains(w) && w.Length >= 2).Distinct().ToList();
        var nw = Words($"{code} {name}").Where(w => !NoiseWords.Contains(w) && w.Length >= 2).ToHashSet();
        if (tw.Count > 0 && nw.Count > 0)
        {
            var hit = tw.Count(w => nw.Contains(w) || (w.Length >= 4 && n.Contains(w)));
            score += 60.0 * hit / tw.Count + 40.0 * hit / nw.Count;
        }
        return score;
    }

    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Material Name (Product_Name) per Material Code, for lists that show many codes at once
    /// (the Unit Conversion master, 12 Nov 2026). COQL "Product_Code in (...)", 50 codes per query;
    /// names found are cached for the life of the process. Codes not found in Zoho are left out.</summary>
    public async Task<Dictionary<string, string>> GetNamesByCodesAsync(IEnumerable<string> codes)
    {
        var wanted = codes.Select(c => (c ?? "").Trim()).Where(c => c.Length > 0 && !IsExcludedCode(c))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var c in wanted)
            if (_names.TryGetValue(c, out var n)) result[c] = n; else missing.Add(c);
        if (missing.Count == 0) return result;
        var module = await ResolveModuleAsync();
        foreach (var chunk in missing.Chunk(50))
        {
            var list = string.Join(",", chunk.Select(c => $"'{c.Replace("'", "''")}'"));
            var rows = await zoho.QueryCoqlAsync(module, "id, Product_Code, Product_Name", $"Product_Code in ({list})", "Product_Code");
            foreach (var r in rows)
            {
                var code = ZohoRecordFields.Str(r, "Product_Code")?.Trim();
                var name = ZohoRecordFields.Str(r, "Product_Name")?.Trim();
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(name) || result.ContainsKey(code)) continue;
                result[code] = name;
                _names[code] = name;
            }
        }
        return result;
    }

    /// <summary>The one Material whose Material Code is exactly <paramref name="code"/>; null when
    /// none or more than one matches. An inactive Material is used only when it is the only hit.
    /// Results (also misses) are cached for the life of the process.</summary>
    public async Task<ZohoMaterial?> FindByCodeAsync(string code)
    {
        var clean = (code ?? "").Trim();
        if (clean.Length == 0 || IsExcludedCode(clean)) return null;
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
